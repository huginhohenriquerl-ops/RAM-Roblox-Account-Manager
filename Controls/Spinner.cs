using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RAM.Controls;

/// <summary>The Claude Code "thinking" glyph: · ✢ ✳ ✶ ✻ ✽ cycling back and forth.</summary>
public sealed class Spinner : TextBlock
{
    static readonly string[] Frames = { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    int _frame;

    public Spinner()
    {
        Text = Frames[0];
        _timer.Tick += (_, _) => Text = Frames[_frame = (_frame + 1) % Frames.Length];
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => { if (IsVisible) _timer.Start(); };
    }
}

/// <summary>Loads remote avatar/game images once and shares them between every binding.</summary>
public sealed class UrlToImageConverter : IValueConverter
{
    static readonly System.Collections.Generic.Dictionary<string, BitmapImage> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        if (Cache.TryGetValue(url, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.DecodePixelWidth = 128;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            Cache[url] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
