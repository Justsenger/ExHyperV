namespace ExHyperV.Models;

/// <summary>
///     GPU device information
/// </summary>
public record GpuInfo(
    string Name,
    string Valid,
    string Manu,
    string InstanceId,
    string Pname,
    string Ram,
    string DriverVersion)
{
    // Factory method for creating objects with complete data
    public static List<GpuInfo> CreateGpuInfoList()
    {
        var gpuList = new List<GpuInfo>();

        // Collect basic GPU information
        var gpulinked = Utils.Run(
            "Get-WmiObject -Class Win32_VideoController | select PNPDeviceID,name,AdapterCompatibility,DriverVersion");

        var gpuBasicData = new Dictionary<string, GpuBasicInfo>();
        if (gpulinked.Count > 0)
        {
            var basicInfoList = gpulinked.Select(x => new
            {
                Name = x.Members["name"]?.Value?.ToString(),
                InstanceId = x.Members["PNPDeviceID"]?.Value?.ToString(),
                Manu = x.Members["AdapterCompatibility"]?.Value?.ToString(),
                DriverVersion = x.Members["DriverVersion"]?.Value?.ToString()
            }).Where(x => !string.IsNullOrEmpty(x.InstanceId));

            foreach (var info in basicInfoList)
                gpuBasicData[info.InstanceId!] = new GpuBasicInfo(
                    info.Name ?? string.Empty,
                    info.Manu ?? string.Empty,
                    info.DriverVersion ?? string.Empty);
        }

        // Collect RAM information
        var gpuRamData = new Dictionary<string, string>();
        const string ramScript = """
                                 Get-ItemProperty -Path "HKLM:\SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0*" -ErrorAction SilentlyContinue |
                                             Select-Object MatchingDeviceId,
                                                   @{Name="MemorySize"; Expression={
                                                       if ($_."HardwareInformation.qwMemorySize") {
                                                           $_."HardwareInformation.qwMemorySize"
                                                       } elseif ($_."HardwareInformation.MemorySize" -is [byte[]]) {
                                                           $hexString = [BitConverter]::ToString($_."HardwareInformation.MemorySize").Replace("-", "")
                                                           $memoryValue = [Convert]::ToInt64($hexString, 16)
                                                           $memoryValue * 16 * 1024 * 1024
                                                       } else {
                                                           $_."HardwareInformation.MemorySize"
                                                       }
                                                   }} |
                                             Where-Object { $_.MemorySize -ne $null }
                                 """;
        var gpuram = Utils.Run(ramScript);
        if (gpuram.Count > 0)
        {
            var ramInfoList = gpuram.Select(x => new
            {
                MatchingDeviceId = x.Members["MatchingDeviceId"]?.Value?.ToString()?.ToUpper(),
                MemorySize = x.Members["MemorySize"]?.Value?.ToString()
            }).Where(x => !string.IsNullOrEmpty(x.MatchingDeviceId) && !string.IsNullOrEmpty(x.MemorySize));

            foreach (var ramInfo in ramInfoList) gpuRamData[ramInfo.MatchingDeviceId!] = ramInfo.MemorySize!;
        }

        // Collect Pname information
        var gpuPnameData = new Dictionary<string, string>();
        var result3 = Utils.Run("Get-VMHostPartitionableGpu | select name");
        if (result3.Count > 0)
            foreach (var gpu in result3)
            {
                var pname = gpu.Members["Name"]?.Value?.ToString();
                if (string.IsNullOrEmpty(pname)) continue;

                // Find corresponding InstanceId
                var matchingKvp =
                    gpuBasicData.FirstOrDefault(x =>
                        pname.Contains(x.Key.Replace("\\", "#"), StringComparison.CurrentCultureIgnoreCase));
                if (matchingKvp.Key is not null) gpuPnameData[matchingKvp.Key] = pname;
            }

        // Create objects with complete data
        foreach (var (instanceId, basicInfo) in gpuBasicData)
        {
            var ram = "0";

            // Search for RAM by MatchingDeviceId
            var matchingRam = gpuRamData.FirstOrDefault(x => instanceId.Contains(x.Key));
            if (matchingRam.Key is not null) ram = matchingRam.Value;

            var pname = gpuPnameData.GetValueOrDefault(instanceId, string.Empty);

            gpuList.Add(new GpuInfo(
                basicInfo.Name,
                "True",
                basicInfo.Manu,
                instanceId,
                pname,
                ram,
                basicInfo.DriverVersion));
        }

        return gpuList;
    }
}

/// <summary>
///     Basic GPU information
/// </summary>
public record GpuBasicInfo(
    string Name,
    string Manu,
    string DriverVersion);

/// <summary>
///     GPU information for window selection
/// </summary>
public record Gpu(
    string Name,
    string Path,
    string IconPath,
    string InstanceId,
    string Manu);

/// <summary>
///     Virtual machine information
/// </summary>
public record VmInfo(
    string Name,
    Dictionary<string, string> GpUs);

/// <summary>
///     Device information for DDA
/// </summary>
public record DeviceInfo(
    string FriendlyName,
    string Status,
    string ClassType,
    string InstanceId,
    List<string> VmNames,
    string Path);

/// <summary>
///     GPU selection event arguments
/// </summary>
public class GpuSelectedEventArgs(string name, string machineName) : EventArgs
{
    public string Name { get; } = name;
    public string MachineName { get; } = machineName;
}