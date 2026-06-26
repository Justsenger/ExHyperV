using System.Diagnostics;
using System.Windows;

namespace ExHyperV.Interaction
{
    /// <summary>
    /// 操作系统外壳门面：剪贴板 / 资源管理器定位 / 默认浏览器。
    /// VM 调用本类，由它集中处理 try-catch；VM 不再直接碰 Clipboard / Process.Start。
    /// </summary>
    public static class Shell
    {
        /// <summary>复制文本到剪贴板。剪贴板被其它进程占用时会抛 COMException，此处静默吞掉并返回是否成功。</summary>
        public static bool CopyToClipboard(string text)
        {
            try { Clipboard.SetText(text); return true; }
            catch { return false; }
        }

        /// <summary>在资源管理器中定位路径：是文件则 /select 高亮，是目录则直接打开，否则退而打开其父目录。失败静默。</summary>
        public static void Reveal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (System.IO.File.Exists(path))
                {
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                else if (System.IO.Directory.Exists(path))
                {
                    Process.Start("explorer.exe", path);
                }
                else
                {
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                        Process.Start("explorer.exe", dir);
                }
            }
            catch { }
        }

        /// <summary>用系统默认程序打开 URL 或路径（UseShellExecute）。失败静默。</summary>
        public static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }
}
