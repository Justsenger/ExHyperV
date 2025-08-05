using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging; 
using ExHyperV.Tools;

namespace ExHyperV.Converters
{
    public class GpuImagePathConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
            {
                return null; 
            }

            string manu = values[0] as string ?? "";
            string name = values[1] as string ?? "";

            // 1. 从 Utils 获取图片路径字符串
            string imagePath = Utils.GetGpuImagePath(manu, name);

            try
            {
                // 2. 创建一个 Uri 对象
                Uri imageUri = new Uri(imagePath, UriKind.Absolute);

                // 3. 创建并返回一个 BitmapImage 对象
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = imageUri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // 确保图片被加载
                bitmap.EndInit();

                return bitmap;
            }
            catch
            {
                // 如果路径无效或图片不存在，返回 null，避免程序崩溃
                return null;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}