using AdlAgent.TestSupport;
using AdlAgent.Windows.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdlAgent.Core.Tests;

/// <summary>
/// A harness with its control surface actually served, and a typed link to
/// it.
/// </summary>
/// <remarks>
/// The transport is real -- a named pipe on Windows, a unix socket elsewhere
/// -- with the real control service on the other end and the fake ADL behind
/// that. What is under test through this is the conversation a technician's
/// window actually has, rather than a window talking to a substitute for the
/// thing it talks to.
/// <para>
/// Each instance serves on a name of its own, so a suite running in parallel
/// neither collides with itself nor cares whether this machine has a real
/// agent installed.
/// </para>
/// </remarks>
internal sealed class ServedAgent : IDisposable
{
    private readonly CancellationTokenSource _stopping;
    private readonly Task _serving;

    private ServedAgent(AgentControlLink link, CancellationTokenSource stopping, Task serving)
    {
        Link = link;
        _stopping = stopping;
        _serving = serving;
    }

    /// <summary>What a local UI asks this agent through.</summary>
    public AgentControlLink Link { get; }

    /// <summary>Start serving, and wait until the surface is listening.</summary>
    public static async Task<ServedAgent> ServingAsync(AgentHarness agent)
    {
        // Short: a pipe name becomes a unix socket path off Windows, and that
        // path has 104 characters to play with including the temp directory.
        var name = $"adl-u{Guid.NewGuid():N}"[..13];

        var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var surface = new NamedPipeControlSurface(
            NullLogger<NamedPipeControlSurface>.Instance, name);

        var serving = surface.ServeAsync(agent.ControlService.HandleAsync, stopping.Token);

        var link = new AgentControlLink(
            () => new NamedPipeControlClient(TimeSpan.FromSeconds(10), name));

        // The surface binds its first pipe instance before it will accept
        // anything; asking before that is a race, not a failure.
        await WaitUntilListeningAsync(link);

        return new ServedAgent(link, stopping, serving);
    }

    /// <summary>A link to a surface nobody is serving: the tray's commonest bad day.</summary>
    public static AgentControlLink NothingServing() =>
        new(() => new NamedPipeControlClient(
            TimeSpan.FromMilliseconds(200), $"adl-x{Guid.NewGuid():N}"[..13]));

    public void Dispose()
    {
        _stopping.Cancel();

        try
        {
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stopping.Dispose();
    }

    private static async Task WaitUntilListeningAsync(AgentControlLink link)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if ((await link.StatusAsync()).ServiceReached)
            {
                return;
            }

            await Task.Delay(10);
        }
    }
}
