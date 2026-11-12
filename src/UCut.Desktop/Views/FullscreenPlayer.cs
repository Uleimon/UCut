using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UCut.Desktop.Views;

/// <summary>
/// The clip on the whole screen (double-click on a player). Double-click or Esc goes back, Space pauses,
/// arrow keys jump 5 seconds, the bar at the bottom seeks.
/// </summary>
internal sealed class FullscreenPlayer : Window
{
    private readonly MediaElement _player;
    private readonly Slider _seek;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TimeSpan _start;
    private bool _playing = true;
    private bool _fromTimer;

    private FullscreenPlayer(Uri source, TimeSpan start)
    {
        _start = start;
        Title = "UCut";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Background = Brushes.Black;
        ShowInTaskbar = false;

        _player = new MediaElement { Source = source, LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
        _player.MediaOpened += (_, _) =>
        {
            _player.Position = _start;
            _timer.Start();
        };
        _player.MediaEnded += (_, _) =>
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
        };

        _seek = new Slider { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, Margin = new Thickness(24, 0, 24, 16), VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.85 };
        _seek.ValueChanged += (_, _) =>
        {
            if (!_fromTimer && _player.NaturalDuration.HasTimeSpan)
            {
                _player.Position = TimeSpan.FromSeconds(_seek.Value * _player.NaturalDuration.TimeSpan.TotalSeconds);
            }
        };
        _timer.Tick += (_, _) =>
        {
            if (_player.NaturalDuration.HasTimeSpan && !_seek.IsMouseCaptureWithin)
            {
                _fromTimer = true;
                _seek.Value = _player.Position.TotalSeconds / Math.Max(0.1, _player.NaturalDuration.TimeSpan.TotalSeconds);
                _fromTimer = false;
            }
        };

        var hint = new TextBlock
        {
            Text = "Doppelklick oder Esc: zurück · Leertaste: Pause · Pfeiltasten: 5 s springen",
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(24, 16, 24, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var grid = new Grid { Background = Brushes.Black };
        grid.Children.Add(_player);
        grid.Children.Add(hint);
        grid.Children.Add(_seek);
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                Close();
            }
        };
        Content = grid;

        KeyDown += OnKey;
        Loaded += (_, _) => _player.Play();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _player.Close();
        };
    }

    /// <summary>Where the clip was when fullscreen was closed.</summary>
    public TimeSpan Position { get; private set; }

    /// <summary>Shows the clip fullscreen from <paramref name="start"/>; returns where it stopped.</summary>
    public static TimeSpan Show(Window? owner, Uri? source, TimeSpan start)
    {
        if (source is null)
        {
            return start;
        }

        var w = new FullscreenPlayer(source, start) { Owner = owner };
        w.ShowDialog();
        return w.Position;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Space:
                if (_playing)
                {
                    _player.Pause();
                }
                else
                {
                    _player.Play();
                }

                _playing = !_playing;
                break;
            case Key.Left:
                _player.Position = TimeSpan.FromSeconds(Math.Max(0, _player.Position.TotalSeconds - 5));
                break;
            case Key.Right:
                _player.Position += TimeSpan.FromSeconds(5);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        Position = _player.Position;
        base.OnClosing(e);
    }
}
