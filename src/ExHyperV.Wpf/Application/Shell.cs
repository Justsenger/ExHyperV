using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ExHyperV.Application
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

        /// <summary>在资源管理器中选中文件或打开目录；目标不存在时打开其直接父目录。系统可复用已有窗口。</summary>
        /// <returns>系统是否接受请求及失败原因；不表示用户已经看到窗口。</returns>
        public static Task<(bool Success, string Error)> RevealAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult((false, Properties.Resources.Shell_PathRequired));

            var completion = new TaskCompletionSource<(bool Success, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // Shell 路径解析可能阻塞，使用独立 STA；COM 和 PIDL 都在该线程结束前释放。
                var thread = new Thread(() =>
                {
                    try
                    {
                        RevealOnSta(path);
                        completion.SetResult((true, string.Empty));
                    }
                    catch (Exception error)
                    {
                        Debug.WriteLine($"[Explorer] {error}");
                        completion.SetResult((false, error.Message));
                    }
                }) { IsBackground = true, Name = "ExHyperV Explorer" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch (Exception error)
            {
                completion.SetResult((false, error.Message));
            }
            return completion.Task;
        }

        private static void RevealOnSta(string path)
        {
            string fullPath = Path.GetFullPath(path);
            int initialized = CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
            Marshal.ThrowExceptionForHR(initialized);
            try
            {
                if (File.Exists(fullPath))
                {
                    nint item = 0;
                    try
                    {
                        Marshal.ThrowExceptionForHR(SHParseDisplayName(fullPath, 0, out item, 0, out _));
                        if (item == 0) Marshal.ThrowExceptionForHR(unchecked((int)0x80004005));
                        Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, 0, 0));
                    }
                    finally
                    {
                        if (item != 0) Marshal.FreeCoTaskMem(item);
                    }
                    return;
                }

                // cidl=0 的目录 PIDL 会在上级目录中被选中；进入目录必须使用单独的打开操作。
                string? directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                    throw new DirectoryNotFoundException(string.Format(Properties.Resources.Shell_PathNotFound, fullPath));

                using Process? process = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true, Verb = "open" });
            }
            finally { CoUninitialize(); }
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(nint reserved, uint flags);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();
        [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributes, out uint returnedAttributes);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint children, uint flags);

        /// <summary>用系统默认程序打开 URL 或路径（UseShellExecute）。失败静默。</summary>
        public static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }
}
