using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AdlAgent.Core;

namespace AdlAgent.Tray;

/// <summary>
/// Writes WPF's binding failures to a file, when asked.
/// </summary>
/// <remarks>
/// A binding whose path is wrong does not throw and does not draw anything:
/// the label is simply empty, and looks exactly like a label whose value the
/// service did not send. That is the one class of mistake in this program
/// that neither the compiler nor the test suite can catch -- XAML compiles
/// with the path unchecked, and the window is not automated -- so the only
/// way to find one is to be told.
/// <para>
/// Off by default and switched on by an environment variable, because this
/// is a tool for whoever is building or testing the tray rather than a
/// condition of the field. A binding is right or wrong for the whole fleet
/// at once; there is nothing a technician in-country could do with the file,
/// and nothing worth writing to their disk every session.
/// </para>
/// <para>
/// Started before the first window is constructed. A listener added
/// afterwards misses exactly the bindings that were evaluated as it opened,
/// which is most of them.
/// </para>
/// </remarks>
internal sealed class BindingTrace : IDisposable
{
    /// <summary>Set this to a file path to turn the trace on.</summary>
    public const string LogPathVariable = "ADL_AGENT_TRAY_BINDING_LOG";

    private readonly TextWriterTraceListener _listener;

    private BindingTrace(TextWriterTraceListener listener)
    {
        _listener = listener;
    }

    /// <summary>
    /// Begin tracing if the environment asks for it, or return
    /// <c>null</c> quietly.
    /// </summary>
    /// <remarks>
    /// Quietly, and never throwing: a diagnostic that stops the window
    /// opening has cost more than it can ever pay back. A path that cannot be
    /// written -- a folder that is not there, a disk that is full, a
    /// technician's profile with no rights to it -- simply means no trace.
    /// </remarks>
    public static BindingTrace? StartIfAsked()
    {
        var path = Environment.GetEnvironmentVariable(LogPathVariable);

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // A writer of our own, with AutoFlush set, rather than the file
            // name overload. That overload buffers, and Trace.AutoFlush below
            // governs writes made through Trace rather than writes made
            // straight to a listener -- so with it alone the whole trace,
            // header included, reaches the disk only when this is disposed,
            // and a run that was killed leaves an empty file. Which is
            // precisely the run somebody wants to read.
            var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            var listener = new TextWriterTraceListener(writer, "adl-agent-tray-bindings");

            // Refreshed first: the trace sources read their configuration
            // once, and a listener added to a source that has not been woken
            // is a listener nothing reaches.
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            // Flushed as it goes. The interesting run is often the one that
            // was closed impatiently, or killed, and a buffered log of it is
            // an empty file.
            Trace.AutoFlush = true;

            listener.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"ADL Agent tray {AgentVersion.Current} — binding trace opened {DateTimeOffset.Now:u}"));

            return new BindingTrace(listener);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        PresentationTraceSources.DataBindingSource.Listeners.Remove(_listener);

        _listener.Flush();
        _listener.Dispose();
    }
}
