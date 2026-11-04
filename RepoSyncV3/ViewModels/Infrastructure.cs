using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RepoSyncV3.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void OnPropertiesChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>An async ICommand that disables itself while it runs. The parameter is passed through.</summary>
public sealed class AsyncCommand : ICommand
{
    /// <summary>
    /// Where a command's exception goes (MainViewModel points it at the Activity panel). Unset, it is rethrown
    /// to App's unhandled-exception handler. Cancellation is never reported.
    /// </summary>
    public static Action<Exception>? ErrorHandler { get; set; }


    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(parameter);
        }
        catch (OperationCanceledException)
        {
            // cancelled on purpose
        }
        catch (Exception ex) when (ErrorHandler is not null)
        {
            ErrorHandler(ex);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public static class TimeText
{
    /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", "4 days ago", else the date.</summary>
    public static string Relative(DateTime? when)
    {
        if (when is not { } t || t == DateTime.MinValue) return string.Empty;
        var span = DateTime.Now - t.ToLocalTime();
        if (span.TotalSeconds < 0) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} days ago";
        return t.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
