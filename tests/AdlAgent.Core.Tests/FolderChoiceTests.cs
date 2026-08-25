using AdlAgent.Tray;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What the settings window's Browse button decides, without a dialog.
/// </summary>
/// <remarks>
/// The dialog is three lines in <c>StationSettingsWindow.xaml.cs</c> and is
/// still not automated. Everything it asks and everything it does with the
/// answer is here.
/// <para>
/// Every path below is a Windows path, and this test project runs on Linux
/// and macOS as well. That is deliberate: <see cref="FolderChoice"/> parses
/// them itself rather than through <c>System.IO.Path</c>, which reads a
/// backslash as an ordinary character everywhere except Windows -- so a rule
/// written against the host's separator would pass here and mean nothing
/// about the machines this ships to.
/// </para>
/// </remarks>
public class FolderChoiceTests
{
    // ---------- where the dialog opens ----------

    [Fact]
    public void A_folder_that_is_there_is_where_the_dialog_opens()
    {
        var choice = Local(exists: "C:\\VendorData\\Garissa");

        Assert.Equal("C:\\VendorData\\Garissa", choice.StartingFolder("C:\\VendorData\\Garissa"));
    }

    [Fact]
    public void A_mistyped_last_segment_opens_at_the_folder_above_it()
    {
        var choice = Local(exists: "C:\\VendorData");

        // The whole reason for walking up: somebody who typed "Garisa" is
        // one click from the right answer, and "This PC" is not.
        Assert.Equal("C:\\VendorData", choice.StartingFolder("C:\\VendorData\\Garisa"));
    }

    [Fact]
    public void A_path_whose_drive_is_the_only_real_part_of_it_opens_at_the_drive()
    {
        var choice = Local(exists: "C:\\");

        Assert.Equal("C:\\", choice.StartingFolder("C:\\Vendor\\Data\\Garissa"));
    }

    [Fact]
    public void A_path_with_nothing_real_in_it_lets_Windows_choose()
    {
        var choice = Local();

        Assert.Null(choice.StartingFolder("D:\\Vendor\\Data"));
    }

    [Fact]
    public void An_empty_box_lets_Windows_choose()
    {
        var choice = Local();

        Assert.Null(choice.StartingFolder(""));
        Assert.Null(choice.StartingFolder("   "));
        Assert.Null(choice.StartingFolder(null));
    }

    [Fact]
    public void A_share_is_handed_over_without_ever_being_asked_about()
    {
        var asked = new List<string>();

        var choice = new FolderChoice(
            _ => null,
            path =>
            {
                asked.Add(path);

                return false;
            });

        Assert.Equal("\\\\nas\\met\\garissa", choice.StartingFolder("\\\\nas\\met\\garissa"));

        // The point of the rule, and it is about a window rather than about a
        // path: asking whether a folder exists on a share whose host is down
        // blocks for the SMB timeout, on the UI thread, at exactly the moment
        // a technician pressed Browse because a share is misbehaving.
        Assert.Empty(asked);
    }

    [Fact]
    public void A_mapped_drive_is_not_asked_about_either()
    {
        var asked = new List<string>();

        var choice = new FolderChoice(
            drive => drive == "Z:" ? new NetworkDrive("\\\\nas\\met") : null,
            path =>
            {
                asked.Add(path);

                return false;
            });

        Assert.Equal("Z:\\garissa", choice.StartingFolder("Z:\\garissa"));
        Assert.Empty(asked);
    }

    // ---------- what a picked folder becomes ----------

    [Fact]
    public void A_local_folder_is_taken_as_it_was_picked()
    {
        var choice = Local();

        Assert.Equal("C:\\VendorData\\Garissa", choice.Accept("C:\\VendorData\\Garissa"));
    }

    [Fact]
    public void A_mapped_drive_is_rewritten_to_the_share_behind_it()
    {
        var choice = Mapped("Z:", "\\\\nas\\met");

        // The letter is the one form of this path that cannot ever work: the
        // service runs as LocalSystem, and LocalSystem has no drive mappings.
        Assert.Equal("\\\\nas\\met\\garissa", choice.Accept("Z:\\garissa"));
    }

    [Fact]
    public void A_mapped_drive_Windows_will_not_name_is_left_as_it_was()
    {
        var choice = new FolderChoice(_ => new NetworkDrive(UncRoot: null), _ => true);

        // Nothing better to offer. The note is what carries the problem in
        // this case, and it does not need the share's name to state it.
        Assert.Equal("Z:\\garissa", choice.Accept("Z:\\garissa"));
    }

    [Fact]
    public void A_trailing_separator_is_dropped_so_that_picking_what_ADL_holds_is_not_an_edit()
    {
        var choice = Local();

        Assert.Equal("C:\\VendorData\\Garissa", choice.Accept("C:\\VendorData\\Garissa\\"));
    }

    [Fact]
    public void A_root_keeps_the_separator_that_is_part_of_it()
    {
        var choice = Local();

        Assert.Equal("C:\\", choice.Accept("C:\\"));
        Assert.Equal("\\\\nas\\met", choice.Accept("\\\\nas\\met"));
    }

    [Fact]
    public void Nothing_picked_is_nothing_typed()
    {
        var choice = Local();

        Assert.Equal("", choice.Accept(""));
        Assert.Equal("", choice.Accept(null));
    }

    // ---------- and what to say about it ----------

    [Fact]
    public void A_local_folder_has_nothing_said_about_it()
    {
        var choice = Local();

        Assert.Equal("", choice.NoteFor("C:\\VendorData\\Garissa"));
        Assert.Equal("", choice.NoteFor(""));
    }

    [Fact]
    public void A_mapped_drive_is_said_to_be_invisible_to_the_service()
    {
        var choice = Mapped("Z:", "\\\\nas\\met");

        var note = choice.NoteFor("Z:\\garissa");

        // Not "check the path is right", which is what the folder count says
        // about this and about a typo alike. Only one of the two is fixable
        // by checking the path.
        Assert.Contains("Z:", note, StringComparison.Ordinal);
        Assert.Contains("LocalSystem", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_share_is_said_to_be_reached_as_the_machine_rather_than_as_the_technician()
    {
        var choice = Local();

        var note = choice.NoteFor("\\\\nas\\met\\garissa");

        Assert.Contains("LocalSystem", note, StringComparison.Ordinal);
        Assert.Contains("this machine's own account", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rewritten_mapped_drive_still_has_something_said_about_it()
    {
        var choice = Mapped("Z:", "\\\\nas\\met");

        // Rewriting gives the path a chance of working. Whether it has more
        // than a chance depends on what the share grants this machine's
        // account, which is the sentence that has to survive the rewrite.
        var note = choice.NoteFor(choice.Accept("Z:\\garissa"));

        Assert.Contains("LocalSystem", note, StringComparison.Ordinal);
    }

    // ---------- helpers ----------

    /// <summary>A machine where every drive is a local disk.</summary>
    private static FolderChoice Local(params string[] exists) =>
        new(_ => null, path => exists.Contains(path, StringComparer.OrdinalIgnoreCase));

    /// <summary>A machine with one letter mapped to one share.</summary>
    private static FolderChoice Mapped(string drive, string share) =>
        new(
            asked => asked.Equals(drive, StringComparison.OrdinalIgnoreCase)
                ? new NetworkDrive(share)
                : null,
            _ => true);
}
