namespace ExHyperV.Tools
{
    /// <summary>
    /// GPU 厂商/型号 → 资源 pack:// URI（指向 Assets/ 下的 vendor logo PNG）。
    /// </summary>
    public static class GpuImages
    {
        public static string GetUri(string manufacturer, string name)
        {
            string imageName;
            if (manufacturer.Contains("NVIDIA"))
                imageName = "NVIDIA.png";
            else if (manufacturer.Contains("Advanced"))
                imageName = "AMD.png";
            else if (manufacturer.Contains("Microsoft"))
                imageName = "Microsoft.png";
            else if (manufacturer.Contains("Intel"))
            {
                imageName = "Intel.png";
                if (name.ToLower().Contains("iris")) imageName = "Intel-IrisXe.png";
                if (name.ToLower().Contains("arc")) imageName = "Intel-ARC.png";
                if (name.ToLower().Contains("data")) imageName = "Intel-DataCenter.png";
            }
            else if (manufacturer.Contains("Moore"))
                imageName = "Moore.png";
            else if (manufacturer.Contains("Qualcomm"))
                imageName = "Qualcomm.png";
            else if (manufacturer.Contains("DisplayLink"))
                imageName = "DisplayLink.png";
            else if (manufacturer.Contains("Silicon"))
                imageName = "Silicon.png";
            else
                imageName = "Default.png";

            return $"pack://application:,,,/Assets/{imageName}";
        }
    }
}
