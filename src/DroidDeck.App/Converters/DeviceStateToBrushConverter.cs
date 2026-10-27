using DroidDeck.Core.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.Converters;

/// <summary>Maps a device state to a status brush.</summary>
public sealed partial class DeviceStateToBrushConverter : IValueConverter
{
    /// <summary>Converts a device state to a brush.</summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is DeviceState state
            ? state switch
            {
                DeviceState.Device => "SystemFillColorSuccessBrush",
                DeviceState.Unauthorized or DeviceState.Authorizing or DeviceState.Connecting
                    or DeviceState.Recovery or DeviceState.Sideload or DeviceState.Bootloader =>
                    "SystemFillColorCautionBrush",
                DeviceState.Offline or DeviceState.Disconnected => "SystemFillColorCriticalBrush",
                _ => "TextFillColorSecondaryBrush",
            }
            : "TextFillColorSecondaryBrush";

        return Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    /// <summary>Not supported.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
