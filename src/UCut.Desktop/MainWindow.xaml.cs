using System.Windows;
using UCut.Desktop.ViewModels;

namespace UCut.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // The cut room handles its own drops (open a video, add music).
        if (HasFiles(e) && DataContext is MainViewModel { Page: not "cutroom" })
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Handled || !HasFiles(e) || DataContext is not MainViewModel vm || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        if (vm.Page == "cutroom")
        {
            var audio = files.FirstOrDefault(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            _ = audio is null ? vm.CutRoom.OpenAsync(files[0]) : vm.CutRoom.AddMusicAsync(audio);

            return;
        }

        vm.AddFiles(files);
    }
}
