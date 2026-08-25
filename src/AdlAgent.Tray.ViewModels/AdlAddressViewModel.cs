using System;
using System.Threading.Tasks;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Tray;

/// <summary>
/// The dialog behind Change… on the ADL row: an address, one box, and the
/// button that raises Windows' consent prompt.
/// </summary>
/// <remarks>
/// Deliberately thin. Everything that decides anything about a repoint --
/// what a usable address is, what becomes of the token, when the service
/// comes back -- is the <c>adl-agent set-url</c> verb, so that a machine with
/// no desktop and a machine with one behave identically. What is decided here
/// is only what a window decides: what to send, when the button is worth
/// pressing, and what to say about each of the three answers.
/// <para>
/// It holds no settings of its own beyond the two in front of somebody.
/// Closing without saving is dropping this object.
/// </para>
/// </remarks>
public sealed class AdlAddressViewModel : Observable
{
    private readonly ShellViewModel _shell;

    /// <summary>Where this machine reports now, as the dialog opened.</summary>
    private readonly string _current;

    private string _address;
    private bool _keepPairing;

    public AdlAddressViewModel(ShellViewModel shell, string current)
    {
        _shell = shell;
        _current = current;
        _address = current;

        SaveCommand = new AsyncCommand(() => SaveAsync(), Failed, () => HasChanges);
    }

    /// <summary>Raised once Windows has answered, with what it answered.</summary>
    /// <remarks>
    /// An event rather than a return value the window awaits, so the button
    /// can stay an <see cref="AsyncCommand"/> -- which is what stops a second
    /// consent prompt being raised behind the first.
    /// </remarks>
    public event EventHandler<AddressChangeOutcome>? Saved;

    public AsyncCommand SaveCommand { get; }

    public string Title => "Where this machine reports";

    /// <summary>The address, as it is being typed.</summary>
    public string Address
    {
        get => _address;
        set
        {
            if (!Set(ref _address, value))
            {
                return;
            }

            // Whatever the last answer was, it was about the address as it
            // was, and it is not that one any more.
            _shell.Message = "";

            SaveCommand.Refresh();

            Raise(nameof(HasChanges));
            Raise(nameof(Answer));
        }
    }

    /// <summary>
    /// True when somebody has said this is the same ADL at a new address.
    /// </summary>
    /// <remarks>
    /// Off by default, and that is the whole of the safety here: a token
    /// issued by one instance means nothing to another, so keeping it across
    /// a real move would leave a machine holding a credential ADL will refuse
    /// and a technician reading "revoked" about something nobody revoked.
    /// The one case it is right for -- an instance that has changed domain --
    /// is a thing somebody knows and this window cannot.
    /// </remarks>
    public bool KeepPairing
    {
        get => _keepPairing;
        set
        {
            if (Set(ref _keepPairing, value))
            {
                Raise(nameof(Consequence));
            }
        }
    }

    /// <summary>
    /// What ticking or leaving the box will cost, said before it costs it.
    /// </summary>
    /// <remarks>
    /// A checkbox whose label says what it does and not what it means would
    /// leave a technician to discover, after the service has restarted, that
    /// a working machine has stopped sending and wants a code nobody in the
    /// building has. Both readings are stated because both have a
    /// consequence, and neither is a mistake.
    /// </remarks>
    public string Consequence => KeepPairing
        ? "This machine will keep the pairing it has. If the new ADL refuses the token, "
            + "the Status tab will ask for a pairing code."
        : "This machine's pairing will be cleared, so nothing will be sent until you pair this "
            + "machine again with a code from the new ADL.";

    /// <summary>True when there is a different address in the box.</summary>
    /// <remarks>
    /// Different, rather than merely present. A prompt raised to write the
    /// address that is already there costs an administrator's password for
    /// nothing -- and, with the box above unticked, would unpair a working
    /// machine on the way.
    /// </remarks>
    public bool HasChanges =>
        Address.Trim().Length > 0 && !string.Equals(Address.Trim(), _current.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// The line along the bottom: what happened, or why the button beside it
    /// is grey.
    /// </summary>
    /// <remarks>
    /// The shell's message rather than a copy, for the same reason the
    /// station settings window reads it: a refusal is read here, in front of
    /// the other window, and a success is read there, because by then this
    /// one has closed. One string either way, so the two cannot disagree.
    /// </remarks>
    public string Answer => _shell.Message.Length > 0
        ? _shell.Message
        : HasChanges ? "" : "Type the address of the ADL this machine should report to.";

    /// <summary>Ask Windows, and say what it answered.</summary>
    public async Task<AddressChangeOutcome> SaveAsync()
    {
        var outcome = await _shell.ChangeAdlAddressAsync(Address, KeepPairing).ConfigureAwait(true);

        Raise(nameof(Answer));

        Saved?.Invoke(this, outcome);

        return outcome;
    }

    /// <summary>
    /// The dialog is over, however it ended.
    /// </summary>
    /// <remarks>
    /// Called from the window's <c>OnClosed</c> rather than from Save or
    /// Cancel, because the ways a window closes include the ones nobody wrote
    /// a handler for. Leaving it uncalled would leave the station list behind
    /// frozen for as long as the tray runs.
    /// </remarks>
    public void Done() => _shell.EndEditing();

    private void Failed(Exception exception)
    {
        _shell.Failed(exception);

        Raise(nameof(Answer));
    }
}
