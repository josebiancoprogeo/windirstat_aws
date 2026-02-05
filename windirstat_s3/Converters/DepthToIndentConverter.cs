using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace windirstat_s3.Converters;

public class DepthToIndentConverter : IValueConverter
{
    public double IndentSize { get; set; } = 19;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int depth)
        {
            return new Thickness(depth * IndentSize, 0, 0, 0);
        }
        return new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
