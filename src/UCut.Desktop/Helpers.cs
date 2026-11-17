using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UCut.Desktop;

/// <summary>Windows shell actions: open files, show in Explorer, shut down, keep the PC awake.</summary>
internal static partial class Shell
{
    public static void Open(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public static void ShowInExplorer(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Opens a console that runs a command and stays open (winget installs).</summary>
    public static void RunInConsole(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k {command}") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public static void ShutdownIn(int seconds)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown", $"/s /t {seconds} /c \"UCut ist fertig. Der PC geht gleich aus. Abbrechen mit: shutdown /a\"")
            { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>While true, Windows does not go to sleep (screen may still turn off).</summary>
    public static void KeepAwake(bool on) => _ = SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    /// <summary>Loads a picture without locking the file (so clips can still be deleted or moved).</summary>
    public static ImageSource? LoadImage(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            if (decodeWidth > 0)
            {
                bmp.DecodePixelWidth = decodeWidth;
            }

            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is not null && value is not "") ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>RadioButton.IsChecked bound to a string property: checked when equal to ConverterParameter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

/// <summary>0..1 to percent text.</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? $"{d * 100:0} %" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
