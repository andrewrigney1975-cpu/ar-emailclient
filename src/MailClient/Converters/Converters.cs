using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;

namespace MailClient.Converters;

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool v && v;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isNull = value is null || (value is string s && s.Length == 0);
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            isNull = !isNull;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// Shows an element only when the bound count is greater than zero.
public sealed partial class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// Completed calendar items are dimmed.
public sealed partial class DoneToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool done && done ? 0.45 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// Unread messages get an accent dot and a bolder weight.
public sealed partial class ReadToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool read && read ? 0.55 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class ReadToWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool read && read ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// Favourite star: filled + accent when true, outline + muted when false.
public sealed partial class FavouriteGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class FavouriteBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// A base64-encoded image string to an ImageSource (empty -> null).
public sealed partial class Base64ToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string s || s.Length == 0)
        {
            return null;
        }

        try
        {
            var bytes = System.Convert.FromBase64String(s);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(bytes.AsBuffer()).AsTask().Wait();
            stream.Seek(0);
            image.SetSource(stream);
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// Selected message rows get an accent-tinted background.
public sealed partial class SelectionToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool sel && sel
            ? (Brush)Application.Current.Resources["AccentAcrylicBackgroundFillColorDefaultBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class ReadToDotBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool read && read
            ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
