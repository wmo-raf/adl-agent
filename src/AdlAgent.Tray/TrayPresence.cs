using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
/// The shape is the ADL mark and the colour is still the state. It used to be
/// a plain filled circle, drawn here rather than shipped, on the reasoning
/// that three states needed three icons and a colour baked into a binary
/// asset could drift from the meaning it stood for. That reasoning survives;
/// what changed is that one shape can serve all of them. <c>adl-mark.png</c>
/// is shipped for its outline alone -- every colour in it is thrown away and
/// replaced by the one this state calls for -- so the colour is still decided
/// here, in the same switch, and still cannot drift.
/// </para>
/// <para>
/// The mark loses its "ADL" lettering in the process, because the letters are
/// opaque black in the source and get recoloured along with everything else.
/// That is the outcome we want: at sixteen pixels they are five pixels by
/// two. See <c>assets/README.md</c>.
/// </para>
/// </remarks>
public sealed class TrayPresence : IDisposable
{
    /// <summary>
    /// The mark, embedded by <c>AdlAgent.Tray.csproj</c>.
    /// </summary>
    private const string Mark = "AdlAgent.Tray.adl-mark.png";

    private readonly NotifyIcon _icon;

    /// <summary>
    /// One icon per state, built the first time that state is seen and held
    /// until this is disposed.
    /// </summary>
    /// <remarks>
    /// <see cref="Show"/> runs on a five-second timer for as long as the
    /// technician is logged on, and what it draws changes perhaps twice a
    /// day. Decoding the mark, recolouring it and creating a fresh GDI handle
    /// seven hundred times an hour to produce the same four pictures is work
    /// nobody asked for, and every one of those handles is this process's to
    /// release. There are exactly four states, so the cache is bounded by the
    /// enumeration rather than by anything this has to police.
    /// </remarks>
    private readonly Dictionary<TrayState, Icon> _icons = new();

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
        if (!_icons.TryGetValue(state, out var icon))
        {
            icon = Draw(state);
            _icons[state] = icon;
        }

        _icon.Icon = icon;

        // Windows truncates the tooltip at 63 characters and throws above
        // 127, so the sentence is cut here rather than by the shell.
        _icon.Text = tooltip.Length > 62 ? tooltip[..59] + "..." : tooltip;
    }

    public void Dispose()
    {
        // The notification icon first: it is holding whichever of these is
        // currently on screen.
        _icon.Visible = false;
        _icon.Dispose();

        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }

    /// <summary>The ADL mark in the colour of the state, at tray size.</summary>
    private static Icon Draw(TrayState state)
    {
        var colour = state switch
        {
            TrayState.Working => Color.FromArgb(0x2E, 0x86, 0x4B),
            TrayState.NeedsAttention => Color.FromArgb(0xC7, 0x8A, 0x1B),
            TrayState.Stopped => Color.FromArgb(0xB3, 0x2D, 0x2D),
            _ => Color.FromArgb(0x77, 0x7F, 0x88),
        };

        using var stream = typeof(TrayPresence).Assembly.GetManifestResourceStream(Mark)
            // Only reachable if the build stopped embedding it, in which case
            // the tray has no icon at all and saying so beats a blank corner.
            ?? throw new InvalidOperationException($"The tray icon '{Mark}' is missing from this build.");

        using var mask = new Bitmap(stream);

        // Drawn at 32 and let Windows scale down: a 16-pixel mark drawn
        // directly loses the separation between the discs on a high-DPI
        // screen, which is most of the machines this now runs on.
        using var bitmap = new Bitmap(32, 32);
        using var canvas = Graphics.FromImage(bitmap);

        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
        canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var recolour = new ImageAttributes();

        // Every pixel keeps its alpha and is given this state's colour. The
        // last row is the translation, so the red, green and blue that come
        // out do not depend on the ones that went in -- which is what makes
        // the shipped file a shape rather than a picture, and the lettering
        // inside it disappear rather than turn up as three grey smudges.
        recolour.SetColorMatrix(new ColorMatrix(
        [
            [0f, 0f, 0f, 0f, 0f],
            [0f, 0f, 0f, 0f, 0f],
            [0f, 0f, 0f, 0f, 0f],
            [0f, 0f, 0f, 1f, 0f],
            [colour.R / 255f, colour.G / 255f, colour.B / 255f, 0f, 1f],
        ]));

        canvas.DrawImage(
            mask,
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            0, 0, mask.Width, mask.Height,
            GraphicsUnit.Pixel,
            recolour);

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
