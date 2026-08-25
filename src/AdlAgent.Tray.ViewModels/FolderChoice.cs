using System;

namespace AdlAgent.Tray;

/// <summary>
/// What Windows says is behind a drive letter, when the answer is "not a
/// local disk".
/// </summary>
/// <param name="UncRoot">
/// The share the letter is mapped to (<c>\\nas\met</c>), or <c>null</c> when
/// Windows would not say. Null is not unusual: a mapping made by a different
/// logon session, or one whose server is not answering, is still a network
/// drive and still one the service cannot see.
/// </param>
public sealed record NetworkDrive(string? UncRoot);

/// <summary>
/// Ask what is behind a drive letter such as <c>Z:</c>, given without a
/// trailing separator. Returns <c>null</c> for a local disk.
/// </summary>
/// <remarks>
/// A delegate rather than one of the numbered platform seams in
/// <c>AdlAgent.Core.Platform</c>. Those five are the collecting service's
/// view of the operating system; this is the tray's alone, and putting it
/// beside them would make the service's assembly carry an abstraction only a
/// window ever calls.
/// </remarks>
public delegate NetworkDrive? DriveLookup(string driveRoot);

/// <summary>
/// Everything the Browse button decides, with no dialog in it.
/// </summary>
/// <remarks>
/// The dialog itself is three lines in the settings window, because
/// <c>OpenFolderDialog</c> is <c>net10.0-windows</c> and nothing there can be
/// driven by a test. What is here is the part that can be wrong: which folder
/// to open at, what a picked path becomes, and what to warn about.
/// <para>
/// The warning is the reason this class is worth having at all. The tray runs
/// as the technician standing at the machine; the service runs as LocalSystem
/// (see <c>packaging/msi/AdlAgent.wxs</c>). A drive letter mapped in the
/// technician's session does not exist in LocalSystem's, and a share the
/// technician can read is reached by the service as the machine's own account
/// rather than as them. Both produce the same answer from the folder preview
/// -- "Nothing was found in this folder" -- which is true and sends somebody
/// looking for a typo that is not there.
/// </para>
/// <para>
/// Windows paths are parsed here rather than through <see cref="System.IO.Path"/>,
/// which reads a backslash as an ordinary character everywhere except
/// Windows. The test project is <c>net10.0</c> and runs on the Linux CI
/// runner and on macOS, so a rule written against the host's separator would
/// pass there while meaning nothing about the machines this ships to.
/// </para>
/// </remarks>
public sealed class FolderChoice
{
    private readonly DriveLookup _drives;
    private readonly Func<string, bool> _exists;

    /// <param name="drives">What is behind a drive letter.</param>
    /// <param name="exists">
    /// Whether a folder is there. Called only for local paths -- see
    /// <see cref="StartingFolder"/>.
    /// </param>
    public FolderChoice(DriveLookup drives, Func<string, bool> exists)
    {
        _drives = drives;
        _exists = exists;
    }

    /// <summary>
    /// The folder the Browse dialog should open at, or <c>null</c> to let
    /// Windows choose.
    /// </summary>
    /// <remarks>
    /// A local path that is not there is walked up until something is:
    /// <c>C:\VendorData\Garisa</c> opens at <c>C:\VendorData</c>, which is
    /// where somebody who mistyped the last segment needs to be.
    /// <para>
    /// A network path is handed back untouched and never probed. Asking
    /// whether a folder exists on a share whose host is down blocks for the
    /// SMB timeout, and this is called on the UI thread from a button that a
    /// technician presses precisely when a share is not behaving. Windows
    /// falls back on its own if the path is bad, which is a worse starting
    /// folder and a very much better window.
    /// </para>
    /// </remarks>
    public string? StartingFolder(string? current)
    {
        var path = (current ?? "").Trim();

        if (path.Length == 0)
        {
            return null;
        }

        if (IsNetwork(path))
        {
            return path;
        }

        for (var candidate = path; candidate is not null; candidate = Parent(candidate))
        {
            if (_exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// What the path box should read after somebody picked a folder.
    /// </summary>
    /// <remarks>
    /// A mapped drive is rewritten to the share it points at, because the
    /// letter is the one form of the path that cannot ever work: drive
    /// mappings belong to a logon session and the service has none. The UNC
    /// form at least has a chance, and whether it has more than a chance is
    /// what <see cref="NoteFor"/> is for.
    /// <para>
    /// Rewriting rather than warning-and-leaving, because a technician who is
    /// told their <c>Z:\</c> path is wrong and left holding it has been given
    /// a problem instead of an answer -- and the answer is one Windows can
    /// state exactly.
    /// </para>
    /// </remarks>
    public string Accept(string? picked)
    {
        var path = (picked ?? "").Trim();

        if (path.Length == 0)
        {
            return "";
        }

        if (Drive(path) is { } drive && _drives(drive) is { UncRoot: { Length: > 0 } share })
        {
            var beneath = path[drive.Length..];

            path = TrimEnd(share) + (beneath.Length == 0 ? "\\" : beneath);
        }

        // A trailing separator is dropped so that picking the folder ADL
        // already holds is not read as an edit -- except on a root, where it
        // is part of the path rather than punctuation after it.
        return IsRoot(path) ? path : TrimEnd(path);
    }

    /// <summary>
    /// What to say under the path box about this path, or nothing when there
    /// is nothing to say.
    /// </summary>
    public string NoteFor(string? path)
    {
        var value = (path ?? "").Trim();

        if (value.Length == 0)
        {
            return "";
        }

        if (Drive(value) is { } drive && _drives(drive) is not null)
        {
            return $"{drive} is a drive mapped in your own logon session. The ADL Agent service "
                + "runs as LocalSystem, which has no drive mappings at all, so it cannot see "
                + "this folder and never will. Give the path in its \\\\server\\share form "
                + "instead.";
        }

        if (IsUnc(value))
        {
            return "This is a network path. The ADL Agent service runs as LocalSystem and "
                + "reaches a share as this machine's own account rather than as you, so the "
                + "share has to grant that account read access.";
        }

        return "";
    }

    private bool IsNetwork(string path) =>
        IsUnc(path) || (Drive(path) is { } drive && _drives(drive) is not null);

    // ---------- Windows paths, read the same way on every host ----------

    private static bool IsSeparator(char character) => character is '\\' or '/';

    /// <summary>The <c>X:</c> a path is rooted at, or null if it is not.</summary>
    private static string? Drive(string path) =>
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':' ? path[..2] : null;

    private static bool IsUnc(string path) =>
        path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

    private static string TrimEnd(string path)
    {
        var end = path.Length;

        while (end > 0 && IsSeparator(path[end - 1]))
        {
            end--;
        }

        return path[..end];
    }

    /// <summary>
    /// True for <c>C:\</c> and for <c>\\server</c> or <c>\\server\share</c>:
    /// the paths that have no folder above them.
    /// </summary>
    private static bool IsRoot(string path)
    {
        var trimmed = TrimEnd(path);

        if (trimmed.Length == 0)
        {
            return true;
        }

        if (Drive(trimmed) is not null && trimmed.Length == 2)
        {
            return true;
        }

        if (!IsUnc(path))
        {
            return false;
        }

        var segments = 0;
        var inside = false;

        foreach (var character in trimmed[2..])
        {
            if (IsSeparator(character))
            {
                inside = false;
            }
            else if (!inside)
            {
                inside = true;
                segments++;
            }
        }

        // \\server and \\server\share are both roots: neither names a folder
        // that a picker could have opened one level above.
        return segments <= 2;
    }

    /// <summary>The folder above this one, or null once there is not one.</summary>
    private static string? Parent(string path)
    {
        var trimmed = TrimEnd(path);

        if (trimmed.Length == 0 || IsRoot(trimmed))
        {
            return null;
        }

        var cut = trimmed.Length - 1;

        while (cut >= 0 && !IsSeparator(trimmed[cut]))
        {
            cut--;
        }

        if (cut < 0)
        {
            return null;
        }

        var above = TrimEnd(trimmed[..cut]);

        if (above.Length == 0)
        {
            return null;
        }

        // "C:" is not a folder anybody can browse; "C:\" is.
        return Drive(above) is not null && above.Length == 2 ? above + '\\' : above;
    }
}
