using System.Globalization;
using System.Windows.Data;

namespace Aion2DPSPro.Overlay;
public sealed class ClassIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var c = value?.ToString();
        if (string.IsNullOrWhiteSpace(c) || c == "Unknown") return null;
        return new Uri($"pack://application:,,,/Assets/ClassIcons/{c}.png", UriKind.Absolute);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
