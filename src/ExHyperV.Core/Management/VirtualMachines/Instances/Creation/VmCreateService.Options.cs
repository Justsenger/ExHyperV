using System.Globalization;
using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>校验创建参数并完成一台虚拟机的配置、存储和网络创建。</summary>
public static partial class VmCreateService
{
    /// <summary>宿主未提供配置目录时使用的 Hyper-V 默认配置根目录。</summary>
    public const string DefaultVmDirectory = @"C:\ProgramData\Microsoft\Windows\Hyper-V";
    /// <summary>Windows 的默认虚拟硬盘目录。</summary>
    public const string DefaultDiskDirectory = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks";
    /// <summary>当前操作系统架构是否支持第一代虚拟机；ARM64 为 false。</summary>
    public static bool SupportsGeneration1 => RuntimeInformation.OSArchitecture != Architecture.Arm64;

    internal static bool IsNoSwitchSelection(string? switchName) => string.IsNullOrWhiteSpace(switchName)
        || switchName == Properties.Resources.ResourceManager.GetString("Common_None", CultureInfo.GetCultureInfo("en-US"))
        || switchName == Properties.Resources.ResourceManager.GetString("Common_None", CultureInfo.GetCultureInfo("zh-CN"));

    /// <summary>并行读取创建所需的宿主能力；不代选配置版本、交换机或创建参数。</summary>
    /// <returns>宿主目录和可选能力；必要查询失败时返回原因。</returns>
    public static async Task<ApiResponse<VmCreationOptions>> GetCreationOptionsAsync()
    {
        var pathsTask = GetHostDefaultPathsAsync();
        var versionsTask = GetSupportedVersionsAsync();
        var isolationTask = GetIsolationSupportAsync();
        var switchesTask = VmNetworkService.GetAvailableSwitchesAsync();
        await Task.WhenAll(pathsTask, versionsTask, isolationTask, switchesTask);
        var paths = await pathsTask;
        var versions = await versionsTask;
        var isolation = await isolationTask;
        var switches = await switchesTask;
        if (!paths.HasData) return CreationQueryFailure<VmCreationOptions, VmHostDefaultPaths>(paths);
        if (!versions.HasData) return CreationQueryFailure<VmCreationOptions, List<string>>(versions);
        if (!isolation.HasData) return CreationQueryFailure<VmCreationOptions, VmIsolationSupport>(isolation);
        if (!switches.HasData) return CreationQueryFailure<VmCreationOptions, IReadOnlyDictionary<Guid, string>>(switches);
        return ApiResponse<VmCreationOptions>.Ok(new(paths.Data.DefaultVmPath, paths.Data.DefaultVhdPath,
            versions.Data, isolation.Data.Supported, isolation.Data.Types, switches.Data));
    }
}
