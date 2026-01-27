using System;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using ExHyperV.Tools;

// 必须添加这个命名空间包裹，且名称必须与 XAML 中的 clr-namespace 一致
namespace ExHyperV.Converters
{
    public class OsTypeToImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            // 调用 Utils 获取图片名称
            string imageName = Utils.GetOsImageName(value?.ToString());

            try
            {
                return new BitmapImage(new Uri($"pack://application:,,,/Assets/{imageName}"));
            }
            catch
            {
                // 如果图片路径报错的回退方案
                return new BitmapImage(new Uri("pack://application:,,,/Assets/microsoft.png"));
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}