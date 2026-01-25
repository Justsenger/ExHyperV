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
                // 只有显式为 SingleThread 时开关才是关闭状态
                return mode != SmtMode.SingleThread;
            }
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isChecked)
            {
                // 开关打开 -> 2线程 (Multi)，开关关闭 -> 1线程 (Single)
                return isChecked ? SmtMode.MultiThread : SmtMode.SingleThread;
            }
            return SmtMode.MultiThread;
        }
    }
}