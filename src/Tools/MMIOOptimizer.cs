using System.Management;
using System.Diagnostics;

public static class MMIOOptimizer
{
    private static readonly ManagementScope Scope = new ManagementScope(@"root\virtualization\v2");

    public static async Task<bool> OptimizeVmAsync(string vmName)
    {
        return await Task.Run(() =>
        {
            try
            {
                Scope.Connect();

                using var service = GetManagementService();
                if (service == null) throw new Exception("无法获取 Msvm_VirtualSystemManagementService");

                // 探测宿主物理极限
                ulong[] vals = { 1073741824, 268435456, 134217728, 67108864, 16777216, 4194304, 1048576, 524288, 262144, 131072, 65536, 34816 };
                ulong foundLimit = 34816;

                foreach (ulong v in vals)
                {
                    Debug.WriteLine($"[MMIO] 正在探测上限值: {v}");

                    using var vmSettings = GetRealizedVmSettings(vmName);
                    if (vmSettings == null) continue;

                    // 临时写入探测值进行启动测试
                    vmSettings["HighMmioGapBase"] = (ulong)(v - 1024);
                    vmSettings["HighMmioGapSize"] = (ulong)1024;

                    if (!ApplySettings(service, vmSettings)) continue;

                    if (RunPowerShellTryStart(vmName))
                    {
                        foundLimit = v;
                        Debug.WriteLine($"[MMIO] 探测成功，宿主上限确定为: {foundLimit}");
                        RunPowerShellStopAndWait(vmName);
                        break;
                    }
                }
                // 基础地址 = 1/2 上限
                ulong finalBase = foundLimit / 2;

                // 空间大小 = 128GB (131072MB) 与 (上限 - 基础地址 - 1GB) 的较小值 
                ulong remainingSpace = foundLimit - finalBase - 1024;
                ulong finalHighSize = Math.Min(remainingSpace, (ulong)131072);

                ulong finalLowSize = 1024; // 固定 1GB

                Debug.WriteLine($"[MMIO] 最终计算结果:");
                Debug.WriteLine($" - HighMmioGapBase: {finalBase}");
                Debug.WriteLine($" - HighMmioGapSize: {finalHighSize}");
                Debug.WriteLine($" - LowMmioGapSize: {finalLowSize}");

                using (var finalSettings = GetRealizedVmSettings(vmName))
                {
                    finalSettings["HighMmioGapBase"] = finalBase;
                    finalSettings["HighMmioGapSize"] = finalHighSize;
                    finalSettings["LowMmioGapSize"] = finalLowSize;
                    finalSettings["GuestControlledCacheTypes"] = true;

                    bool success = ApplySettings(service, finalSettings);
                    if (success) Debug.WriteLine("[MMIO] 最终配置已成功应用。");
                    return success;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MMIO] 错误: {ex.Message}");
                return false;
            }
        });
    }

    private static ManagementObject GetManagementService()
    {
        using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    private static ManagementObject GetRealizedVmSettings(string vmName)
    {
        string query = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName='{vmName}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
        using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery(query));
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    private static bool ApplySettings(ManagementObject service, ManagementObject vmSettings)
    {
        try
        {
            using var inParams = service.GetMethodParameters("ModifySystemSettings");
            inParams["SystemSettings"] = vmSettings.GetText((TextFormat)2);
            using var outParams = service.InvokeMethod("ModifySystemSettings", inParams, null);
            uint ret = (uint)outParams["ReturnValue"];
            return ret == 0 || ret == 4096;
        }
        catch { return false; }
    }

    private static bool RunPowerShellTryStart(string vmName)
    {
        string script = $"Start-VM -Name '{vmName}' -ErrorAction Stop";
        return ExecutePs(script);
    }

    private static void RunPowerShellStopAndWait(string vmName)
    {
        string script = $@"
            Stop-VM -Name '{vmName}' -Turnoff -Force -ErrorAction SilentlyContinue
            while((Get-VM -Name '{vmName}').State -ne 'Off') {{ Start-Sleep -Seconds 1 }}
        ";
        ExecutePs(script);
    }

    private static bool ExecutePs(string script)
    {
        using var process = new Process();
        process.StartInfo.FileName = "powershell.exe";
        process.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"try {{ {script}; exit 0 }} catch {{ exit 1 }}\"";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}