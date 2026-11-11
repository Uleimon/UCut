using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UCut.Desktop.ViewModels;

namespace UCut.Desktop.Views;

public partial class TwitchView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _playing;
    private bool _fromTimer;
    private TwitchViewModel? _vm;

    public TwitchView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (Player.NaturalDuration.HasTimeSpan && !Seek.IsMouseCaptureWithin)
            {
                _fromTimer = true;
                Seek.Value = Player.Position.TotalSeconds / Math.Max(0.1, Player.NaturalDuration.TimeSpan.TotalSeconds);
                _fromTimer = false;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
            }

            _vm = DataContext as TwitchViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
            }
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            Player.Stop();
            _playing = false;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TwitchViewModel.PreviewUri))
        {
            // Preview downloaded: start playing.
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm?.PreviewUri is null)
                {
                    Player.Stop();
                    SetPlaying(false);
                }
                else
                {
                    Player.Play();
                    SetPlaying(true);
                }
            }, DispatcherPriority.Background);
        }
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e) => _timer.Start();

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        Player.Play();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            Player.Pause();
        }
        else
        {
            Player.Play();
        }

        SetPlaying(!_playing);
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayButton.Content = playing ? "" : "";
    }

    /// <summary>Any change of the slider by the user (click, drag, arrow keys) jumps there; changes from the timer don't.</summary>
    private void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_fromTimer && Player.NaturalDuration.HasTimeSpan)
        {
            Player.Position = TimeSpan.FromSeconds(Seek.Value * Player.NaturalDuration.TimeSpan.TotalSeconds);
        }
    }
}
