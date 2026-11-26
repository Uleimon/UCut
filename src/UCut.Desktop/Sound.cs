using System.ComponentModel;

namespace UCut.Desktop;

/// <summary>
/// One volume for every player in UCut (gallery, Twitch preview, cut room, fullscreen). Bound from XAML with
/// {Binding Volume, Source={x:Static local:Sound.Current}}; MainViewModel loads and saves it.
/// </summary>
public sealed class Sound : INotifyPropertyChanged
{
    public static Sound Current { get; } = new();

    private double _volume = 0.3;

    /// <summary>0 (silent) to 1.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _volume) < 0.0005)
            {
                return;
            }

            _volume = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            Changed?.Invoke();
        }
    }

    /// <summary>Speaker glyph (Segoe Fluent Icons): muted, low, loud.</summary>
    public string Icon => _volume <= 0.001 ? "" : _volume < 0.5 ? "" : "";

    public string Text => $"Lautstärke {_volume * 100:0} %";

    public event Action? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;
}
