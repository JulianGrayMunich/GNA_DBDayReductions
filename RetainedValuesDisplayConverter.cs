#region Imports
using System.Globalization;
using System.Windows.Data;
#endregion
namespace GNA_DBDayReductions;
#region Retained Value Display Formatting
public sealed class RetainedValuesDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrEmpty(value: text)) return value;
        string[] values = text.Split(separator: ';');
        for (int index = 0; index < values.Length; index++)
            if (decimal.TryParse(s: values[index].Trim(), style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out decimal number))
                values[index] = number.ToString(format: "F4", provider: culture);
        return string.Join(separator: "; ", value: values);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException(message: "Review data is read-only.");
}
#endregion
