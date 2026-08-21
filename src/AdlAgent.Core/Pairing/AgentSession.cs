using AdlAgent.Core.Api;
using AdlAgent.Core.State;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Pairing;

/// <summary>
/// This machine's standing with ADL: paired or not, and with which token.
/// </summary>
/// <remarks>
/// One object owns this, and every loop asks it rather than keeping its own
/// copy, because the states it holds are the states the whole agent obeys.
/// When ADL refuses the token, the scan cycle must stop offering files, the
/// heartbeat must stop beating, and the tray must say "re-pair needed" -- and
/// those three things happening at slightly different times, from three
/// remembered copies of the same fact, is exactly the class of bug this
/// product exists to stop shipping to countries.
/// </remarks>
public sealed class AgentSession
{
    private readonly IAdlApiClient _client;
    private readonly IAgentStateStore _store;
    private readonly ILogger<AgentSession> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private AgentState _state;

    public AgentSession(
        IAdlApiClient client,
        IAgentStateStore store,
        TimeProvider time,
        ILogger<AgentSession> logger)
    {
        _client = client;
        _store = store;
        _time = time;
        _logger = logger;
        _state = store.Load();
    }

    public PairingState State
    {
        get
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(_state.Token))
                {
                    return PairingState.Unpaired;
                }

                return _state.RePairNeeded ? PairingState.RePairNeeded : PairingState.Paired;
            }
        }
    }

    public DeviceSummary? Device
    {
        get
        {
            lock (_gate)
            {
                return _state.Device;
            }
        }
    }

    public DateTimeOffset? PairedAt
    {
        get
        {
            lock (_gate)
            {
                return _state.PairedAt;
            }
        }
    }

    /// <summary>
    /// The token to call ADL with, or <c>null</c> when this machine has no
    /// business calling.
    /// </summary>
    /// <remarks>
    /// A revoked token is withheld rather than handed out with a flag: this
    /// is the single point where "stop uploading" is enforced, so a caller
    /// that forgot to check gets nothing to call with instead of a 401 per
    /// file.
    /// </remarks>
    public string? ActiveToken
    {
        get
        {
            lock (_gate)
            {
                return _state.RePairNeeded ? null : NullIfBlank(_state.Token);
            }
        }
    }

    /// <summary>
    /// Redeem a pairing code and become paired.
    /// </summary>
    /// <remarks>
    /// Re-pairing a machine that is already paired is normal and allowed --
    /// it is what an administrator's token rotation asks the technician to
    /// do -- and it clears the re-pair state that sent them to the tray in
    /// the first place.
    /// </remarks>
    /// <exception cref="AdlRequestException">The code was not redeemable.</exception>
    /// <exception cref="AdlUnreachableException">ADL could not be reached.</exception>
    public async Task<DeviceSummary> PairAsync(string pairingCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pairingCode))
        {
            throw new AdlRequestException(
                System.Net.HttpStatusCode.BadRequest,
                "invalid_pairing_code",
                "Enter the pairing code from the ADL admin.");
        }

        var paired = await _client.PairAsync(pairingCode.Trim(), cancellationToken).ConfigureAwait(false);

        Store(new AgentState
        {
            Token = paired.Token,
            Device = paired.Device,
            RePairNeeded = false,
            PairedAt = _time.GetUtcNow(),
        });

        _logger.LogInformation(
            "Paired with ADL as device {DeviceName} (#{DeviceId}).",
            paired.Device.Name, paired.Device.Id);

        return paired.Device;
    }

    /// <summary>
    /// Record that ADL refused this token.
    /// </summary>
    /// <remarks>
    /// The token is kept rather than deleted. It is worthless to ADL, but it
    /// is the evidence that this machine was once paired, which is what lets
    /// the tray say "re-pair needed" instead of "not set up" to a technician
    /// who is about to be told nothing was ever configured here.
    /// </remarks>
    public void MarkRevoked()
    {
        AgentState revoked;

        lock (_gate)
        {
            if (_state.RePairNeeded || string.IsNullOrEmpty(_state.Token))
            {
                // Nothing to lose and nothing to say: an unpaired machine
                // getting a 401 is not news, and saying it twice would put a
                // second transition in the log for one revocation.
                return;
            }

            revoked = _state with { RePairNeeded = true };
            _state = revoked;
        }

        _store.Save(revoked);

        _logger.LogError(
            "ADL no longer accepts this device's token. Sending has stopped until the machine is paired again.");
    }

    private void Store(AgentState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        _store.Save(state);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
