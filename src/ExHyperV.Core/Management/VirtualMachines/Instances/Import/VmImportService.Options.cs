using System.Collections.ObjectModel;
using ExHyperV.Models;

namespace ExHyperV.Services;

public static partial class VmImportService
{
    internal sealed record ImportPlan(string Name, string ConfigurationRoot, string DiskRoot,
        bool GenerateNewGuid, IReadOnlyDictionary<Guid, Guid?> SwitchMappings);

    internal static VmImportOptions SnapshotOptions(VmImportPlacementMode placement, VmImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(placement) || !Enum.IsDefined(options.GuidMode))
            throw new ArgumentException("导入放置方式或 GUID 策略无效。");
        if (placement == VmImportPlacementMode.ExistingDirectory &&
            (options.GuidMode != VmImportGuidMode.Preserve || options.ConfigurationDirectory != null || options.DiskDirectory != null))
            throw new ArgumentException("使用原目录注册必须保留原 GUID，且不能指定配置或磁盘目标目录。");
        if (options.Name != null && string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("导入名称不能只包含空白。");
        static string? DirectoryOption(string? path)
        {
            if (path == null) return null;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("导入目录必须是完整的绝对路径。");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        ArgumentNullException.ThrowIfNull(options.SwitchMappings);
        var mappings = new Dictionary<Guid, Guid?>();
        foreach (var (source, target) in options.SwitchMappings)
        {
            if (source == Guid.Empty || target == Guid.Empty)
                throw new ArgumentException("交换机映射不能使用空 GUID；断开连接请使用 null。");
            mappings.Add(source, target);
        }
        return new(options.GuidMode)
        {
            Name = options.Name,
            ConfigurationDirectory = DirectoryOption(options.ConfigurationDirectory),
            DiskDirectory = DirectoryOption(options.DiskDirectory),
            SwitchMappings = new ReadOnlyDictionary<Guid, Guid?>(mappings)
        };
    }

    internal static ImportPlan BuildImportPlan(VmImportSession session, VmImportOptions options,
        string defaultVmPath, string defaultDiskPath, IReadOnlySet<Guid> usedGuids,
        IReadOnlySet<Guid> availableSwitches)
    {
        options = SnapshotOptions(session.PlacementMode, options);
        ValidateImportGuid(session.Preview.OriginalGuid, options.GuidMode, usedGuids);
        foreach (var (source, target) in options.SwitchMappings)
        {
            if (!session.Preview.SourceSwitchIds.Contains(source))
                throw new ArgumentException($"来源预览没有交换机 {source:D}，请重新核对映射。");
            if (target is Guid destination && !availableSwitches.Contains(destination))
                throw new InvalidOperationException($"目标交换机 {destination:D} 不存在，未开始导入。");
        }
        string name = options.Name ?? session.Preview.Name;
        string config = session.SourceRoot, disk = session.SourceRoot;
        if (session.PlacementMode == VmImportPlacementMode.HostDirectories)
        {
            string suffix = SanitizeFileName(name);
            config = options.ConfigurationDirectory ?? DefaultTarget(defaultVmPath, suffix);
            disk = options.DiskDirectory ?? (string.IsNullOrWhiteSpace(defaultDiskPath)
                ? config : DefaultTarget(defaultDiskPath, suffix));
            foreach (string target in new[] { config, disk }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // 失败清理不得触及来源文件；目录相同或互为祖先都不是复制目标。
                if (IsSameOrWithin(target, session.SourceRoot) || IsSameOrWithin(session.SourceRoot, target))
                    throw new ArgumentException("复制导入的目标目录不能与来源目录相同或互相包含。");
                if (session.SourceKind == VmImportSourceKind.Zip && IsWithin(session.SourcePath, target))
                    throw new ArgumentException("目标目录不能包含源 ZIP 文件。");
            }
        }
        return new(name, config, disk, options.GuidMode == VmImportGuidMode.GenerateNew, options.SwitchMappings);
    }

    private static string DefaultTarget(string root, string suffix)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("宿主未提供有效默认目录，请明确指定目标目录。");
        return Path.GetFullPath(Path.Combine(root, suffix));
    }

    private static bool IsSameOrWithin(string path, string root) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) || IsWithin(path, root);

    internal static void ValidateImportGuid(Guid original, VmImportGuidMode mode, IReadOnlySet<Guid> used)
    {
        if (mode == VmImportGuidMode.GenerateNew) return;
        if (original == Guid.Empty) throw new InvalidDataException("没有取得源 GUID，不能选择保留原身份。");
        if (used.Contains(original))
            throw new InvalidOperationException($"宿主已存在 GUID 为 {original:D} 的虚拟机或导入计划；保留身份的导入未执行。");
    }

    private static async Task ConfigureImportOptionsAsync(VmImportSession session, VmImportOptions options,
        CancellationToken cancellationToken)
    {
        options = SnapshotOptions(session.PlacementMode, options);
        cancellationToken.ThrowIfCancellationRequested();
        string vmPath = string.Empty, diskPath = string.Empty;
        if (session.PlacementMode == VmImportPlacementMode.HostDirectories &&
            (options.ConfigurationDirectory == null || options.DiskDirectory == null))
        {
            var defaults = await VmCreateService.GetHostDefaultPathsAsync();
            if (!defaults.HasData) throw new InvalidOperationException(defaults.Error);
            (vmPath, diskPath) = (defaults.Data.DefaultVmPath, defaults.Data.DefaultVhdPath);
        }
        IReadOnlySet<Guid> used = options.GuidMode == VmImportGuidMode.Preserve
            ? await GetUsedVmGuidsAsync() : new HashSet<Guid>();
        var switches = await GetAvailableSwitchesAsync(cancellationToken);
        var plan = BuildImportPlan(session, options, vmPath, diskPath, used, switches.Keys.ToHashSet());
        if (session.PlacementMode == VmImportPlacementMode.HostDirectories)
            foreach (string path in new[] { plan.ConfigurationRoot, plan.DiskRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
                EnsureTargetAbsent(path);
        cancellationToken.ThrowIfCancellationRequested();
        session.TargetName = plan.Name;
        session.TargetConfigurationRoot = plan.ConfigurationRoot;
        session.TargetDiskRoot = plan.DiskRoot;
        session.GenerateNewGuid = plan.GenerateNewGuid;
        session.SwitchMappings = plan.SwitchMappings;
    }

    // null = 保持配置；空串 = 断开；其他值 = 经 GUID 解析的当前宿主交换机路径。
    internal static string? ResolveImportNetworkTarget(Guid source, IReadOnlyDictionary<Guid, Guid?> mappings,
        IReadOnlyDictionary<Guid, string> available)
    {
        if (mappings.TryGetValue(source, out Guid? target))
        {
            if (target == null) return string.Empty;
            if (!available.TryGetValue(target.Value, out string? path))
                throw new InvalidOperationException($"目标交换机 {target:D} 已不存在，未改接其他交换机。");
            return path;
        }
        return source == Guid.Empty || available.ContainsKey(source) ? null : string.Empty;
    }

    internal static (string Configuration, string Disk) GetImportStagingRoots(string config, string disk, string token)
    {
        if (IsSameOrWithin(disk, config))
        {
            string staging = config + ".importing-" + token;
            return (staging, Path.GetFullPath(Path.Combine(staging, Path.GetRelativePath(config, disk))));
        }
        if (IsWithin(config, disk))
        {
            string staging = disk + ".importing-" + token;
            return (Path.GetFullPath(Path.Combine(staging, Path.GetRelativePath(disk, config))), staging);
        }
        return (config + ".importing-" + token, disk + ".importing-" + token);
    }
}
