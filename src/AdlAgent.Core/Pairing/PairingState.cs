namespace AdlAgent.Core.Pairing;

/// <summary>Where this install stands with its ADL instance.</summary>
public enum PairingState
{
    /// <summary>Installed, never paired. Nothing is sent and nothing is wrong.</summary>
    Unpaired,

    /// <summary>Paired, with a token ADL has not refused.</summary>
    Paired,

    /// <summary>
    /// Paired once, and ADL has since refused the token. The distinction from
    /// <see cref="Unpaired"/> is the whole point: this machine was working
    /// and has stopped, which is something a technician needs told rather
    /// than something they should discover from missing data.
    /// </summary>
    RePairNeeded,
}
