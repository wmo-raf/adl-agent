using AdlAgent.Core.Update;

namespace AdlAgent.Core.Platform;

/// <summary>
/// Platform seam 5 of 5: replacing the agent with a newer one.
/// </summary>
/// <remarks>
/// Deciding <em>whether</em> to update is the core's, and it is the same
/// decision everywhere: ask ADL what this machine should run, refuse a
/// package whose bytes do not hash to what ADL said, and act only on a
/// version newer than the running one. Carrying it out is not portable at
/// all. On Windows it is Windows Installer performing a major upgrade of a
/// service, or Velopack swapping a per-user install; on Linux it will be a
/// package manager or a unit file pointing at a new directory. None of that
/// is expressible once.
/// <para>
/// The seam also answers <em>which</em> package this install takes, because
/// that is the same question: an install knows how it was installed, and
/// nothing else on the machine reliably does. Making it a fact the head
/// reports rather than a setting somebody types is what keeps a per-user
/// install from being handed an MSI it has no administrator rights to run.
/// </para>
/// </remarks>
public interface IUpdateInstaller
{
    /// <summary>
    /// Which tier this install is -- <see cref="UpdateTiers.Service"/> or
    /// <see cref="UpdateTiers.User"/>. Travels with every update check, and
    /// decides which package ADL offers.
    /// </summary>
    string Tier { get; }

    /// <summary>
    /// True when this install is one the agent may replace by itself.
    /// </summary>
    /// <remarks>
    /// False for a copy somebody unzipped into a folder, or a developer's
    /// <c>dotnet run</c>: there is no install for a package to upgrade, and
    /// running an MSI against such a machine would put a second, real
    /// install beside the one being debugged. Those machines are told that a
    /// newer version exists and left alone.
    /// </remarks>
    bool CanApply { get; }

    /// <summary>
    /// Install <paramref name="update"/>, replacing this agent.
    /// </summary>
    /// <remarks>
    /// Expected not to return in the ordinary case: the process being
    /// replaced is this one, and both Windows paths stop the service or the
    /// app as their first act. A caller must therefore have finished
    /// everything it cared about before calling, and must treat "this
    /// returned normally" as "the update did not happen yet", not as
    /// success.
    /// </remarks>
    /// <exception cref="UpdateFailedException">
    /// The package could not be handed to the platform's installer at all.
    /// </exception>
    Task ApplyAsync(DownloadedUpdate update, CancellationToken cancellationToken = default);
}

/// <summary>Handing the package to the platform's installer did not work.</summary>
/// <remarks>
/// Its own type so that the loop above can tell it from a bad download and
/// say so: a machine that cannot fetch its update has a link problem, and a
/// machine that cannot run the one it fetched has an install problem, and
/// they are fixed by different people.
/// </remarks>
public sealed class UpdateFailedException : Exception
{
    public UpdateFailedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
