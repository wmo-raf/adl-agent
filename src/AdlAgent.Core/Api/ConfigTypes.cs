namespace AdlAgent.Core.Api;

/// <summary>
/// ADL's answer to a config write: what now stands, and at which version.
/// </summary>
/// <remarks>
/// There is no conflict answer to handle. Writes to the shared tier are
/// last-write-wins by decision (#260), so ADL never refuses one for being
/// stale -- it applies it and says what the station's settings now are. An
/// agent whose cached version has since moved simply re-reads, which is what
/// <see cref="ConfigVersion"/> is for: a tray that watches it move has seen
/// its own write land, and an administrator watching the admin sees the same
/// number.
/// </remarks>
public sealed record ConfigWriteResponse
{
    public long StationLinkId { get; init; }

    public long ConfigVersion { get; init; }

    /// <summary>The whole app tier as it now stands, not only what changed.</summary>
    public StationLinkAppConfig Config { get; init; } = new();
}
