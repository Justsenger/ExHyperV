using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ExHyperV.Converters
{
    public class OsTypeToImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 统一转为小写
            string osType = value?.ToString()?.ToLower() ?? "default";

            string imageName = osType switch
            {
                // Windows (对应 microsoft.png)
                "windows" => "microsoft.png",

                // Linux 发行版
                "linux" => "linux.png",
                "android" => "android.png",
                "chromeos" => "chromeos.png",
                "fydeos" => "fydeos.png",

                // Apple
                "macos" => "macos.png",

                // BSD
                "freebsd" => "freebsd.png",
                "openbsd" => "openbsd.png",

                // 软路由/NAS
                "openwrt" => "openwrt.png",
                "fnos" => "fnos.png",

                // 默认
                _ => "default.png"
            };

            return new BitmapImage(new Uri($"pack://application:,,,/Assets/{imageName}"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}