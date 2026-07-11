using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ExHyperV.Tools;

// bcdedit 封装：读某元素值 / 提权写。集中此处，免各 service 各写一份 ProcessStartInfo。
internal static class Bcdedit
{
    // 读 {current} 项某元素的值（如 hypervisorlaunchtype→"Off"/"Auto"）。不存在/失败返回 null。
    // 键名与值均不本地化，解析安全；bcdedit /enum 需管理员。
    public static string? ReadValue(string element)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = "/enum {current}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith(element, StringComparison.OrdinalIgnoreCase))
                    return t.Substring(element.Length).Trim();
            }
            return null;
        }
        catch (Exception ex) { Debug.WriteLine($"[Bcdedit] read {element} failed: {ex.Message}"); return null; }
    }

    // 提权写 {current} 项某元素（bcdedit /set <element> <value>）。成功返回 true。
    // Verb=runas 与 RedirectStandardOutput 互斥，故写不重定向输出；App 恒提权时不再弹 UAC。
    public static async Task<bool> SetValueAsync(string element, string value)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = $"/set {element} {value}",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex) { Debug.WriteLine($"[Bcdedit] set {element} failed: {ex.Message}"); return false; }
    }
}
