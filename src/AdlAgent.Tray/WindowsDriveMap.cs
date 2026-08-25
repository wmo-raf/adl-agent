using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AdlAgent.Tray;

/// <summary>
/// What Windows says is behind a drive letter.
/// </summary>
/// <remarks>
/// The one thing the folder picker needs that has no managed API.
/// <see cref="DriveInfo"/> will say a letter is a network drive but not what
/// it is mapped to, and the mapping is the whole point: a letter cannot be
/// saved to ADL, because the service that will read it runs as LocalSystem
/// and LocalSystem has no drive mappings. The share behind the letter can be.
/// <para>
/// Here rather than in <c>AdlAgent.Tray.ViewModels</c> because this is the
/// half that touches the operating system. What to do with the answer is in
/// <see cref="FolderChoice"/>, next door, where a test can drive it.
/// </para>
/// </remarks>
internal static class WindowsDriveMap
{
    /// <summary>
    /// Long enough for any UNC path Windows will hand back, which is bounded
    /// by MAX_PATH for this API.
    /// </summary>
    private const int RemoteNameBufferChars = 261;

    private const int NoError = 0;

    /// <summary>
    /// What is behind <paramref name="driveRoot"/> (given as <c>Z:</c>), or
    /// null when it is an ordinary local disk.
    /// </summary>
    /// <remarks>
    /// Never throws. Every failure here means "Windows would not say", and a
    /// network drive whose target could not be read is still a network drive
    /// -- which is the part the technician has to be told about, and the part
    /// that does not depend on knowing the share's name.
    /// </remarks>
    public static NetworkDrive? Lookup(string driveRoot)
    {
        if (driveRoot.Length < 2)
        {
            return null;
        }

        try
        {
            if (new DriveInfo(driveRoot).DriveType != DriveType.Network)
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            // Not a drive letter this machine has ever heard of. Nothing is
            // mapped to it, so nothing needs saying about it.
            return null;
        }

        return new NetworkDrive(RemoteNameOf(driveRoot));
    }

    private static string? RemoteNameOf(string driveRoot)
    {
        var remote = new StringBuilder(RemoteNameBufferChars);
        var length = remote.Capacity;

        return WNetGetConnection(driveRoot, remote, ref length) == NoError && remote.Length > 0
            ? remote.ToString()
            : null;
    }

    [DllImport(
        "mpr.dll",
        CharSet = CharSet.Unicode,
        EntryPoint = "WNetGetConnectionW",
        ExactSpelling = true)]
    private static extern int WNetGetConnection(
        string localName,
        StringBuilder remoteName,
        ref int remoteNameLength);
}
