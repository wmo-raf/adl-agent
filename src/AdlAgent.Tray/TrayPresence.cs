using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AdlAgent.Tray;

/// <summary>
/// The dot in the notification area: the whole of the product for a
/// technician who is not currently doing anything.
/// </summary>
/// <remarks>
/// Story 27 is mostly this. Somebody who wants to know whether the machine is
/// still sending should be able to find out by looking at the corner of their
/// screen, and only open the window when the answer is no. So the icon's
/// colour is the answer and the tooltip is the sentence, and neither is
/// computed here -- both come from the status the service reported.
/// <para>
/// The icon is drawn rather than shipped as a file. Three states need three
/// icons, an <c>.ico</c> is a binary asset in a source tree, and the drawing
/// is eight lines -- and this way the colour cannot drift from the meaning.
/// </para>
/// </remarks>
public sealed class TrayPresence : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _drawn;

    public TrayPresence()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Open ADL Agent", null, (_, _) => Opened?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Close this window", null, (_, _) => Closed?.Invoke(this, EventArgs.Empty));

        _icon = new NotifyIcon
        {
            Text = "ADL Agent",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => Opened?.Invoke(this, EventArgs.Empty);

        Show(TrayState.Unknown, "ADL Agent");
    }

    /// <summary>The technician asked for the window.</summary>
    public event EventHandler? Opened;

    /// <summary>
    /// The technician asked for the tray itself to go away.
    /// </summary>
    /// <remarks>
    /// Named for what it does. Closing this window stops nothing: the service
    /// keeps collecting and sending with nobody logged on, which is the whole
    /// point of it being a service. A menu item saying "Exit" beside a
    /// product whose job is to keep running unattended is an invitation to
    /// stop a country's data by accident.
    /// </remarks>
    public event EventHandler? Closed;

    /// <summary>Say what the machine is doing, in a colour and a sentence.</summary>
    public void Show(TrayState state, string tooltip)
    {
        var next = Draw(state);
        var previous = _drawn;

        _icon.Icon = next;
        _drawn = next;

        // The handle behind the old icon is this process's to release, and
        // this runs every few seconds for as long as the technician is logged
        // on -- which is long enough for a leak here to matter.
        previous?.Dispose();

        // Windows truncates the tooltip at 63 characters and throws above
        // 127, so the sentence is cut here rather than by the shell.
        _icon.Text = tooltip.Length > 62 ? tooltip[..59] + "..." : tooltip;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawn?.Dispose();
    }

    /// <summary>A filled circle in the colour of the state, at tray size.</summary>
    private static Icon Draw(TrayState state)
    {
        var colour = state switch
        {
            TrayState.Working => Color.FromArgb(0x2E, 0x86, 0x4B),
            TrayState.NeedsAttention => Color.FromArgb(0xC7, 0x8A, 0x1B),
            TrayState.Stopped => Color.FromArgb(0xB3, 0x2D, 0x2D),
            _ => Color.FromArgb(0x77, 0x7F, 0x88),
        };

        // Drawn at 32 and let Windows scale down: a 16-pixel circle drawn
        // directly is a square with corners on a high-DPI screen, which is
        // most of the machines this now runs on.
        using var bitmap = new Bitmap(32, 32);
        using var canvas = Graphics.FromImage(bitmap);

        canvas.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var fill = new SolidBrush(colour);

        canvas.FillEllipse(fill, 3, 3, 26, 26);

        var handle = bitmap.GetHicon();

        try
        {
            // Cloned because the icon returned by FromHandle does not own its
            // handle, and the handle must be destroyed here.
            using var borrowed = Icon.FromHandle(handle);

            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <remarks>
    /// The classic marshaller rather than <c>LibraryImport</c>: the generated
    /// form needs <c>AllowUnsafeBlocks</c>, which is a large thing to turn on
    /// across a user-interface project for one call that frees one handle.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>
/// What the dot means. Three states, because there are three different
/// things to do about them.
/// </summary>
public enum TrayState
{
    /// <summary>Nothing has been heard from the service yet.</summary>
    Unknown,

    /// <summary>Paired, synced, and ADL is answering. Nothing to do.</summary>
    Working,

    /// <summary>Running, but something wants a person: unpaired, revoked, or ADL unreachable.</summary>
    NeedsAttention,

    /// <summary>The service is not running. Nothing is being collected.</summary>
    Stopped,
}
