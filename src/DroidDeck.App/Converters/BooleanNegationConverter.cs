using Microsoft.UI.Xaml.Data;

namespace DroidDeck.App.Converters;

/// <summary>Inverts a boolean value.</summary>
public sealed partial class BooleanNegationConverter : IValueConverter
{
    /// <summary>Returns the inverse of a boolean.</summary>
    public object Convert(object value, Type targetType, object parameter, string language) => value is not true;

    /// <summary>Returns the inverse of a boolean.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is not true;
}
