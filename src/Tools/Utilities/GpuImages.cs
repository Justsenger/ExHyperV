namespace ExHyperV.Tools
{
    /// <summary>
    /// GPU 厂商/型号 → 矢量资源键（Vector.Gpu.{厂商}）。
    /// GPU-PV 仅支持 NVIDIA / AMD / Intel / 高通；其余厂商回退 Default。
    /// </summary>
    public static class GpuImages
    {
        public static string GetResourceKey(string manufacturer, string name)
        {
            if (manufacturer.Contains("NVIDIA")) return "Gpu.NVIDIA";
            if (manufacturer.Contains("Advanced")) return "Gpu.AMD";
            if (manufacturer.Contains("Intel"))
            {
                string n = name.ToLowerInvariant();   // 不用 ToLower():土耳其语区域 'I' 会转成 'ı',"Iris" 认不出
                if (n.Contains("iris")) return "Gpu.Intel-IrisXe";
                if (n.Contains("arc")) return "Gpu.Intel-ARC";
                if (n.Contains("data")) return "Gpu.Intel-DataCenter";
                return "Gpu.Intel";
            }
            if (manufacturer.Contains("Qualcomm")) return "Gpu.Qualcomm";
            return "Gpu.Default";
        }
    }
}
