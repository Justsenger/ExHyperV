using System.IO;
using System.Text.RegularExpressions;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Services;

internal enum GpuDriverVendor
{
    Unknown,
    Nvidia,
    Intel,
    Amd,
    Qualcomm
}

/// <summary>
/// 精确描述当前所选 GPU 的主包和映射依赖。
/// 部署会镜像完整 FileRepository；这个计划只负责限定注册表与文件链接来源，
/// 防止同名文件错误地链接到另一版本或另一块显卡的驱动包。
/// </summary>
internal sealed class GpuDriverPackagePlan
{
    public required GpuDriverVendor Vendor { get; init; }
    public required string DisplayClassSubKeyName { get; init; }
    public required string HostFileRepository { get; init; }
    public required string PrimaryPackageName { get; init; }
    public required IReadOnlyList<string> PackageNames { get; init; }
    public IReadOnlyList<string> AmdOpenClPackageNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AmdWinPackageNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> PrimaryPackageNames => new[] { PrimaryPackageName };
}

internal static class GpuDriverPackageResolver
{
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly Regex CopyInfDirective = new(
        @"^\s*CopyINF\s*=\s*(?<value>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GpuDriverPackagePlan Resolve(string gpuInstancePath, string gpuManufacturer)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string classSubKeyName = GetSelectedDisplayClassSubKeyName(baseKey, gpuInstancePath);
        if (string.IsNullOrWhiteSpace(classSubKeyName))
            throw new InvalidOperationException("Unable to map the selected GPU-PV instance to its display-class key.");

        using RegistryKey adapterKey = baseKey.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Control\Class\{DisplayClassGuid}\{classSubKeyName}")
            ?? throw new InvalidOperationException("The selected display-class registry key does not exist.");

        string publishedInfName = adapterKey.GetValue("InfPath")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(publishedInfName))
            throw new InvalidOperationException("The selected display adapter does not expose its published INF name.");

        string primaryInfPath = Win32Api.GetInfDriverStoreLocation(publishedInfName);
        DirectoryInfo primaryDirectory = GetPackageDirectory(primaryInfPath);
        DirectoryInfo fileRepository = primaryDirectory.Parent
            ?? throw new InvalidOperationException($"Invalid DriverStore package path: {primaryDirectory.FullName}");
        if (!fileRepository.Name.Equals("FileRepository", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SetupAPI returned a path outside DriverStore\\FileRepository: {primaryInfPath}");

        GpuDriverVendor vendor = ResolveVendor(
            gpuInstancePath,
            gpuManufacturer,
            adapterKey.GetValue("MatchingDeviceId")?.ToString(),
            adapterKey.GetValue("ProviderName")?.ToString());

        var packages = new List<DriverPackage>
        {
            new(primaryDirectory, new FileInfo(primaryInfPath))
        };
        IReadOnlyList<string> openClPackages = Array.Empty<string>();
        IReadOnlyList<string> amdWinPackages = Array.Empty<string>();
        var warnings = new List<string>();

        // 完整 FileRepository 会在复制阶段统一进入来宾。此处仍要精确定位 AMD 映射实际消费的
        // 两个独立软件组件包，供后续链接查找限定来源；CopyINF 中其他设备包无需建立 GPU 文件链接。
        if (vendor == GpuDriverVendor.Amd)
        {
            HashSet<string> declaredDependencies = ReadCopyInfNames(packages[0].Inf)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string? openClInfName = declaredDependencies.FirstOrDefault(name =>
                name.Equals("amdocl.inf", StringComparison.OrdinalIgnoreCase));
            string? amdWinInfName = declaredDependencies.FirstOrDefault(name =>
                name.StartsWith("amdwin-", StringComparison.OrdinalIgnoreCase));

            if (openClInfName != null)
            {
                try
                {
                    DriverPackage openCl = SelectExactCompanionPackage(
                        fileRepository,
                        packages[0],
                        openClInfName);
                    packages.Add(openCl);
                    openClPackages = new[] { openCl.Directory.Name };
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add($"[GPU Package] Optional AMD OpenCL package was not selected: {ex.Message}");
                }
            }

            if (amdWinInfName != null)
            {
                try
                {
                    DriverPackage amdWin = SelectExactCompanionPackage(
                        fileRepository,
                        packages[0],
                        amdWinInfName);
                    packages.Add(amdWin);
                    amdWinPackages = new[] { amdWin.Directory.Name };
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add($"[GPU Package] Optional AMD Windows-support package was not selected: {ex.Message}");
                }
            }
        }

        return new GpuDriverPackagePlan
        {
            Vendor = vendor,
            DisplayClassSubKeyName = classSubKeyName,
            HostFileRepository = fileRepository.FullName,
            PrimaryPackageName = primaryDirectory.Name,
            PackageNames = packages.Select(package => package.Directory.Name).ToArray(),
            AmdOpenClPackageNames = openClPackages,
            AmdWinPackageNames = amdWinPackages,
            Warnings = warnings
        };
    }

    private static DriverPackage SelectExactCompanionPackage(
        DirectoryInfo fileRepository,
        DriverPackage primaryPackage,
        string dependencyInfName)
    {
        string primaryDriverVersion = ReadInfDirective(primaryPackage.Inf, "DriverVer")
            ?? throw new InvalidOperationException(
                $"DriverVer is missing from the selected display INF: {primaryPackage.Inf.FullName}");
        var candidates = new List<DriverPackage>();

        foreach (DirectoryInfo directory in fileRepository.EnumerateDirectories())
        {
            if (!directory.Name.StartsWith($"{dependencyInfName}_", StringComparison.OrdinalIgnoreCase)) continue;

            FileInfo[] matchingInfs;
            try
            {
                matchingInfs = directory.GetFiles(dependencyInfName, SearchOption.AllDirectories);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            if (matchingInfs.Length != 1) continue;

            FileInfo inf = matchingInfs[0];
            string? driverVersion = ReadInfDirective(inf, "DriverVer");
            if (string.Equals(driverVersion, primaryDriverVersion, StringComparison.OrdinalIgnoreCase))
                candidates.Add(new DriverPackage(directory, inf));
        }

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                $"The declared companion {dependencyInfName} with DriverVer {primaryDriverVersion} is not installed."),
            _ => throw new InvalidOperationException(
                $"The declared companion {dependencyInfName} with DriverVer {primaryDriverVersion} is ambiguous.")
        };
    }

    private static IReadOnlyList<string> ReadCopyInfNames(FileInfo inf)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(inf.FullName))
        {
            Match match = CopyInfDirective.Match(StripInfComment(line));
            if (!match.Success) continue;

            foreach (string entry in match.Groups["value"].Value.Split(','))
            {
                string path = entry.Trim().Trim('"').Replace('/', '\\');
                string fileName = Path.GetFileName(path);
                if (fileName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) &&
                    fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                {
                    names.Add(fileName);
                }
            }
        }
        return names.ToArray();
    }

    private static string? ReadInfDirective(FileInfo inf, string directiveName)
    {
        foreach (string line in File.ReadLines(inf.FullName))
        {
            string content = StripInfComment(line);
            int separator = content.IndexOf('=');
            if (separator <= 0) continue;
            if (!content[..separator].Trim().Equals(directiveName, StringComparison.OrdinalIgnoreCase)) continue;
            return Regex.Replace(content[(separator + 1)..].Trim(), @"\s+", string.Empty);
        }
        return null;
    }

    private static string StripInfComment(string line)
    {
        int commentIndex = line.IndexOf(';');
        return commentIndex < 0 ? line : line[..commentIndex];
    }

    private static DirectoryInfo GetPackageDirectory(string infPath)
    {
        var inf = new FileInfo(infPath);
        if (!inf.Exists)
            throw new FileNotFoundException("SetupAPI returned an INF which does not exist.", infPath);
        return inf.Directory
            ?? throw new InvalidOperationException($"Invalid DriverStore INF path: {infPath}");
    }

    private static GpuDriverVendor ResolveVendor(
        string gpuInstancePath,
        string gpuManufacturer,
        string? matchingDeviceId,
        string? providerName)
    {
        string identity = string.Join('|', gpuInstancePath, gpuManufacturer, matchingDeviceId, providerName);
        if (identity.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return GpuDriverVendor.Nvidia;
        if (identity.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return GpuDriverVendor.Intel;
        if (identity.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(identity, @"(^|\|)AMD($|\|)", RegexOptions.IgnoreCase))
            return GpuDriverVendor.Amd;
        if (identity.Contains("VEN_17CB", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("QCOM", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase))
            return GpuDriverVendor.Qualcomm;
        return GpuDriverVendor.Unknown;
    }

    private static string GetSelectedDisplayClassSubKeyName(RegistryKey baseKey, string gpuInstancePath)
    {
        if (string.IsNullOrWhiteSpace(gpuInstancePath)) return string.Empty;

        string normalized = gpuInstancePath.Trim();
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized[4..];
        int suffixIndex = normalized.IndexOf('{');
        if (suffixIndex >= 0) normalized = normalized[..suffixIndex];
        string deviceId = normalized.Replace('#', '\\').TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;

        using RegistryKey? deviceKey = baseKey.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
        string driverKey = deviceKey?.GetValue("Driver")?.ToString() ?? string.Empty;
        string displayClassPrefix = $@"{DisplayClassGuid}\";
        if (!driverKey.StartsWith(displayClassPrefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        string subKeyName = driverKey[displayClassPrefix.Length..];
        return subKeyName.Contains('\\') ? string.Empty : subKeyName;
    }

    private sealed record DriverPackage(DirectoryInfo Directory, FileInfo Inf);
}
