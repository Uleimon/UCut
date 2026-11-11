using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace UCut.Desktop.ViewModels;

internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Async command that disables itself while running, so actions cannot be started twice.</summary>
internal sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.Net.Http.HttpRequestException or ArgumentException)
        {
            // Errors are shown, never swallowed.
            System.Windows.MessageBox.Show(ex.Message, "UCut – Fehler", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

internal sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Dialogs, abstracted so view models stay testable.</summary>
internal interface IDialogs
{
    bool Confirm(string title, string message, bool dangerous = false);
    void Info(string title, string message);
    string? PickFolder(string title);
    IReadOnlyList<string> PickFiles(string title, string filter, bool multiple);
    string? SaveFile(string title, string fileName, string filter);
}

internal sealed class WpfDialogs : IDialogs
{
    public const string VideoFilter = "Videos|*.mp4;*.mkv;*.mov|Alle Dateien|*.*";
    public const string AudioFilter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac|Alle Dateien|*.*";

    public bool Confirm(string title, string message, bool dangerous = false) =>
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo,
            dangerous ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    public void Info(string title, string message) =>
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    public string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public IReadOnlyList<string> PickFiles(string title, string filter, bool multiple)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, Multiselect = multiple };
        return dlg.ShowDialog() == true ? dlg.FileNames : [];
    }

    public string? SaveFile(string title, string fileName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = fileName, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
