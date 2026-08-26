using static AdlAgent.Core.Tests.WixSource;

namespace AdlAgent.Core.Tests;

/// <summary>
/// The pictures the installer draws, and the one thing a test can say about a
/// picture.
/// </summary>
/// <remarks>
/// Not whether it looks right — nothing here can see it, and nothing in CI
/// ever will. What can be checked is the part Windows Installer is silent
/// about: WixUI draws each of these at exactly one size, and a bitmap that is
/// not that size is stretched or clipped without a warning, at link time or at
/// run time. So a banner eight pixels too wide ships looking subtly wrong to
/// twenty-six countries and nothing in the build says a word.
/// <para>
/// Made by <c>assets/render-icons.sh</c> and committed, so that neither CI leg
/// nor a machine reproducing a release needs a rasteriser — see
/// <c>assets/README.md</c>.
/// </para>
/// </remarks>
public class InstallerArtworkTests
{
    /// <summary>
    /// The two WixUI images, and the size each is drawn at.
    /// </summary>
    /// <remarks>
    /// The strip across the top of every screen, and the panel down the left
    /// of the Welcome and Exit dialogs. Both sizes are WixUI's rather than
    /// this package's, which is exactly why they are written down here: the
    /// package is free to change the picture and not free to change the shape.
    /// </remarks>
    public static TheoryData<string, int, int> Images =>
        new()
        {
            { "WixUIBannerBmp", 493, 58 },
            { "WixUIDialogBmp", 493, 312 },
        };

    [Theory]
    [MemberData(nameof(Images))]
    public void Each_picture_is_the_size_the_installer_draws_it_at(
        string variable, int width, int height)
    {
        var file = Artwork(variable);

        var header = File.ReadAllBytes(file).AsSpan(0, 54);

        // A BMP says what it is in its own first 54 bytes: "BM", then the size
        // at 18 and 22, the depth at 28 and the compression at 30. Reading it
        // here rather than through an image library keeps this test on the
        // same footing as the rest of the suite, which runs on a Linux CI
        // runner with nothing installed on it.
        Assert.Equal((byte)'B', header[0]);
        Assert.Equal((byte)'M', header[1]);

        Assert.Equal(width, BitConverter.ToInt32(header[18..22]));
        Assert.Equal(height, BitConverter.ToInt32(header[22..26]));

        // 24-bit and uncompressed, because a BMP carrying an alpha channel or
        // a run-length payload is one some versions of Windows draw as a black
        // rectangle -- which is worse than the toolset's own picture.
        Assert.Equal(24, BitConverter.ToInt16(header[28..30]));
        Assert.Equal(0, BitConverter.ToInt32(header[30..34]));
    }

    /// <summary>
    /// And the per-user tier shows the same one.
    /// </summary>
    /// <remarks>
    /// A PNG, and not held to any of the above: Velopack draws it itself and
    /// none of Windows Installer's rules about the Binary table apply. What
    /// matters is that it is there, because <c>pack.ps1</c> names it on a
    /// command line and a missing file is a packaging job that fails after
    /// building everything else.
    /// </remarks>
    [Fact]
    public void The_per_user_installer_has_a_picture_too()
    {
        var splash = Path.Combine(RepositoryRoot.Path, "assets", "installer-splash.png");

        Assert.True(File.Exists(splash), $"{splash} is not there, and pack.ps1 passes it to vpk.");

        Assert.Contains(
            "installer-splash.png",
            File.ReadAllText(Path.Combine(PackagingDirectory, "pack.ps1")));
    }

    /// <summary>
    /// The pictures are the package's own rather than the toolset's.
    /// </summary>
    /// <remarks>
    /// Overriding these is a linker variable and nothing else: no Binary row
    /// is redefined and no control is repointed, so the address screen still
    /// asks for <c>WixUI_Bmp_Banner</c> and gets whatever this names. Which
    /// means the failure is silent in the other direction too — remove the
    /// variable and the installer quietly goes back to the toolset's artwork,
    /// with nothing broken and nobody told.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Images))]
    public void The_installer_is_pointed_at_them(string variable, int width, int height)
    {
        _ = width;
        _ = height;

        Assert.True(File.Exists(Artwork(variable)));
    }

    /// <summary>Where a WixVariable says one of the pictures is.</summary>
    /// <remarks>
    /// Expanded the way the WiX preprocessor would, so what is checked is the
    /// path the compiler is given rather than the name of a variable.
    /// </remarks>
    private static string Artwork(string variable)
    {
        var declared = Load("AdlAgentUI.wxs").Descendants(Wxs + "WixVariable")
            .Single(wix => (string?)wix.Attribute("Id") == variable)
            .Condition("Value");

        // AssetsDir defaults to "assets", relative to where the compiler is
        // run, which for a test is the repository. The quotes come with it:
        // a WiX <?define?> carries them, and the preprocessor strips them
        // where it substitutes.
        var path = declared.Replace("\"", string.Empty)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(RepositoryRoot.Path, path);
    }
}
