using System.Windows;
using System.Windows.Media;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 原生 WPF 矢量图标查找。资源定义在 Resources/VectorIcons.xaml，
    /// 键名形如 "Vector.{基名}"（基名与 PNG 文件名去扩展名一致）。
    /// 找不到对应矢量资源时返回 null，调用方负责回退到 PNG，从而支持逐个迁移。
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
