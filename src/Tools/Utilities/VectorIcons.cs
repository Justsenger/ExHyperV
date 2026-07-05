using System.Windows;
using System.Windows.Media;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 原生 WPF 矢量图标查找。资源定义在 Resources/VectorIcons.xaml，键名形如 "Vector.{基名}"。
    /// 找不到对应资源时返回 null，由调用方决定如何处理缺失。
    /// </summary>
    public static class VectorIcons
    {
        public static ImageSource? TryGet(string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName)) return null;
            return Application.Current?.TryFindResource($"Vector.{baseName}") as ImageSource;
        }
    }
}
