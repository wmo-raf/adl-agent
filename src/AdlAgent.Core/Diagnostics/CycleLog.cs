using System.Globalization;
using System.Text.Json;
using AdlAgent.Core.Platform;
using AdlAgent.Core.Serialization;
using Microsoft.Extensions.Options;

namespace AdlAgent.Core.Diagnostics;

/// <summary>
/// This machine's record of what its collection actually did, one JSON line
/// per unit pass.
/// </summary>
/// <remarks>
/// Until this existed, nothing the agent did survived the cycle that did it.
/// The counts were held in memory for the heartbeat and overwritten by the
/// next pass; the sentence that says <em>why</em> a silent station is silent
/// was computed, written into a tally, and thrown away ten minutes later. When
/// somebody asks what happened at 13:24, this is the only thing on the machine
/// that can answer.
/// <para>
/// Its own file and its own ceiling, kept apart from the general log
/// deliberately. Interleaving the two into one chronological stream reads
/// better -- a failed upload beside the exception that caused it -- but it
/// puts one ceiling over data with wildly different value per byte, and the
/// loser of that arrangement is always the cycle history.
/// </para>
/// </remarks>
public sealed class CycleLog : ILogFlush, IAsyncDisposable
{
    private readonly BackgroundLogQueue? _queue;
    private readonly string _instance;

    /// <summary>The file extension this log's files carry.</summary>
    public const string Extension = ".jsonl";

    public CycleLog(IOptions<AgentOptions> options, IHostLifecycle host, TimeProvider time)
        : this(
            AgentLogs.In(options.Value.ResolveStateDirectory(host)),
            options.Value.CycleLogMegabytes,
            options.Value.AdlBaseUrl,
            time)
    {
    }

    /// <param name="instance">
    /// The ADL these records are written against, stamped on every one of
    /// them. A repoint deliberately leaves this folder alone, so a log can
    /// hold records from two instances -- and station link ids the newer one
    /// has issued to entirely different stations.
    /// </param>
    public CycleLog(string directory, int megabytes, string instance, TimeProvider time)
    {
        Directory = directory;
        _instance = instance;

        try
        {
            _queue = new BackgroundLogQueue(
                new BoundedLogWriter(
                    directory, AgentLogs.CycleLogName, Extension, (long)megabytes * 1024 * 1024, time),
                static dropped => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"dropped\":{dropped},\"note\":\"unit passes this log could not keep up with\"}}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A machine whose logs folder cannot be made still collects. The
            // diagnostic is the thing that is meant to survive a bad day, and
            // it must never be the reason there is one.
            _queue = null;
        }
    }

    /// <summary>Where this log's files are.</summary>
    public string Directory { get; }

    /// <summary>Leave one unit pass on the disk.</summary>
    /// <remarks>
    /// Returns as soon as the record is handed over. Nothing here touches a
    /// file: the caller is a cycle thread, and a cycle thread that waited on
    /// a share which had just stopped answering would stop a country's
    /// observations for a diagnostic.
    /// </remarks>
    public void Write(CycleRecord record)
    {
        if (_queue is null)
        {
            return;
        }

        try
        {
            _queue.Write(JsonSerializer.Serialize(record with { Instance = _instance }, AgentJson.Options));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
        }
    }

    /// <summary>Wait until everything written so far has reached the disk.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _queue?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => _queue?.DisposeAsync() ?? ValueTask.CompletedTask;
}
