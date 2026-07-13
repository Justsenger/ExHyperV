using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 把归一化(0..100)的历史点集按实际渲染尺寸缩放到像素坐标，供 Polyline/Polygon 直接用。
    /// 取代 Viewbox Stretch=Fill 的非等比缩放——那会把描边拉粗变形(水平粗、斜线细)；
    /// 直接算实际坐标后描边恒定像素宽，波峰细腻(似任务管理器)。
    /// 入参：[0]=归一化 PointCollection，[1]=实际宽，[2]=实际高。尺寸未就绪(≤0)返回 null(暂不绘制)。
    /// </summary>
    public class ScalePointsConverter : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 3 || values[0] is not PointCollection src
                || values[1] is not double w || values[2] is not double h || w <= 0 || h <= 0)
                return null;

            var dst = new PointCollection(src.Count);
            foreach (var p in src)
                dst.Add(new Point(p.X / 100.0 * w, p.Y / 100.0 * h));
            dst.Freeze();
            return dst;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
