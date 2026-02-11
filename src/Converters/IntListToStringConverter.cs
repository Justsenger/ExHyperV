using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 值转换器：用于在 整数列表 (List<int>) 与 逗号分隔的字符串 (string) 之间进行双向转换。
    /// 主要用途：允许用户在 UI 的一个文本框（TextBox）中，方便地查看和编辑类似 TrunkAllowedVlanIds 这样的 ID 列表。
    /// </summary>
    public class IntListToStringConverter : IValueConverter
    {
        /// <summary>
        /// 正向转换：将数据源 (ViewModel) 的 List<int> 转换为 UI (View) 的 string。
        /// 例如：后台的 List<int>{10, 20, 30} 会被转换为前台文本框中显示的 "10,20,30"。
        /// </summary>
        /// <param name="value">绑定的数据源，期望是 List<int> 类型。</param>
        /// <param name="targetType">目标类型，此处应为 string。</param>
        /// <param name="parameter">转换器参数（未使用）。</param>
        /// <param name="culture">区域性信息（未使用）。</param>
        /// <returns>转换后的字符串。</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 检查传入的值是否确实是一个整数列表
            if (value is List<int> intList)
            {
                // 如果是，使用 string.Join 方法，以逗号为分隔符，将列表中的所有数字高效地拼接成一个字符串。
                return string.Join(",", intList);
            }

            // 如果传入的值不是 List<int> 或为 null，则返回一个空字符串，避免界面显示异常。
            return string.Empty;
        }

        /// <summary>
        /// 反向转换：将 UI (View) 的 string 转换回数据源 (ViewModel) 的 List<int>。
        /// 例如：用户在文本框中输入 "10, 20, 30"，后台的属性会更新为 List<int>{10, 20, 30}。
        /// </summary>
        /// <param name="value">从 UI 控件传回的值，期望是 string 类型。</param>
        /// <param name="targetType">目标类型，此处应为 List<int>。</param>
        /// <param name="parameter">转换器参数（未使用）。</param>
        /// <param name="culture">区域性信息（未使用）。</param>
        /// <returns>转换后的整数列表。</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 检查传入的值是否是一个字符串
            if (value is string str)
            {
                // 如果字符串是空的或仅包含空白字符，直接返回一个新的空列表。
                if (string.IsNullOrWhiteSpace(str))
                {
                    return new List<int>();
                }

                // 转换过程 (使用 LINQ 链式调用，非常优雅)：
                var intList = str.Split(',')                  // 1. 使用逗号将字符串分割成一个子字符串数组。
                                 .Select(s => s.Trim())       // 2. 对每个子字符串，使用 Trim() 去除其前后的空格。
                                 .Where(s => int.TryParse(s, out _)) // 3. 筛选数组，只保留那些可以被成功解析为整数的子字符串（这可以过滤掉空字符串或无效输入如 "abc"）。
                                 .Select(int.Parse)           // 4. 将所有有效的数字字符串解析成真正的整数。
                                 .ToList();                  // 5. 将所有转换后的整数收集到一个新的 List<int> 中。

                return intList;
            }

            // 如果传入的值不是 string 或为 null，同样返回一个空的列表，保证程序的健壮性。
            return new List<int>();
        }
    }
}