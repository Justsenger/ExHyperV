using System;
using System.Globalization;
using System.Windows.Data;
using ExHyperV.Models; // 请确保引用你的 SmtMode 所在的命名空间

namespace ExHyperV.Converters
{
    public class SmtModeToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SmtMode mode)
            {
                // 只有明确设置为 MultiThread 才显示为“开”
                return mode == SmtMode.MultiThread;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isChecked && isChecked)
            {
                return SmtMode.MultiThread;
            }
            return SmtMode.SingleThread;
        }
    }
}