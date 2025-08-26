using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public class InversePercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double percentage)
            {
                // 返回一个 GridLength，表示剩余的比例
                double inversePercentage = 100.0 - percentage;
                if (inversePercentage < 0) inversePercentage = 0;
                return new GridLength(inversePercentage, GridUnitType.Star);
            }
            // 如果输入无效，返回一个占据所有剩余空间的 GridLength
            return new GridLength(1, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}