using System;

namespace ExHyperV.Tools
{
    public static class LayoutHelper
    {
        /// <summary>
        /// 根据项目总数计算出最优的列数，使布局尽可能接近矩形。
        /// </summary>
        /// <param name="count">项目总数</param>
        /// <returns>最佳列数</returns>
        public static int CalculateOptimalColumns(int count)
        {
            if (count <= 1) return 1;
            if (count <= 3) return count;
            if (count == 4) return 2;
            if (count <= 6) return 3;
            if (count == 8) return 4;

            double sqrt = Math.Sqrt(count);

            // 如果是完美的正方形 (e.g., 9, 16, 25...)
            if (sqrt == (int)sqrt) return (int)sqrt;

            // 从平方根向下寻找最大的整数因数，这样可以得到更宽的矩形
            int startingPoint = (int)sqrt;
            for (int i = startingPoint; i >= 2; i--)
            {
                if (count % i == 0)
                {
                    return count / i; // 返回较大的那个因数作为列数
                }
            }

            // 如果找不到因数 (质数)，则返回接近平方根的列数
            return (int)Math.Ceiling(sqrt);
        }
    }
}