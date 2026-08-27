using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// <see cref="ILogger"/> output, on the disk, on both tiers.
/// </summary>
/// <remarks>
/// Until this existed the agent registered no logging provider at all, so on
/// the service tier <c>ILogger</c> output went to the Windows Event Log --
/// findable, unstructured, and interleaved with everything else on the
/// machine -- and on the per-user tray tier it went to a console window that
/// closes. That tier had no durable record of anything: not a crash, not a
/// TLS failure, not an unhandled exception. This is its first.
/// <para>
/// Hand-rolled, and that is a decision rather than an omission. The agent's
/// whole supply chain is five <c>Microsoft.Extensions.*</c> packages and
/// Velopack, on a binary installed inside 26 government networks, and the
/// rolling, gzipping and eviction machinery already had to exist for the
/// cycle log. Serilog and <c>NReco.Logging.File</c> were both considered and
/// rejected on that basis -- and each would also have brought a second,
/// independent retention model that knows nothing about the cycle log's
/// ceiling.
/// </para>
/// <para>
/// Constructed by the head rather than resolved from the container. A logging
/// provider is built while logging is being configured, before the service
/// provider exists, so it owns its own writer and its own queue -- which is
/// also what gives the two logs their two independent ceilings.
/// </para>
/// </remarks>
public sealed class AgentFileLoggerProvider : ILoggerProvider, ILogFlush
{
    /// <summary>The file extension this log's files carry.</summary>
    public const string Extension = ".log";

    private readonly BackgroundLogQueue? _queue;
    private readonly LogLevel _minimum;
    private readonly TimeProvider _time;

    public AgentFileLoggerProvider(
        string directory, int megabytes, LogLevel minimum, TimeProvider time)
    {
        _minimum = minimum;
        _time = time;
        Directory = directory;

        try
        {
            _queue = new BackgroundLogQueue(
                new BoundedLogWriter(
                    directory, AgentLogs.GeneralLogName, Extension, (long)megabytes * 1024 * 1024, time),
                static dropped => $"[{dropped} log lines were dropped: this log could not keep up]");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _queue = null;
        }
    }

    /// <summary>Where this log's files are.</summary>
    public string Directory { get; }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(Shortened(categoryName), _queue, _minimum, _time);

    /// <summary>
    /// Wait until everything logged so far has reached the disk.
    /// </summary>
    /// <remarks>
    /// Called before a diagnostics bundle is read off these same files, so
    /// that the last thing that happened before somebody pressed the button
    /// is in the thing they are about to email.
    /// </remarks>
    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _queue?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    public void Dispose() => _queue?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// The category without the namespace it shares with every other one.
    /// </summary>
    /// <remarks>
    /// <c>AdlAgent.Core.Cycle.UploadCycle</c> is 34 characters of which 15 are
    /// the same on every line in the file. What is left is what a person
    /// reading the file is actually using to find the lines they want.
    /// </remarks>
    private static string Shortened(string category) =>
        category.StartsWith("AdlAgent.", StringComparison.Ordinal)
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

    /// <summary>One category, writing through the provider's queue.</summary>
    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly BackgroundLogQueue? _queue;
        private readonly LogLevel _minimum;
        private readonly TimeProvider _time;

        public FileLogger(
            string category, BackgroundLogQueue? queue, LogLevel minimum, TimeProvider time)
        {
            _category = category;
            _queue = queue;
            _minimum = minimum;
            _time = time;
        }

        /// <summary>
        /// Scopes are not kept.
        /// </summary>
        /// <remarks>
        /// Nothing in this agent opens one, and a null scope provider is the
        /// documented way to say so. If something ever does, this is where it
        /// would be answered.
        /// </remarks>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            _queue is not null && logLevel >= _minimum && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder()
                .Append(_time.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append("Z  ")
                .Append(Level(logLevel))
                .Append("  ")
                .Append(_category)
                .Append("  ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                // In full, on its own lines. The stack trace is the reason
                // anybody opens this file, and a log that summarised it would
                // send a technician back to a machine they can no longer
                // reach.
                line.Append(Environment.NewLine).Append(exception);
            }

            _queue!.Write(line.ToString());
        }

        /// <summary>
        /// The level, padded so the columns line up in a text editor.
        /// </summary>
        /// <remarks>
        /// Five characters, which is what the two levels anybody reads --
        /// <c>WARN</c> and <c>ERROR</c> -- need between them. <c>Warning</c>
        /// is written <c>WARN</c> so that the file can be grepped for the
        /// spelling every other log on the machine uses.
        /// </remarks>
        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "     ",
        };
    }
}
