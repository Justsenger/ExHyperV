using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>在整数列表与逗号分隔文本之间转换。</summary>
    public class IntListToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is List<int> intList)
            {
                return string.Join(",", intList);
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str)
            {
                if (string.IsNullOrWhiteSpace(str))
                {
                    return new List<int>();
                }

                var intList = str.Split(',')
                                 .Select(s => s.Trim())
                                 .Where(s => int.TryParse(s, out _))
                                 .Select(int.Parse)
                                 .ToList();

                return intList;
            }

            return new List<int>();
        }
    }
}
