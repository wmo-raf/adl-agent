using AdlAgent.Core.Diagnostics;

namespace AdlAgent.Tray;

/// <summary>
/// One recorded unit pass, as a row somebody can open.
/// </summary>
/// <remarks>
/// A heading and a block, because that is the shape of the question. Somebody
/// opening this window is looking down a list of moments for the one they are
/// asking about -- "what happened at 13:24" -- and only then wants the file
/// detail. A list that showed every file of every pass would bury the moments
/// under them.
/// <para>
/// Both strings come from <see cref="CycleRecordText"/>, which is also what
/// writes the diagnostics bundle. One renderer, so the pass a technician read
/// on screen and the pass HQ reads in the attachment are the same sentences.
/// </para>
/// </remarks>
public sealed class PassViewModel
{
    public PassViewModel(CycleRecord record)
    {
        Heading = CycleRecordText.Heading(record);
        Detail = CycleRecordText.Render(record).TrimEnd();
        Completed = record.Completed;
    }

    /// <summary>The one line the row shows closed.</summary>
    public string Heading { get; }

    /// <summary>Everything about the pass, shown when the row is opened.</summary>
    public string Detail { get; }

    /// <summary>False when the pass was cut short, which the row says in colour.</summary>
    public bool Completed { get; }
}
