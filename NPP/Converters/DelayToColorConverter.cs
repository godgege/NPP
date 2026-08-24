using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Netch.App.Converters;

public class DelayToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not int delay)
            return new SolidColorBrush(Colors.Gray);

        return delay switch
        {
            <= 0 => new SolidColorBrush(Colors.Gray),
            <= 80 => new SolidColorBrush(Colors.Green),
            <= 200 => new SolidColorBrush(Colors.Orange),
            _ => new SolidColorBrush(Colors.Red)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
