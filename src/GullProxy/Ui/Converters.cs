using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GullProxy.Ui;

/// <summary>true → Wrap, false → NoWrap. Drives the body word-wrap toggle.</summary>
public sealed class BoolToWrapConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
