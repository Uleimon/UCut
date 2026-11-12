using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UCut.Desktop.ViewModels;
using UCut.Editing;

namespace UCut.Desktop.Views;

/// <summary>"Create" dialog: draw the cam on a still picture, pick the layout, correct the caption words.</summary>
public partial class RedoWindow : Window
{
    private Point? _start;

    internal RedoWindow(RedoViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            await vm.LoadFrameAsync();
            ShowCam(vm.Cam);
        };
    }

    private RedoViewModel Vm => (RedoViewModel)DataContext;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Overlay);
        Overlay.CaptureMouse();
        Draw(_start.Value, _start.Value);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is { } s && e.LeftButton == MouseButtonState.Pressed)
        {
            Draw(s, e.GetPosition(Overlay));
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } s)
        {
            return;
        }

        Overlay.ReleaseMouseCapture();
        _start = null;
        var end = e.GetPosition(Overlay);
        double w = Overlay.ActualWidth, h = Overlay.ActualHeight;
        double x0 = Math.Clamp(Math.Min(s.X, end.X), 0, w), x1 = Math.Clamp(Math.Max(s.X, end.X), 0, w);
        double y0 = Math.Clamp(Math.Min(s.Y, end.Y), 0, h), y1 = Math.Clamp(Math.Max(s.Y, end.Y), 0, h);
        if (x1 - x0 < 12 || y1 - y0 < 12 || w <= 0 || h <= 0)
        {
            return; // a click, not a rectangle
        }

        Vm.Cam = new NormRect(Math.Round(x0 / w, 4), Math.Round(y0 / h, 4), Math.Round((x1 - x0) / w, 4), Math.Round((y1 - y0) / h, 4));
        ShowCam(Vm.Cam);
    }

    private void Draw(Point a, Point b)
    {
        CamBox.Visibility = Visibility.Visible;
        Canvas.SetLeft(CamBox, Math.Min(a.X, b.X));
        Canvas.SetTop(CamBox, Math.Min(a.Y, b.Y));
        CamBox.Width = Math.Abs(a.X - b.X);
        CamBox.Height = Math.Abs(a.Y - b.Y);
    }

    private void ShowCam(NormRect? cam)
    {
        if (cam is null)
        {
            CamBox.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateLayout();
        double w = Overlay.ActualWidth > 0 ? Overlay.ActualWidth : Vm.FrameWidth, h = Overlay.ActualHeight > 0 ? Overlay.ActualHeight : Vm.FrameHeight;
        Draw(new Point(cam.X * w, cam.Y * h), new Point((cam.X + cam.Width) * w, (cam.Y + cam.Height) * h));
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanRun(out var why))
        {
            Vm.Status = why;
            return;
        }

        DialogResult = true;
    }

    /// <summary>For the self-test: draws a cam without the mouse.</summary>
    internal void SetCamForTest(NormRect cam)
    {
        Vm.Cam = cam;
        ShowCam(cam);
    }
}
