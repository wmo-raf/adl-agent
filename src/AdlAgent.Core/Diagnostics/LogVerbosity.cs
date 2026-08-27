using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// How much this machine writes to its log, and who last had a say in it.
/// </summary>
/// <remarks>
/// Two settings, one answer. The machine's own -- read from its settings file
/// at start-up -- is what a technician standing at it can change, and it has
/// to keep working on a machine that cannot reach ADL at all, which is most
/// of the machines anybody wants a log from. ADL's is what HQ can raise
/// without reaching the machine, which is the whole point of
/// wmo-raf/adl#307: putting a country server on <c>Debug</c> for a day
/// otherwise means finding somebody in-country to edit a file.
/// <para>
/// ADL's wins when ADL has one, and its absence is not a level -- it means
/// the local setting stands. So clearing the field in the admin gives the
/// machine back to whoever is standing at it, rather than pinning it to a
/// default HQ never chose.
/// </para>
/// <para>
/// A word nobody can parse is the same as no word at all. This arrives from a
/// text field on a form and from a file somebody edited over a telephone, and
/// the answer to a typo is the level below it rather than silence.
/// </para>
/// </remarks>
public sealed class LogVerbosity
{
    private readonly Lock _gate = new();

    private LogLevel _local = LogLevel.Information;
    private LogLevel? _remote;

    /// <summary>What the machine itself was set to.</summary>
    /// <remarks>
    /// Settled once, by the head, out of the same options the file logger is
    /// built from. Everything after that is ADL's to say.
    /// </remarks>
    public LogLevel Local
    {
        get
        {
            lock (_gate)
            {
                return _local;
            }
        }
    }

    /// <summary>True when ADL is currently overruling the machine's setting.</summary>
    public bool Overridden
    {
        get
        {
            lock (_gate)
            {
                return _remote is not null;
            }
        }
    }

    /// <summary>The level in force.</summary>
    public LogLevel Effective
    {
        get
        {
            lock (_gate)
            {
                return _remote ?? _local;
            }
        }
    }

    /// <summary>
    /// Raised when <see cref="Effective"/> moves, and only then.
    /// </summary>
    /// <remarks>
    /// The logging pipeline is built once at start-up and re-reads its rules
    /// only when something tells it to; this is what tells it. Only on a real
    /// move, because every sync carries the field and rebuilding every
    /// logger's filter every cycle would be a cost paid by every machine for
    /// a setting almost none of them use.
    /// </remarks>
    public event Action? Changed;

    /// <summary>Say what the machine itself was set to.</summary>
    public void SetLocal(string? asked)
    {
        var level = Parse(asked) ?? LogLevel.Information;

        Announce(() =>
        {
            _local = level;
        });
    }

    /// <summary>Take ADL's word for it, or give the machine back its own.</summary>
    /// <returns>True when the level in force moved.</returns>
    public bool Adopt(string? asked)
    {
        var level = Parse(asked);

        return Announce(() =>
        {
            _remote = level;
        });
    }

    /// <summary>
    /// A level name, or null when there is nothing usable to read.
    /// </summary>
    /// <remarks>
    /// <c>None</c> is refused along with the nonsense. It parses, and it means
    /// "log nothing at all" -- which is a machine that has silently stopped
    /// keeping the only evidence anybody will have of its next bad day, set
    /// from a form on another continent. There is no support case that wants
    /// it, and the ceiling already makes a chatty machine harmless.
    /// </remarks>
    private static LogLevel? Parse(string? asked) =>
        !string.IsNullOrWhiteSpace(asked) &&
        Enum.TryParse<LogLevel>(asked.Trim(), ignoreCase: true, out var parsed) &&
        parsed != LogLevel.None
            ? parsed
            : null;

    private bool Announce(Action change)
    {
        LogLevel before;
        LogLevel after;

        lock (_gate)
        {
            before = _remote ?? _local;
            change();
            after = _remote ?? _local;
        }

        if (before == after)
        {
            return false;
        }

        // Outside the lock. A handler rebuilds the logging pipeline, which
        // logs, and a logger reaching back into this while it is held is a
        // deadlock in the one subsystem that has to keep working when
        // everything else has stopped.
        Changed?.Invoke();

        return true;
    }
}
