using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WSLC_Panel_App.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is Visibility.Visible;
    }
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is false ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is not Visibility.Visible;
    }
}

public class BoolToStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RunningBrush = new(Colors.ForestGreen);
    private static readonly SolidColorBrush StoppedBrush = new(Colors.Gray);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? RunningBrush : StoppedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 事件严重度 → 画刷。
///
/// 优先取系统的语义色（<c>SystemFillColor*Brush</c>，随浅/深主题自动切换）；
/// 取不到时退回一组中等明度的固定色，保证在任何主题下都还能看清，
/// 而不是直接抛异常把整页拖垮。
/// </summary>
public class EventSeverityBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CriticalFallback = new(Color.FromArgb(255, 0xD1, 0x34, 0x38));
    private static readonly SolidColorBrush CautionFallback = new(Color.FromArgb(255, 0xC0, 0x7A, 0x00));
    private static readonly SolidColorBrush SuccessFallback = new(Color.FromArgb(255, 0x10, 0x7C, 0x10));
    private static readonly SolidColorBrush NeutralFallback = new(Color.FromArgb(255, 0x8A, 0x8A, 0x8A));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var (resourceKey, fallback) = (value as string) switch
        {
            "critical" => ("SystemFillColorCriticalBrush", CriticalFallback),
            "caution" => ("SystemFillColorCautionBrush", CautionFallback),
            "success" => ("SystemFillColorSuccessBrush", SuccessFallback),
            _ => ("SystemFillColorNeutralBrush", NeutralFallback)
        };

        try
        {
            if (Application.Current?.Resources?.TryGetValue(resourceKey, out var found) == true &&
                found is Brush brush)
            {
                return brush;
            }
        }
        catch
        {
            // 资源字典查不到就走固定色
        }

        return fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
