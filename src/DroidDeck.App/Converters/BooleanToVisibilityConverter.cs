using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace DroidDeck.App.Converters;

/// <summary>Converts a boolean to a visibility value.</summary>
public sealed partial class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>Converts a boolean to visibility.</summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is true;
        if (parameter is string text && string.Equals(text, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Not supported.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
