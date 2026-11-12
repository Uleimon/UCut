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

    /// <summary>Info with "Für diese Sitzung nicht mehr anzeigen" and "Nie wieder anzeigen".</summary>
    HintAnswer Hint(string title, string message);
    string? PickFolder(string title);
    IReadOnlyList<string> PickFiles(string title, string filter, bool multiple);
    string? SaveFile(string title, string fileName, string filter);

    /// <summary>Asks for one line of text. Null = cancelled.</summary>
    string? Ask(string title, string message);
}

internal enum HintAnswer
{
    Ok,
    HideThisSession,
    HideForever,
}

internal sealed class WpfDialogs : IDialogs
{
    public HintAnswer Hint(string title, string message)
    {
        var session = new System.Windows.Controls.CheckBox { Content = "Für diese Sitzung nicht mehr anzeigen", Margin = new System.Windows.Thickness(0, 14, 0, 0) };
        var never = new System.Windows.Controls.CheckBox { Content = "Nie wieder anzeigen (in den Einstellungen wieder einschaltbar)", Margin = new System.Windows.Thickness(0, 6, 0, 0) };
        session.Checked += (_, _) => never.IsChecked = false;
        never.Checked += (_, _) => session.IsChecked = false;
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, IsCancel = true, Padding = new System.Windows.Thickness(24, 6, 24, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 16, 0, 0) };
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(22) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap, MaxWidth = 480 });
        panel.Children.Add(session);
        panel.Children.Add(never);
        panel.Children.Add(ok);
        var window = new System.Windows.Window
        {
            Title = title, Content = panel, SizeToContent = System.Windows.SizeToContent.WidthAndHeight, ResizeMode = System.Windows.ResizeMode.NoResize,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner, Owner = System.Windows.Application.Current?.MainWindow,
            Background = Brush("WindowBrush"), Foreground = Brush("TextBrush"),
        };
        if (System.Windows.Application.Current?.TryFindResource("Primary") is System.Windows.Style primary)
        {
            ok.Style = primary;
        }

        ok.Click += (_, _) => window.Close();
        window.ShowDialog();
        return never.IsChecked == true ? HintAnswer.HideForever : session.IsChecked == true ? HintAnswer.HideThisSession : HintAnswer.Ok;
    }

    // The app's styles make text light for the dark theme, so the dialogs need the dark window colour too (white made the text unreadable).
    private static System.Windows.Media.Brush? Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;

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

    public string? Ask(string title, string message)
    {
        var box = new System.Windows.Controls.TextBox { Margin = new System.Windows.Thickness(0, 12, 0, 12), MinWidth = 460 };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Padding = new System.Windows.Thickness(18, 6, 18, 6), Margin = new System.Windows.Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Abbrechen", IsCancel = true, Padding = new System.Windows.Thickness(18, 6, 18, 6) };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap, MaxWidth = 460 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new System.Windows.Window
        {
            Title = title, Content = panel, SizeToContent = System.Windows.SizeToContent.WidthAndHeight, ResizeMode = System.Windows.ResizeMode.NoResize,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner, Owner = System.Windows.Application.Current?.MainWindow,
            Background = Brush("WindowBrush"), Foreground = Brush("TextBrush"),
        };
        ok.Click += (_, _) => window.DialogResult = true;
        box.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Text : null;
    }

    public string? SaveFile(string title, string fileName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = fileName, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
