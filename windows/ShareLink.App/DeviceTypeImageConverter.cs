using System.Globalization;
using System.Windows;
using System.Windows.Data;
namespace ShareLink;
public sealed class DeviceTypeImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.FindResource((value as string)?.Trim().ToLowerInvariant() switch
        {
            "phone" => "PhoneDrawing", "tablet" => "TabletDrawing",
            "computer" or "pc" or "2in1" => "ComputerDrawing", _ => "UnknownDeviceDrawing"
        });
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
