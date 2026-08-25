using System;
using System.Windows;
using AdlAgent.Windows.Platform;

namespace AdlAgent.Tray;

/// <summary>
/// Where this machine's ADL address is changed.
/// </summary>
/// <remarks>
/// Modal, like the station settings window and for the same reason: while it
/// is open the list behind it stops moving, and dropping this window is what
/// Cancel means.
/// <para>
/// What is in this file is the two things that are not decisions: putting the
/// cursor in the box, and closing on the one answer that finishes this
/// window. What to send, what to say, and which answers leave it open are
/// next door in <see cref="AdlAddressViewModel"/>, where a test can reach
/// them.
/// </para>
/// </remarks>
public partial class AdlAddressWindow : Window
{
    private readonly AdlAddressViewModel _address;

    public AdlAddressWindow(AdlAddressViewModel address)
    {
        _address = address;

        InitializeComponent();

        DataContext = address;

        address.Saved += Finished;
    }

    /// <summary>
    /// The cursor starts in the address, at the end of what is already there.
    /// </summary>
    /// <remarks>
    /// A technician opening this is here to change one string, and the
    /// commonest edit is to an address that is nearly right. Selecting it all
    /// would make the first keystroke delete a hostname somebody wanted to
    /// keep.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        AddressBox.Focus();
        AddressBox.CaretIndex = AddressBox.Text.Length;
    }

    protected override void OnClosed(EventArgs e)
    {
        _address.Saved -= Finished;

        // Whatever happened, the list behind this window may move again.
        _address.Done();

        base.OnClosed(e);
    }

    /// <summary>
    /// A change that went through is this window being finished. Everything
    /// else -- an address refused before the prompt, a prompt declined, a verb
    /// that could not finish -- leaves it open over the line saying so, because
    /// what is on the screen is the thing that has to change.
    /// </summary>
    private void Finished(object? sender, AddressChangeOutcome outcome)
    {
        if (outcome != AddressChangeOutcome.Changed)
        {
            return;
        }

        Close();
    }
}
