using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ExHyperV.Services;

internal enum GpuFilePromotionMode
{
    PreserveExisting,
    WhenNewer,
    Overwrite
}

internal sealed record GpuFilePromotionRule(
    string SourceName,
    string TargetName,
    string TargetSubDir,
    GpuFilePromotionMode Mode = GpuFilePromotionMode.PreserveExisting,
    IReadOnlyList<string>? PreferredPackageNames = null,
    bool RequirePreferredPackage = false);

internal sealed record GpuRegistryValueSpec(
    string Name,
    string Kind,
    string? StringValue = null,
    string[]? MultiStringValue = null,
    int? DwordValue = null,
    byte[]? BinaryValue = null)
{
    internal RegistryValueKind ToRegistryValueKind() => Kind switch
    {
        nameof(RegistryValueKind.ExpandString) => RegistryValueKind.ExpandString,
        nameof(RegistryValueKind.MultiString) => RegistryValueKind.MultiString,
        nameof(RegistryValueKind.DWord) => RegistryValueKind.DWord,
        nameof(RegistryValueKind.Binary) => RegistryValueKind.Binary,
        _ => RegistryValueKind.String
    };

    internal object ToRegistryObject() => Kind switch
    {
        nameof(RegistryValueKind.ExpandString) => StringValue ?? string.Empty,
        nameof(RegistryValueKind.MultiString) => MultiStringValue ?? Array.Empty<string>(),
        nameof(RegistryValueKind.DWord) => DwordValue ?? 0,
        nameof(RegistryValueKind.Binary) => BinaryValue ?? Array.Empty<byte>(),
        _ => StringValue ?? string.Empty
    };
}

internal sealed record GpuNvidiaServiceSpec(IReadOnlyList<GpuRegistryValueSpec> Values);

internal sealed record GpuIntelRuntimeSpec(
    string DisplayClassSubKeyName,
    IReadOnlyList<GpuRegistryValueSpec> RuntimeValues,
    string? LevelZeroDriverPath);

internal sealed record GpuProgramFolderSpec(
    string SourcePath,
    string RootTag,
    string RelativePath,
    string IsoSubDirectory);

internal static class GpuDriverPromotionBuilder
{
    internal const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] IntelGpuRuntimeRegistryValueNames =
    {
        "OpenCLDriverName",
        "OpenCLDriverNameWow",
        "LevelZeroDriverPath",
        "DriverStorePathForComputeRuntime",
        "DriverStorePathForMediaSDK",
        "DriverStorePathForVPL",
        "DriverStorePathForMDF",
        "VulkanDriverName",
        "VulkanDriverNameWow",
        "OpenGLDriverName",
        "OpenGLDriverNameWow",
        "OpenGLFlags",
        "OpenGLFlagsWow",
        "OpenGLInstalled",
        "OpenGLVersion",
        "OpenGLVersionWow",
        "ContentProtectionDriverName",
        "ControlApiPath",
        "ControlApiPath32"
    };

    internal static IReadOnlyList<GpuFilePromotionRule> CollectRegistryPromotions(
        GpuDriverPackagePlan packagePlan,
        bool includeWow64)
    {
        var rules = new List<GpuFilePromotionRule>();
        string classGuidPath = $@"SYSTEM\CurrentControlSet\Control\Class\{DisplayClassGuid}";
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? classKey = baseKey.OpenSubKey(classGuidPath);
        if (classKey == null || string.IsNullOrEmpty(packagePlan.DisplayClassSubKeyName))
            return rules;

        using RegistryKey? selectedKey = classKey.OpenSubKey(packagePlan.DisplayClassSubKeyName);
        if (selectedKey == null)
            throw new InvalidOperationException("The selected display-class registry key could not be opened.");

        IReadOnlyList<string> preferred = packagePlan.PrimaryPackageNames.ToArray();
        CollectKey(selectedKey, "CopyToVmWhenNewer", "System32", GpuFilePromotionMode.WhenNewer, preferred, rules);
        CollectKey(selectedKey, "CopyToVmOverwrite", "System32", GpuFilePromotionMode.Overwrite, preferred, rules);
        if (includeWow64)
        {
            CollectKey(selectedKey, "CopyToVmWhenNewerWow64", "SysWOW64", GpuFilePromotionMode.WhenNewer, preferred, rules);
            CollectKey(selectedKey, "CopyToVmOverwriteWow64", "SysWOW64", GpuFilePromotionMode.Overwrite, preferred, rules);
        }

        return rules;
    }

    private static void CollectKey(
        RegistryKey adapterKey,
        string subKeyName,
        string targetSubDir,
        GpuFilePromotionMode mode,
        IReadOnlyList<string> preferredPackageNames,
        List<GpuFilePromotionRule> destination)
    {
        using RegistryKey? promotionKey = adapterKey.OpenSubKey(subKeyName);
        if (promotionKey == null) return;

        foreach (string valName in promotionKey.GetValueNames())
        {
            object? val = promotionKey.GetValue(valName);
            string? sourceSearch = null;
            string? targetLinkName = null;

            if (val is string[] pairs && pairs.Length > 0)
            {
                sourceSearch = pairs[0];
                targetLinkName = pairs.Length > 1 ? pairs[1] : pairs[0];
            }
            else if (val is string single)
            {
                sourceSearch = targetLinkName = single;
            }

            if (!string.IsNullOrEmpty(sourceSearch) && !string.IsNullOrEmpty(targetLinkName))
            {
                destination.Add(new GpuFilePromotionRule(sourceSearch, targetLinkName, targetSubDir, mode, preferredPackageNames));
            }
        }
    }

    internal static IReadOnlyList<GpuFilePromotionRule> GetVendorPromotions(GpuDriverPackagePlan packagePlan)
    {
        return packagePlan.Vendor switch
        {
            GpuDriverVendor.Nvidia => GetNvidiaPromotions(),
            GpuDriverVendor.Intel => GetIntelPromotions(),
            GpuDriverVendor.Amd => GetAmdPromotions(packagePlan),
            GpuDriverVendor.Qualcomm => GetQualcommPromotions(),
            _ => Array.Empty<GpuFilePromotionRule>()
        };
    }

    private static IReadOnlyList<GpuFilePromotionRule> GetNvidiaPromotions()
    {
        const string s32 = "System32";
        const string sw64 = "SysWOW64";
        const string lxss = @"System32\lxss\lib";
        return
        [
            new("MCU.exe", "MCU.exe", s32),
            new("nvapi64.dll", "nvapi64.dll", s32),
            new("nvcpl.dll", "nvcpl.dll", s32),
            new("nvcuda_loader64.dll", "nvcuda.dll", s32),
            new("nvcudadebugger.dll", "nvcudadebugger.dll", s32),
            new("nvcuvid64.dll", "nvcuvid.dll", s32),
            new("nvdebugdump.exe", "nvdebugdump.exe", s32),
            new("nvEncodeAPI64.dll", "nvEncodeAPI64.dll", s32),
            new("NvFBC64.dll", "NvFBC64.dll", s32),
            new("nvidia-pcc.exe", "nvidia-pcc.exe", s32),
            new("nvidia-smi.exe", "nvidia-smi.exe", s32),
            new("NvIFR64.dll", "NvIFR64.dll", s32),
            new("nvinfo.pb", "nvinfo.pb", s32),
            new("nvml_loader.dll", "nvml.dll", s32),
            new("nvofapi64.dll", "nvofapi64.dll", s32),
            new("OpenCL64.dll", "OpenCL.dll", s32),
            new("vulkan-1-x64.dll", "vulkan-1.dll", s32),
            new("vulkan-1-x64.dll", "vulkan-1-999-0-0-0.dll", s32),
            new("vulkaninfo-x64.exe", "vulkaninfo.exe", s32),

            new("license.txt", "license.txt", @"System32\drivers\NVIDIA Corporation"),
            new("dbInstaller.exe", "dbInstaller.exe", @"System32\drivers\NVIDIA Corporation\Drs"),
            new("nvdrsdb.bin", "nvdrsdb.bin", @"System32\drivers\NVIDIA Corporation\Drs"),

            new("libcuda_loader.so", "libcuda.so", lxss),
            new("libcuda_loader.so", "libcuda.so.1", lxss),
            new("libcuda_loader.so", "libcuda.so.1.1", lxss),
            new("libcudadebugger.so.1", "libcudadebugger.so.1", lxss),
            new("libnvcuvid.so.1", "libnvcuvid.so", lxss),
            new("libnvcuvid.so.1", "libnvcuvid.so.1", lxss),
            new("libnvdxdlkernels.so", "libnvdxdlkernels.so", lxss),
            new("libnvidia-encode.so.1", "libnvidia-encode.so", lxss),
            new("libnvidia-encode.so.1", "libnvidia-encode.so.1", lxss),
            new("libnvidia-ml_loader.so", "libnvidia-ml.so.1", lxss),
            new("libnvidia-ngx.so.1", "libnvidia-ngx.so.1", lxss),
            new("libnvidia-opticalflow.so.1", "libnvidia-opticalflow.so", lxss),
            new("libnvidia-opticalflow.so.1", "libnvidia-opticalflow.so.1", lxss),
            new("libnvoptix_loader.so.1", "libnvoptix.so.1", lxss),
            new("libnvwgf2umx.so", "libnvwgf2umx.so", lxss),
            new("nvidia-ngx-updater", "nvidia-ngx-updater", lxss),
            new("nvidia-smi", "nvidia-smi", lxss),

            new("nvapi.dll", "nvapi.dll", sw64),
            new("nvcuda_loader32.dll", "nvcuda.dll", sw64),
            new("nvcuvid32.dll", "nvcuvid.dll", sw64),
            new("nvEncodeAPI.dll", "nvEncodeAPI.dll", sw64),
            new("NvFBC.dll", "NvFBC.dll", sw64),
            new("NvIFR.dll", "NvIFR.dll", sw64),
            new("nvofapi.dll", "nvofapi.dll", sw64),
            new("OpenCL32.dll", "OpenCL.dll", sw64),
            new("vulkan-1-x86.dll", "vulkan-1.dll", sw64),
            new("vulkan-1-x86.dll", "vulkan-1-999-0-0-0.dll", sw64),
            new("vulkaninfo-x86.exe", "vulkaninfo.exe", sw64)
        ];
    }

    private static IReadOnlyList<GpuFilePromotionRule> GetIntelPromotions()
    {
        const string s32 = "System32";
        const string sw64 = "SysWOW64";
        return
        [
            new("ControlLib.dll", "ControlLib.dll", s32),
            new("intel_gfx_api-x64.dll", "intel_gfx_api-x64.dll", s32),
            new("mfx_loader_dll_hw64.dll", "libmfxhw64.dll", s32),
            new("vpl_dispatcher_64.dll", "libvpl.dll", s32),
            new("mfxplugin64_hw.dll", "mfxplugin64_hw.dll", s32),
            new("vulkan-1-64.dll", "vulkan-1.dll", s32),
            new("vulkan-1-64.dll", "vulkan-1-999-0-0-0.dll", s32),
            new("vulkaninfo-64.exe", "vulkaninfo.exe", s32),
            new("vulkaninfo-64.exe", "vulkaninfo-1-999-0-0-0.exe", s32),
            new("ze_intel_gpu_raytracing.dll", "ze_intel_gpu_raytracing.dll", s32),
            new("ze_loader.dll", "ze_loader.dll", s32),
            new("ze_tracing_layer.dll", "ze_tracing_layer.dll", s32),
            new("ze_validation_layer.dll", "ze_validation_layer.dll", s32),

            new("ControlLib32.dll", "ControlLib32.dll", sw64),
            new("IntelControlLib32.dll", "IntelControlLib32.dll", sw64),
            new("intel_gfx_api-x86.dll", "intel_gfx_api-x86.dll", sw64),
            new("mfx_loader_dll_hw32.dll", "libmfxhw32.dll", sw64),
            new("vpl_dispatcher_32.dll", "libvpl.dll", sw64),
            new("mfxplugin32_hw.dll", "mfxplugin32_hw.dll", sw64),
            new("vulkan-1-32.dll", "vulkan-1.dll", sw64),
            new("vulkan-1-32.dll", "vulkan-1-999-0-0-0.dll", sw64),
            new("vulkaninfo-32.exe", "vulkaninfo.exe", sw64),
            new("vulkaninfo-32.exe", "vulkaninfo-1-999-0-0-0.exe", sw64)
        ];
    }

    private static IReadOnlyList<GpuFilePromotionRule> GetAmdPromotions(GpuDriverPackagePlan packagePlan)
    {
        const string s32 = "System32";
        const string sw64 = "SysWOW64";
        IReadOnlyList<string> selectedPackageNames = packagePlan.PrimaryPackageNames.ToArray();
        IReadOnlyList<string> openClPackageNames = packagePlan.AmdOpenClPackageNames.ToArray();
        IReadOnlyList<string> amdWinPackageNames = packagePlan.AmdWinPackageNames.ToArray();
        return
        [
            new("atidxxstub64.dll", "atidxx64.dll", s32),
            new("amdxcstub64.dll", "amdxc64.dll", s32),
            new("amdxc64.so", "amdxc64.so", s32),
            new("amdadlx64.dll", "amdadlx64.dll", s32),
            new("amdave64.dll", "amdave64.dll", s32),
            new("amdgfxinfo64.dll", "amdgfxinfo64.dll", s32),
            new("amdlvr64.dll", "amdlvr64.dll", s32),
            new("amdpcom64.dll", "amdpcom64.dll", s32),
            new("amfrt64.dll", "amfrt64.dll", s32),
            new("atiadlxx.dll", "atiadlxx.dll", s32),
            new("atimpc64.dll", "atimpc64.dll", s32),
            new("atisamu64.dll", "atisamu64.dll", s32),
            new("amdsasrv64.dll", "amdsasrv64.dll", s32),
            new("amdsacli64.dll", "amdsacli64.dll", s32),
            new("atieclxx.exe", "atieclxx.exe", s32),
            new("atieah64.exe", "atieah64.exe", s32),
            new("EEURestart.exe", "EEURestart.exe", s32),
            new("GameManager64.dll", "GameManager64.dll", s32),
            new("amdmiracast.dll", "amdmiracast.dll", s32, GpuFilePromotionMode.PreserveExisting, selectedPackageNames),
            new("amf-mft-mjpeg-decoder64.dll", "amf-mft-mjpeg-decoder64.dll", s32, GpuFilePromotionMode.PreserveExisting, selectedPackageNames),
            new("atidemgy.dll", "atidemgy.dll", s32, GpuFilePromotionMode.PreserveExisting, selectedPackageNames),
            new("atimuixx.dll", "atimuixx.dll", s32, GpuFilePromotionMode.PreserveExisting, selectedPackageNames),
            new("amd_comgr_2.dll", "amd_comgr_2.dll", s32, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("amdhip64_6.dll", "amdhip64_6.dll", s32, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("amdmmcl.dll", "amdmmcl.dll", s32, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("amdmmcl6.dll", "amdmmcl6.dll", s32, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("clinfo.exe", "clinfo.exe", s32, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("hiprt02000_amd.hipfb", "hiprt02000_amd.hipfb", s32, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("hiprt0200064.dll", "hiprt0200064.dll", s32, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("oro_compiled_kernels.hipfb", "oro_compiled_kernels.hipfb", s32, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("amdlogum.exe", "amdlogum.exe", s32, GpuFilePromotionMode.PreserveExisting, amdWinPackageNames, true),
            new("dgtrayicon.exe", "dgtrayicon.exe", s32, GpuFilePromotionMode.PreserveExisting, amdWinPackageNames, true),
            new("Rapidfire64.dll", "Rapidfire64.dll", s32, GpuFilePromotionMode.PreserveExisting, amdWinPackageNames, true),
            new("RapidFireServer64.dll", "RapidFireServer64.dll", s32, GpuFilePromotionMode.PreserveExisting, amdWinPackageNames, true),
            new("atiapfxx.blb", "atiapfxx.blb", s32),
            new("ativvsva.dat", "ativvsva.dat", s32),
            new("ativvsvl.dat", "ativvsvl.dat", s32),
            new("AMDKernelEvents.mc", "AMDKernelEvents.man", s32),
            new("detoured64.dll", "detoured.dll", s32),

            new("atidxxstub32.dll", "atidxx32.dll", sw64),
            new("amdxcstub32.dll", "amdxc32.dll", sw64),
            new("amdadlx32.dll", "amdadlx32.dll", sw64),
            new("amdave32.dll", "amdave32.dll", sw64),
            new("amdgfxinfo32.dll", "amdgfxinfo32.dll", sw64),
            new("amdlvr32.dll", "amdlvr32.dll", sw64),
            new("amdpcom32.dll", "amdpcom32.dll", sw64),
            new("amfrt32.dll", "amfrt32.dll", sw64),
            new("atimpc32.dll", "atimpc32.dll", sw64),
            new("atisamu32.dll", "atisamu32.dll", sw64),
            new("GameManager32.dll", "GameManager32.dll", sw64),
            new("atiadlxy.dll", "atiadlxx.dll", sw64),
            new("detoured32.dll", "detoured.dll", sw64),
            new("amdsacli32.dll", "amdsacli32.dll", sw64, GpuFilePromotionMode.PreserveExisting, selectedPackageNames),
            new("amf-mft-mjpeg-decoder32.dll", "amf-mft-mjpeg-decoder32.dll", sw64, GpuFilePromotionMode.PreserveExisting, selectedPackageNames),
            new("atiadlxy.dll", "atiadlxy.dll", sw64, GpuFilePromotionMode.PreserveExisting, selectedPackageNames),
            new("atieah32.exe", "atieah32.exe", sw64, GpuFilePromotionMode.PreserveExisting, selectedPackageNames),
            new("amd_comgr32.dll", "amd_comgr32.dll", sw64, GpuFilePromotionMode.PreserveExisting, openClPackageNames, true),
            new("Rapidfire.dll", "Rapidfire.dll", sw64, GpuFilePromotionMode.PreserveExisting, amdWinPackageNames, true),
            new("RapidFireServer.dll", "RapidFireServer.dll", sw64, GpuFilePromotionMode.PreserveExisting, amdWinPackageNames, true),
            new("atiapfxx.blb", "atiapfxx.blb", sw64),
            new("ativvsva.dat", "ativvsva.dat", sw64),
            new("ativvsvl.dat", "ativvsvl.dat", sw64),

            new("vulkan64.dll", "vulkan-1.dll", s32, GpuFilePromotionMode.WhenNewer, selectedPackageNames),
            new("vulkan64.dll", "vulkan-1-999-0-0-0.dll", s32, GpuFilePromotionMode.WhenNewer, selectedPackageNames),
            new("vulkaninfo64.exe", "vulkaninfo.exe", s32, GpuFilePromotionMode.WhenNewer, selectedPackageNames),
            new("vulkaninfo64.exe", "vulkaninfo-1-999-0-0-0.exe", s32, GpuFilePromotionMode.WhenNewer, selectedPackageNames),
            new("vulkan32.dll", "vulkan-1.dll", sw64, GpuFilePromotionMode.WhenNewer, selectedPackageNames),
            new("vulkan32.dll", "vulkan-1-999-0-0-0.dll", sw64, GpuFilePromotionMode.WhenNewer, selectedPackageNames),
            new("vulkaninfo32.exe", "vulkaninfo.exe", sw64, GpuFilePromotionMode.WhenNewer, selectedPackageNames),
            new("vulkaninfo32.exe", "vulkaninfo-1-999-0-0-0.exe", sw64, GpuFilePromotionMode.WhenNewer, selectedPackageNames)
        ];
    }

    private static IReadOnlyList<GpuFilePromotionRule> GetQualcommPromotions()
    {
        const string s32 = "System32";
        const string sw64 = "SysWOW64";
        const string sc32 = "SyChpe32";
        return
        [
            new("OpenCL.dll", "OpenCL.dll", s32),
            new("qcdxkmsuc8380.mbn", "qcdxkmsuc8380.mbn", s32),
            new("qcdxkmsucpurwa.mbn", "qcdxkmsucpurwa.mbn", s32),
            new("qchdcpumd8380.dll", "qchdcpumd8380.dll", s32),
            new("vulkan-1.dll", "vulkan-1.dll", s32, GpuFilePromotionMode.WhenNewer),
            new("vulkan-1.dll", "vulkan-1-999-0-0-0.dll", s32, GpuFilePromotionMode.WhenNewer),
            new("vulkaninfo.exe", "vulkaninfo.exe", s32, GpuFilePromotionMode.WhenNewer),
            new("vulkaninfo.exe", "vulkaninfo-1-999-0-0-0.exe", s32, GpuFilePromotionMode.WhenNewer),

            new("qcdx11x86um.dll", "qcdx11x86um.dll", sw64),
            new("qcdx12x86um.dll", "qcdx12x86um.dll", sw64),
            new("qcdxdmlx86.dll", "qcdxdmlx86.dll", sw64),
            new("qcdxsdx86.dll", "qcdxsdx86.dll", sw64),
            new("qcegpx86.dll", "qcegpx86.dll", sw64),
            new("qcgpux86compilercore.DLL", "qcgpux86compilercore.DLL", sw64),
            new("qcvidencx86um.DLL", "qcvidencum.DLL", sw64),

            new("qcdx11chpeum.dll", "qcdx11x86um.dll", sc32),
            new("qcdx12chpeum.dll", "qcdx12x86um.dll", sc32),
            new("qcdxdmlchpe.dll", "qcdxdmlx86.dll", sc32),
            new("qcdxsdchpe.dll", "qcdxsdx86.dll", sc32),
            new("qcegpchpe.dll", "qcegpx86.dll", sc32),
            new("qcgpuchpecompilercore.dll", "qcgpux86compilercore.DLL", sc32)
        ];
    }

    internal static GpuNvidiaServiceSpec? ReadNvidiaServiceSpec(GpuDriverPackagePlan packagePlan)
    {
        using RegistryKey hostBaseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? hostServiceKey = hostBaseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nvlddmkm");
        if (hostServiceKey == null) return null;

        string packageDirectory = Path.Combine(packagePlan.HostFileRepository, packagePlan.PrimaryPackageName);
        string[] driverFiles = Directory.GetFiles(packageDirectory, "nvlddmkm.sys", SearchOption.AllDirectories);
        if (driverFiles.Length != 1)
            throw new InvalidOperationException(
                $"The selected NVIDIA package must contain exactly one nvlddmkm.sys. Found {driverFiles.Length}: {packageDirectory}");

        string relativeDriverPath = Path.GetRelativePath(packagePlan.HostFileRepository, driverFiles[0]);
        if (relativeDriverPath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeDriverPath))
            throw new InvalidOperationException($"The selected NVIDIA kernel driver is outside FileRepository: {driverFiles[0]}");

        var values = new List<GpuRegistryValueSpec>();
        foreach (string valueName in new[] { "Type", "Start", "ErrorControl", "Group" })
        {
            object? value = hostServiceKey.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null)
                throw new InvalidOperationException($"The host nvlddmkm service is missing the required value {valueName}.");
            values.Add(ToSpec(valueName, value, hostServiceKey.GetValueKind(valueName)));
        }

        string guestImagePath = $@"\SystemRoot\System32\HostDriverStore\FileRepository\{relativeDriverPath}";
        values.Add(new GpuRegistryValueSpec("ImagePath", nameof(RegistryValueKind.ExpandString), StringValue: guestImagePath));
        return new GpuNvidiaServiceSpec(values);
    }

    internal static GpuIntelRuntimeSpec? ReadIntelRuntimeSpec(GpuDriverPackagePlan packagePlan)
    {
        if (string.IsNullOrWhiteSpace(packagePlan.DisplayClassSubKeyName))
            throw new InvalidOperationException("Unable to map the selected Intel GPU-PV instance to its display-class registry key.");

        using RegistryKey hostBaseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? hostAdapterKey = hostBaseKey.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Control\Class\{DisplayClassGuid}\{packagePlan.DisplayClassSubKeyName}");
        if (hostAdapterKey == null)
            throw new InvalidOperationException("The selected Intel display-class registry key does not exist.");

        var runtimeValues = new List<GpuRegistryValueSpec>();
        foreach (string valueName in IntelGpuRuntimeRegistryValueNames)
        {
            object? value = hostAdapterKey.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) continue;

            RegistryValueKind valueKind;
            try { valueKind = hostAdapterKey.GetValueKind(valueName); }
            catch { continue; }

            runtimeValues.Add(ToSpec(valueName, RewriteDriverStoreRegistryValue(value), valueKind));
        }

        if (runtimeValues.Count == 0)
            throw new InvalidOperationException("The selected Intel driver does not expose GPU runtime registry values.");

        string? levelZero = runtimeValues
            .FirstOrDefault(item => item.Name.Equals("LevelZeroDriverPath", StringComparison.OrdinalIgnoreCase))
            ?.StringValue;
        return new GpuIntelRuntimeSpec(packagePlan.DisplayClassSubKeyName, runtimeValues, levelZero);
    }

    internal static IReadOnlyList<GpuProgramFolderSpec> CollectVendorProgramFolders(IEnumerable<GpuDriverVendor> vendors)
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        string[] tags = { "ProgramFiles", "ProgramFilesX86", "CommonApplicationData" };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var specs = new List<GpuProgramFolderSpec>();

        foreach (GpuDriverVendor vendor in vendors.Distinct())
        {
            string[] names = vendor switch
            {
                GpuDriverVendor.Nvidia => new[] { "NVIDIA Corporation", "NVIDIA" },
                GpuDriverVendor.Amd => new[] { "AMD", "ATI Technologies" },
                GpuDriverVendor.Intel => new[] { "Intel", "Intel Corporation" },
                GpuDriverVendor.Qualcomm => new[] { "Qualcomm", "Qualcomm Incorporated" },
                _ => Array.Empty<string>()
            };

            for (int r = 0; r < roots.Length; r++)
            {
                string root = roots[r];
                if (string.IsNullOrWhiteSpace(root)) continue;
                foreach (string name in names)
                {
                    string candidate = Path.Combine(root, name);
                    if (!Directory.Exists(candidate) || !seen.Add(candidate)) continue;
                    string rootPrefix = Path.GetPathRoot(candidate)
                        ?? throw new IOException($"Unable to determine the root of vendor directory: {candidate}");
                    string relToRoot = Path.GetRelativePath(rootPrefix, candidate);
                    string relToSpecialRoot = Path.GetRelativePath(root, candidate);
                    specs.Add(new GpuProgramFolderSpec(
                        candidate,
                        tags[r],
                        relToRoot,
                        Path.Combine("_payload", "ProgramFolders", tags[r], relToSpecialRoot)));
                }
            }
        }

        return specs;
    }

    internal static object RewriteDriverStoreRegistryValue(object value)
    {
        static string Rewrite(string text) => Regex.Replace(
            text,
            @"(?i)([\\/])System32([\\/])DriverStore([\\/])",
            "$1System32$2HostDriverStore$3");

        return value switch
        {
            string text => Rewrite(text),
            string[] texts => texts.Select(Rewrite).ToArray(),
            _ => value
        };
    }

    private static GpuRegistryValueSpec ToSpec(string name, object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.ExpandString => new(name, nameof(RegistryValueKind.ExpandString), StringValue: Convert.ToString(value)),
        RegistryValueKind.MultiString => new(name, nameof(RegistryValueKind.MultiString), MultiStringValue: value as string[] ?? Array.Empty<string>()),
        RegistryValueKind.DWord => new(name, nameof(RegistryValueKind.DWord), DwordValue: Convert.ToInt32(value)),
        RegistryValueKind.Binary => new(name, nameof(RegistryValueKind.Binary), BinaryValue: value as byte[] ?? Array.Empty<byte>()),
        _ => new(name, nameof(RegistryValueKind.String), StringValue: Convert.ToString(value))
    };
}
