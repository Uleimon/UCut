using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UCut.Desktop.ViewModels;

namespace UCut.Desktop.Views;

public partial class CutRoomView : UserControl
{
    private const double FrameStep = 1.0 / 30;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private CutRoomViewModel? _vm;
    private bool _playing;
    private bool _dragging;

    public CutRoomView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        DataContextChanged += (_, _) => Attach(DataContext as CutRoomViewModel);
        Loaded += (_, _) => Focus();
        Unloaded += (_, _) => Pause();
    }

    private void Attach(CutRoomViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.SeekRequested -= OnSeekRequested;
            _vm.MusicChanged -= SyncMusic;
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.SeekRequested += OnSeekRequested;
            _vm.MusicChanged += SyncMusic;
            _vm.PropertyChanged += OnVmChanged;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CutRoomViewModel.SourceUri))
        {
            // A new video: show its first frame (MediaElement only opens the file on Play/Pause).
            Pause();
            Dispatcher.BeginInvoke(() => Player.Pause(), DispatcherPriority.Background);
        }
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            Player.Position = TimeSpan.FromSeconds(_vm.PlayheadSourceTime);
        }
    }

    private void OnSeekRequested(double sourceSeconds)
    {
        Player.Position = TimeSpan.FromSeconds(sourceSeconds);
        SyncMusic();
    }

    private void Tick()
    {
        if (_vm is null || !_playing)
        {
            return;
        }

        var jump = _vm.OnPlayback(Player.Position.TotalSeconds);
        if (jump is < 0)
        {
            Pause();
        }
        else if (jump is { } src)
        {
            Player.Position = TimeSpan.FromSeconds(src);
        }

        SyncMusic();
        KeepPlayheadVisible();
    }

    /// <summary>The music file plays alongside the video, from the music start on the timeline.</summary>
    private void SyncMusic()
    {
        if (_vm is null || _vm.MusicUri is null)
        {
            MusicPlayer.Stop();
            return;
        }

        MusicPlayer.Volume = _vm.MusicVolume;
        var musicPos = _vm.Playhead - _vm.MusicStart;
        if (!_playing || musicPos < 0)
        {
            MusicPlayer.Pause();
            return;
        }

        if (Math.Abs(MusicPlayer.Position.TotalSeconds - musicPos) > 0.25)
        {
            MusicPlayer.Position = TimeSpan.FromSeconds(musicPos);
        }

        MusicPlayer.Play();
    }

    private void KeepPlayheadVisible()
    {
        if (_vm is null)
        {
            return;
        }

        var x = _vm.PlayheadX;
        if (x < LaneScroll.HorizontalOffset || x > LaneScroll.HorizontalOffset + LaneScroll.ViewportWidth - 40)
        {
            LaneScroll.ScrollToHorizontalOffset(Math.Max(0, x - 80));
        }
    }

    private void Play()
    {
        if (_vm is not { HasProject: true })
        {
            return;
        }

        if (_vm.Playhead >= _vm.Duration - 0.05)
        {
            _vm.ClickTimeline(0, false);
        }

        Player.Position = TimeSpan.FromSeconds(_vm.PlayheadSourceTime);
        Player.Play();
        _playing = true;
        PlayButton.Content = "";
        _timer.Start();
        SyncMusic();
    }

    private void Pause()
    {
        Player.Pause();
        MusicPlayer.Pause();
        _playing = false;
        PlayButton.Content = "";
        _timer.Stop();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            Pause();
        }
        else
        {
            Play();
        }

        Focus();
    }

    private void OnToStart(object sender, RoutedEventArgs e) => _vm?.ClickTimeline(0, false);

    private void OnToEnd(object sender, RoutedEventArgs e) => _vm?.ClickTimeline(_vm.Duration * _vm.PixelsPerSecond, false);

    private void Step(double seconds)
    {
        if (_vm is null)
        {
            return;
        }

        Pause();
        _vm.ClickTimeline((_vm.Playhead + seconds) * _vm.PixelsPerSecond, false);
    }

    private void OnLaneDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Primitives.ScrollBar>(d) is not null)
        {
            return;
        }

        // Click on a meme bar: select it (and still move the playhead there).
        if (e.OriginalSource is FrameworkElement { DataContext: EffectRow row })
        {
            _vm?.SelectEffect(row);
        }

        _dragging = true;
        Lanes.CaptureMouse();
        var wasPlaying = _playing;
        Pause();
        _vm?.ClickTimeline(e.GetPosition(Lanes).X, true);
        if (wasPlaying)
        {
            Play();
        }

        Focus();
        e.Handled = true;
    }

    private void OnLaneMove(object sender, MouseEventArgs e)
    {
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            _vm?.ClickTimeline(Math.Max(0, e.GetPosition(Lanes).X), false);
        }
    }

    private void OnLaneUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Lanes.ReleaseMouseCapture();
    }

    private void OnLaneScroll(object sender, ScrollChangedEventArgs e) => HeaderScroll.ScrollToVerticalOffset(e.VerticalOffset);

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_vm is not { HasProject: true } || e.OriginalSource is TextBox or ComboBox)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        RelayCommand? cmd = e.Key switch
        {
            Key.S when !ctrl => _vm.SplitCommand,
            Key.Delete or Key.Back => _vm.DeleteCommand,
            Key.Q => _vm.TrimStartCommand,
            Key.W => _vm.TrimEndCommand,
            Key.Z when ctrl && shift => _vm.RedoCommand,
            Key.Z when ctrl => _vm.UndoCommand,
            Key.Y when ctrl => _vm.RedoCommand,
            Key.OemPlus or Key.Add => _vm.ZoomInCommand,
            Key.OemMinus or Key.Subtract => _vm.ZoomOutCommand,
            _ => null,
        };
        if (cmd is not null)
        {
            if (cmd.CanExecute(null))
            {
                cmd.Execute(null);
                OnSeekRequested(_vm.PlayheadSourceTime);
            }

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                OnPlayPause(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left:
                Step(shift ? -1 : -FrameStep);
                e.Handled = true;
                break;
            case Key.Right:
                Step(shift ? 1 : FrameStep);
                e.Handled = true;
                break;
            case Key.Home:
                OnToStart(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.End:
                OnToEnd(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null and not T)
        {
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }

        return d as T;
    }
}
