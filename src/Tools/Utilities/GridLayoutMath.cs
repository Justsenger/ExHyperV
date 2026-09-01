using System;

namespace ExHyperV.Tools
{
    public static class GridLayoutMath
    {
        /// <summary>选择接近方形且尽量无空位的列数。</summary>
        public static int CalculateOptimalColumns(int count)
        {
            if (count <= 1) return 1;
            if (count <= 3) return count;
            if (count == 4) return 2;
            if (count <= 6) return 3;
            if (count == 8) return 4;

            double sqrt = Math.Sqrt(count);

            if (sqrt == (int)sqrt) return (int)sqrt;

            int startingPoint = (int)sqrt;
            for (int i = startingPoint; i >= 2; i--)
            {
                if (count % i == 0)
                {
                    return count / i;
                }
            }

            return (int)Math.Ceiling(sqrt);
        }
    }
}
