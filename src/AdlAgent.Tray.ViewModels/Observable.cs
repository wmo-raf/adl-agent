using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AdlAgent.Tray;

/// <summary>The smallest thing WPF binding needs in order to notice a change.</summary>
/// <remarks>
/// Hand-written rather than taken from a toolkit package. The tray has four
/// view models and this is a dozen lines; a dependency here would be a
/// dependency in the installer, in the update feed, and in whatever review a
/// ministry's IT department runs before it lets a binary onto a server.
/// </remarks>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Change a field and say so, if it actually changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(property);

        return true;
    }

    protected void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>A button, wired to a method that goes to the service and back.</summary>
/// <remarks>
/// Asynchronous and self-disabling while it runs, which is also what stops a
/// technician pressing Pair four times while the first press is still
/// travelling.
/// <para>
/// <see cref="ICommand.Execute"/> returns void, so the task cannot be
/// awaited by WPF and an exception escaping it would take the process down
/// with no window and no message -- on a machine where the whole point of
/// this program is to be the thing that explains what is wrong. Hence the
/// catch, and hence <paramref name="onError"/> rather than a swallow.
/// </para>
/// </remarks>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly Action<Exception> _onError;
    private readonly Func<bool>? _ready;
    private bool _running;

    public AsyncCommand(Func<Task> run, Action<Exception> onError, Func<bool>? ready = null)
    {
        _run = run;
        _onError = onError;
        _ready = ready;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_ready?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        Refresh();

        try
        {
            await _run().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    /// <summary>Ask WPF to re-evaluate whether this button should be enabled.</summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
