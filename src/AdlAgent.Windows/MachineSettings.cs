using AdlAgent.Core.State;

namespace AdlAgent.Windows;

/// <summary>
/// The settings file an installer writes, and an administrator can edit.
/// </summary>
/// <remarks>
/// Where a machine's ADL is cannot come from ADL, so it has to be put on the
/// machine by whatever installed the agent. An MSI can write an INI file
/// directly, which is why this is one: there is no way to have Windows
/// Installer fill in a value inside a JSON document, and every alternative
/// route was worse.
/// <para>
/// A machine-wide environment variable was the obvious one and is a trap:
/// the Service Control Manager takes its environment block at boot and does
/// not re-read it, so a service installed and started in one Windows
/// Installer transaction starts without the variable that transaction just
/// set -- and then fails, restarts, and fails again until somebody reboots
/// the server. A file is read when the service starts, by the service.
/// </para>
/// <para>
/// It sits in the state directory rather than beside the program, so an
/// upgrade -- which replaces everything under Program Files -- cannot lose
/// it, and so an administrator diagnosing a machine finds the configuration
/// in the same place as the token and the logs.
/// </para>
/// </remarks>
public static class MachineSettings
{
    /// <summary>What the file is called, under the state directory.</summary>
    public const string FileName = "agent.ini";

    /// <summary>
    /// The section an INI file's keys are read under, which is the same
    /// section the agent's own options bind to: <c>[Agent] AdlBaseUrl=…</c>
    /// arrives as <c>Agent:AdlBaseUrl</c>.
    /// </summary>
    public const string Section = "Agent";

    /// <summary>The key holding the address of this machine's ADL.</summary>
    public const string AdlBaseUrlKey = "AdlBaseUrl";

    /// <summary>Where the file lives for a machine whose state is in <paramref name="stateDirectory"/>.</summary>
    public static string PathIn(string stateDirectory) =>
        Path.Combine(stateDirectory, FileName);

    /// <summary>
    /// Point the machine whose state is in <paramref name="stateDirectory"/>
    /// at <paramref name="adlBaseUrl"/>.
    /// </summary>
    /// <remarks>
    /// Written through <see cref="AtomicFile"/>: a country server losing power
    /// halfway through this would otherwise come back with a truncated
    /// <c>agent.ini</c>, and a truncated <c>agent.ini</c> is a machine that has
    /// forgotten where it reports -- which is exactly what somebody was using
    /// this to fix.
    /// <para>
    /// The directory has to be there already, and is pointedly not created.
    /// The MSI replaces this folder's permissions with SYSTEM and
    /// Administrators because the device token is stored in it in the clear;
    /// a folder made here instead would inherit whatever <c>%ProgramData%</c>
    /// grants, and the next pairing would put a credential in it.
    /// </para>
    /// </remarks>
    public static void PointAt(string stateDirectory, string adlBaseUrl)
    {
        var path = PathIn(stateDirectory);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;

        AtomicFile.Write(path, WithAdlBaseUrl(existing, adlBaseUrl));
    }

    /// <summary>
    /// The contents <paramref name="existing"/> would have with
    /// <see cref="AdlBaseUrlKey"/> set to <paramref name="adlBaseUrl"/>.
    /// </summary>
    /// <remarks>
    /// A rewrite of one line rather than of the file, because this file is not
    /// only ours. A country whose IT department deploys software itself sets
    /// <c>AutoUpdate=false</c> here, and an installer may have left comments
    /// in it; a repoint that dropped either would turn a machine's policy off
    /// silently, at whatever hour somebody was repointing it. Everything that
    /// is not this key comes through untouched, in the order it was in.
    /// </remarks>
    public static string WithAdlBaseUrl(string? existing, string adlBaseUrl)
    {
        var setting = $"{AdlBaseUrlKey}={adlBaseUrl}";

        if (string.IsNullOrWhiteSpace(existing))
        {
            return $"[{Section}]\r\n{setting}\r\n";
        }

        var lines = existing.ReplaceLineEndings("\n").Split('\n').ToList();
        var section = -1;
        var inSection = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Trim().Equals(Section, StringComparison.OrdinalIgnoreCase);

                if (inSection)
                {
                    section = index;
                }

                continue;
            }

            if (!inSection || IsComment(line))
            {
                continue;
            }

            var separator = line.IndexOf('=');

            if (separator > 0 &&
                line[..separator].Trim().Equals(AdlBaseUrlKey, StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = setting;

                return Rendered(lines);
            }
        }

        if (section >= 0)
        {
            // Directly under the heading, where an eye looking for it goes.
            lines.Insert(section + 1, setting);
        }
        else
        {
            // No [Agent] section at all: a file somebody wrote by hand for
            // something else. Its own contents are left where they are and
            // ours is added after them.
            if (lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{Section}]");
            lines.Add(setting);
        }

        return Rendered(lines);
    }

    /// <summary>
    /// The lines as the file, CRLF and ending in a newline.
    /// </summary>
    /// <remarks>
    /// Normalised rather than preserved. This is a Windows file, every reader
    /// of it accepts either ending, and one whose lines end two different ways
    /// -- because an installer wrote some of them and this wrote another -- is
    /// worse to read over somebody's shoulder than one that is consistent.
    /// </remarks>
    private static string Rendered(IEnumerable<string> lines) =>
        string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n";

    private static bool IsComment(string line) =>
        line.StartsWith(';') || line.StartsWith('#');
}
