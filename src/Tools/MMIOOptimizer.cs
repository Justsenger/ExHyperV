using System.Management;
using System.Diagnostics;
using System.Linq;

namespace ExHyperV.Tools
{
    public static class MMIOOptimizer
    {
        //  返回宿主物理寻址（单位：MB）
        private const string DetectionScript = @"
            $tmp = 'Probe_' + (Get-Random);
            New-VM $tmp -Gen 2 -NoVHD | Out-Null;
            Set-VM $tmp -AutomaticCheckpointsEnabled $false | Out-Null;
            $m = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_VirtualSystemManagementService;
            $vals = @(268435456, 134217728, 67108864, 16777216, 4194304, 1048576, 524288, 262144, 131072, 65536, 34816);
            $found = 34816;
            foreach ($v in $vals) {
                $p = (Get-CimInstance -Namespace root\virtualization\v2 -Filter ""ElementName='$tmp'"" -ClassName Msvm_VirtualSystemSettingData).Path;
                $s = [WMI]$p;
                $s.HighMmioGapBase = $v - 1024; # 顶格探测
                $s.HighMmioGapSize = 1024;
                $m.ModifySystemSettings($s.GetText(2)) | Out-Null;
                try {
                    Start-VM $tmp -EA Stop | Out-Null;
                    $found = $v;
                    Stop-VM $tmp -Turnoff -F | Out-Null;
                    while((Get-VM $tmp).State -ne 'Off') { Start-Sleep -s 1 };
                    break;
                } catch { }
            }
            Remove-VM $tmp -Force | Out-Null;
            Write-Output $found;";

public static async Task<bool> OptimizeVmAsync(string vmName)
{
    return await Task.Run(async () =>
    {
        try
        {
            var output = Utils.Run(DetectionScript);
            if (output == null || output.Count == 0 || !ulong.TryParse(output[0].ToString(), out ulong maxRangeMb))
            {
                maxRangeMb = 34816; // 8cx 安全回退
            }

            ulong baseMB = maxRangeMb / 2;

            ulong remainingSpace = maxRangeMb - baseMB - 1024;
            ulong highMmioSpace = Math.Min(remainingSpace, 131072); 

            using var serviceSearcher = new ManagementObjectSearcher(WmiTools.HyperVScope, "SELECT * FROM Msvm_VirtualSystemManagementService");
            using var service = serviceSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
            
            string query = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{vmName}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
            using var searcher = new ManagementObjectSearcher(WmiTools.HyperVScope, query);
            using var collection = searcher.Get();
            var vmSettings = collection.Cast<ManagementObject>().FirstOrDefault();

            if (vmSettings == null || service == null) return false;

            vmSettings["HighMmioGapBase"] = (ulong)baseMB;
            vmSettings["HighMmioGapSize"] = (ulong)highMmioSpace;
            vmSettings["LowMmioGapSize"] = (ulong)1024;
            vmSettings["GuestControlledCacheTypes"] = true;

            using var inParams = service.GetMethodParameters("ModifySystemSettings");
            inParams["SystemSettings"] = vmSettings.GetText((TextFormat)2);
            using var outParams = service.InvokeMethod("ModifySystemSettings", inParams, null);
            
            return (uint)outParams["ReturnValue"] == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MMIOOptimizer Fail: {ex.Message}");
            return false;
        }
    });
}    }
}