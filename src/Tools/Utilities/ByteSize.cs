namespace ExHyperV.Tools
{
    /// <summary>字节数 → 人类可读格式（1.5 GB / 256 MB / 0 B）。</summary>
    public static class ByteSize
    {
        public static string Format(long bytes)
        {
            if (bytes < 0) return "Invalid size";
            if (bytes == 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            int unitIndex = (int)Math.Floor(Math.Log(bytes, 1024));
            double number = bytes / Math.Pow(1024, unitIndex);
            string format = (unitIndex == 0) ? "F0" : "F2";
            return $"{number.ToString(format)} {units[unitIndex]}";
        }
    }
}
