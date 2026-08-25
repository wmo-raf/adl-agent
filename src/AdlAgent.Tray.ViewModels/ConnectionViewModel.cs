using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using AdlAgent.Core.Status;

namespace AdlAgent.Tray;

/// <summary>
/// One connection: a row in the list on the left, and the stations it shows
/// on the right when it is the one selected.
/// </summary>
/// <remarks>
/// The station list used to be flat, with the connection repeated down a
/// column of it, and that made a connection a value rather than a thing.
/// Two facts had nowhere to be said as a result. A connection ADL has
/// switched off arrived only as a false on each of its stations, so the
/// window blamed the stations; and a connection with no station links left no
/// trace at all, so an administrator who had made one and not yet linked to
/// it looked, from the machine, exactly like one who had done nothing. Both
/// are sentences on this object now.
/// <para>
/// It owns its rows rather than filtering a shared list, so moving between
/// connections changes which collection the grid is bound to and never
/// rebuilds a row. The alternative -- one collection, repopulated on every
/// click -- would destroy and restore the selected station each time somebody
/// looked at another connection, which is the churn
/// <see cref="ShellViewModel"/> already goes to lengths to avoid on the poll.
/// </para>
/// <para>
/// Nothing here is editable, and that is not an omission. Unlike the station
/// link beneath it, an ADL connection has no app-editable tier at all: every
/// field on it is HQ's. So this is a thing to read and to group by, and never
/// a thing to act on.
/// </para>
/// </remarks>
public sealed class ConnectionViewModel
{
    private readonly AgentConnectionSnapshot _connection;
    private readonly StationStanding _standing;

    public ConnectionViewModel(
        AgentConnectionSnapshot connection,
        IReadOnlyList<AgentStationSnapshot> stations,
        DateTimeOffset asOf)
    {
        _connection = connection;
        _standing = StationStanding.Of(stations);

        Stations = [.. stations.Select(station => new StationViewModel(station, asOf: asOf))];
    }

    /// <summary>This connection's rows, built once and outliving every click.</summary>
    public ObservableCollection<StationViewModel> Stations { get; }

    public long ConnectionId => _connection.ConnectionId;

    public string ConnectionName => _connection.ConnectionName;

    /// <summary>The ADL network this collects for, under the name.</summary>
    public string Network => _connection.Network;

    /// <summary>True when HQ has switched off the whole connection.</summary>
    public bool Enabled => _connection.Enabled;

    /// <summary>How many stations ADL has linked here, in words.</summary>
    public string StationCount => Stations.Count == 1
        ? "1 station"
        : string.Create(CultureInfo.CurrentCulture, $"{Stations.Count} stations");

    /// <summary>
    /// What this connection needs, in one line.
    /// </summary>
    /// <remarks>
    /// The point of the list existing. A pane that showed only names would
    /// make a technician click every connection in turn to find out whether
    /// there was anything in it for them -- strictly worse than the single
    /// grid it replaced, where a problem was visible without navigating. So
    /// each row carries enough to say "there is nothing for you in here".
    /// <para>
    /// Below the connection's own state, the wording is the standing's, and
    /// the standing is the same function that writes the line at the top of
    /// the window. The two are on screen together and cannot be allowed to
    /// disagree.
    /// </para>
    /// </remarks>
    public string Standing
    {
        get
        {
            if (!Enabled)
            {
                return "Switched off in ADL";
            }

            return _standing.Kind switch
            {
                StandingKind.NoStations => "No stations linked",
                StandingKind.BindAFolder => Needing(_standing.Unbound.Count, "a folder"),
                StandingKind.FixAStation => Reporting(_standing.Failing.Count),
                StandingKind.Quiet => Silent(_standing.Quiet.Count),
                StandingKind.AllSwitchedOff => "Every station switched off in ADL",
                _ => "Collecting",
            };
        }
    }

    /// <summary>
    /// The colour beside the name.
    /// </summary>
    /// <remarks>
    /// A switched-off connection is <see cref="TrayState.Working"/> rather
    /// than amber on purpose. It is an administrator's deliberate choice,
    /// there is nothing on this machine to fix, and a warning colour would
    /// send a technician hunting for a fault that does not exist.
    /// </remarks>
    public TrayState Attention => Enabled ? _standing.Attention : TrayState.Working;

    /// <summary>True when this connection has no rows to draw.</summary>
    public bool HasNoStations => Stations.Count == 0;

    /// <summary>
    /// Why this connection's grid is empty, in the words of the reason it is.
    /// </summary>
    /// <remarks>
    /// Two different problems wanting two different people. "ADL has not
    /// linked any stations to this connection yet" sends somebody to their
    /// administrator; "this connection is switched off" tells them the
    /// folders on this machine are fine and to stop looking. A single
    /// hardcoded sentence in the window would have been one of them, and the
    /// other would never have been written.
    /// </remarks>
    public string NoStationsReason => Enabled
        ? "ADL has not linked any stations to this connection yet. Your ADL administrator does "
            + "that in the ADL admin; this window updates on its own, so there is nothing to "
            + "press here."
        : "This connection is switched off in ADL, so nothing under it is being scanned or sent. "
            + "Nothing on this machine needs changing.";

    private static string Needing(int count, string what) => count == 1
        ? $"1 station needs {what}"
        : string.Create(CultureInfo.CurrentCulture, $"{count} stations need {what}");

    /// <summary>
    /// A station that failed, and said so.
    /// </summary>
    /// <remarks>
    /// It used to read "collected nothing", which was accurate and is now
    /// taken: the rung below says the same thing about a station that failed
    /// at nothing. Two neighbouring rungs whose sentences a reader cannot
    /// tell apart is two rungs that might as well be one, so this one is
    /// worded from what distinguishes it -- there is an error, and it is on
    /// the row.
    /// </remarks>
    private static string Reporting(int count) => count == 1
        ? "1 station reported a problem"
        : string.Create(CultureInfo.CurrentCulture, $"{count} stations reported a problem");

    /// <summary>A station that is configured, blaming nothing, and silent.</summary>
    private static string Silent(int count) => count == 1
        ? "1 station has sent nothing"
        : string.Create(CultureInfo.CurrentCulture, $"{count} stations have sent nothing");
}
