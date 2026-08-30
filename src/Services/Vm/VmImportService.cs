using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Vmcx;

namespace ExHyperV.Services;

public sealed class VmImportSession : IAsyncDisposable
{
    internal VmImportSession() { }

    public required string SourcePath { get; init; }
    public required VmImportSourceKind SourceKind { get; init; }
    public required VmImportPlacementMode PlacementMode { get; init; }
    public string SourceRoot { get; internal set; } = string.Empty;
    public string MainConfigurationPath { get; internal set; } = string.Empty;
    public string SnapshotFolder { get; internal set; } = string.Empty;
    public string PlannedSystemPath { get; internal set; } = string.Empty;
    internal Guid PlannedGuid { get; set; }
    public VmImportPreview Preview { get; internal set; } = new();
    public string? TemporaryRoot { get; internal set; }
    public string? ZipRootPrefix { get; internal set; }
    internal string? MainConfigurationEntry { get; set; }
    internal IReadOnlyDictionary<string, long>? ArchiveEntries { get; set; }
    internal IReadOnlyDictionary<string, string>? ExtractedArchivePaths { get; set; }
    internal IReadOnlySet<string> AvailableSwitchIds { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal string TargetConfigurationRoot { get; set; } = string.Empty;
    internal string TargetDiskRoot { get; set; } = string.Empty;
    internal string? ConfigurationStagingRoot { get; set; }
    internal string? DiskStagingRoot { get; set; }
    public bool ArchiveFullyExtracted { get; internal set; }
    public bool IsRealized { get; internal set; }
    internal bool OwnsTemporaryRoot { get; set; }
    internal bool GenerateNewGuid { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (!IsRealized && !string.IsNullOrWhiteSpace(PlannedSystemPath))
            await VmImportService.DestroyPlannedSystemAsync(PlannedSystemPath);

        if (OwnsTemporaryRoot)
            VmImportService.TryDeleteTemporaryRoot(TemporaryRoot);
        VmImportService.TryDeleteImportStaging(this);
        PlannedSystemPath = string.Empty;
    }
}

public sealed class VmImportBatchSession : IAsyncDisposable
{
    internal VmImportBatchSession() { }

    public required string SourcePath { get; init; }
    public required VmImportSourceKind SourceKind { get; init; }
    public required VmImportPlacementMode PlacementMode { get; init; }
    public IReadOnlyList<VmImportSession> VirtualMachines { get; internal set; } = [];
    internal string? TemporaryRoot { get; set; }

    public async ValueTask DisposeAsync()
    {
        foreach (VmImportSession session in VirtualMachines)
            await session.DisposeAsync();

        VmImportService.TryDeleteTemporaryRoot(TemporaryRoot);
        TemporaryRoot = null;
    }
}

public static class VmImportService
{
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private static readonly string[] DiskExtensions = [".vhd", ".vhdx", ".avhd", ".avhdx", ".vhds"];
    private static readonly Regex VmcxDiskPathRegex = new(
        @"^/configuration/_[^/]+_/controller(?<controller>\d+)/drive(?<drive>\d+)/pathname$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record SourceLayout(
        string SourceRoot,
        string MainConfigurationPath,
        string SnapshotFolder,
        string? TemporaryRoot,
        string? ZipRootPrefix,
        string? MainConfigurationEntry,
        IReadOnlyDictionary<string, long>? ArchiveEntries);

    private sealed record DiskAllocation(string Path, string InstanceId, string HostResource, string Parent);
    private sealed record VhdMetadata(string Format, string Type, ulong VirtualSize, string? ParentPath);
    private sealed record VmcxDiskReference(string ConfiguredPath, string Controller);
    private sealed record VmcxPreviewData(
        string Name,
        Guid Guid,
        int Generation,
        string ConfigurationVersion,
        DateTime Created,
        string Notes,
        int ProcessorCount,
        ulong StartupMemoryMb,
        IReadOnlyList<VmcxDiskReference> Disks);

    private sealed class ZipCrc32
    {
        private static readonly uint[] Table = CreateTable();
        private uint _value = uint.MaxValue;

        public uint Value => ~_value;

        public void Append(ReadOnlySpan<byte> data)
        {
            uint value = _value;
            foreach (byte item in data)
                value = Table[(int)((value ^ item) & 0xff)] ^ (value >> 8);
            _value = value;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (int i = 0; i < table.Length; i++)
            {
                uint value = (uint)i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0
                        ? 0xedb88320u ^ (value >> 1)
                        : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }

    public static async Task<ApiResponse<VmImportBatchSession>> PreparePreviewsAsync(
        string sourcePath,
        VmImportPlacementMode placementMode,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        VmImportBatchSession? batch = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return ApiResponse<VmImportBatchSession>.Fail("请选择导入来源。");

            VmImportSourceKind kind = File.Exists(sourcePath)
                && string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase)
                    ? VmImportSourceKind.Zip
                    : VmImportSourceKind.Folder;

            if (kind == VmImportSourceKind.Zip && placementMode == VmImportPlacementMode.ExistingDirectory)
                return ApiResponse<VmImportBatchSession>.Fail("ZIP 来源只能导入主机目录。");

            IReadOnlyList<SourceLayout> layouts;
            string? temporaryRoot = null;
            if (kind == VmImportSourceKind.Zip)
            {
                (layouts, temporaryRoot) = await PrepareZipLayoutsAsync(sourcePath, cancellationToken);
            }
            else
            {
                layouts = PrepareFolderLayouts(sourcePath);
            }

            batch = new VmImportBatchSession
            {
                SourcePath = sourcePath,
                SourceKind = kind,
                PlacementMode = placementMode,
                TemporaryRoot = temporaryRoot
            };

            HashSet<Guid> usedGuids = await GetUsedVmGuidsAsync();
            (string DefaultVmPath, string DefaultVhdPath)? hostPaths = placementMode == VmImportPlacementMode.HostDirectories
                ? await VmCreateService.GetHostDefaultPathsAsync()
                : null;
            var sessions = new List<VmImportSession>(layouts.Count);
            for (int index = 0; index < layouts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((index + 1, layouts.Count));
                VmImportSession session = await PrepareSessionAsync(
                    sourcePath,
                    kind,
                    placementMode,
                    layouts[index],
                    usedGuids,
                    hostPaths,
                    cancellationToken);
                sessions.Add(session);
                batch.VirtualMachines = sessions;
            }

            AddBatchTargetConflicts(sessions, placementMode);
            return ApiResponse<VmImportBatchSession>.Ok(batch);
        }
        catch (OperationCanceledException ex)
        {
            if (batch != null) await batch.DisposeAsync();
            return ApiResponse<VmImportBatchSession>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
        }
        catch (Exception ex)
        {
            if (batch != null) await batch.DisposeAsync();
            return ApiResponse<VmImportBatchSession>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
        }
    }

    private static async Task<VmImportSession> PrepareSessionAsync(
        string sourcePath,
        VmImportSourceKind kind,
        VmImportPlacementMode placementMode,
        SourceLayout layout,
        HashSet<Guid> usedGuids,
        (string DefaultVmPath, string DefaultVhdPath)? hostPaths,
        CancellationToken cancellationToken)
    {
        VmcxPreviewData data = await ReadVmcxPreviewAsync(layout.MainConfigurationPath, cancellationToken);
        Guid originalGuid = data.Guid != Guid.Empty
            ? data.Guid
            : TryReadGuidFromConfigurationName(layout.MainConfigurationPath);
        bool guidConflict = originalGuid != Guid.Empty && usedGuids.Contains(originalGuid);
        if (guidConflict && placementMode == VmImportPlacementMode.ExistingDirectory)
        {
            throw new InvalidOperationException(
                $"主机上已存在 GUID 为 {originalGuid:D} 的虚拟机。使用现有目录时不能更改虚拟机 GUID。");
        }

        bool generateNewGuid = guidConflict && placementMode == VmImportPlacementMode.HostDirectories;
        var preview = new VmImportPreview
        {
            Name = data.Name,
            OriginalGuid = originalGuid,
            // 预览展示来源配置中的 GUID。若发生冲突，真正的新 GUID 由 Hyper-V
            // 在用户点击“导入”后生成，不能在预览阶段伪造一个可能不一致的值。
            PlannedGuid = originalGuid,
            Generation = data.Generation,
            ConfigurationVersion = data.ConfigurationVersion,
            Created = data.Created,
            Notes = data.Notes,
            OsType = OsImages.Canonical(NotesTag.Get(data.Notes, "OSType")),
            ProcessorCount = data.ProcessorCount,
            StartupMemoryMb = data.StartupMemoryMb
        };
        var session = new VmImportSession
        {
            SourcePath = sourcePath,
            SourceKind = kind,
            PlacementMode = placementMode,
            SourceRoot = layout.SourceRoot,
            MainConfigurationPath = layout.MainConfigurationPath,
            SnapshotFolder = layout.SnapshotFolder,
            TemporaryRoot = layout.TemporaryRoot,
            OwnsTemporaryRoot = false,
            ZipRootPrefix = layout.ZipRootPrefix,
            MainConfigurationEntry = layout.MainConfigurationEntry,
            ArchiveEntries = layout.ArchiveEntries,
            GenerateNewGuid = generateNewGuid,
            Preview = preview
        };

        if (originalGuid != Guid.Empty)
            usedGuids.Add(originalGuid);

        if (hostPaths.HasValue)
        {
            string safeName = SanitizeFileName(preview.Name);
            session.TargetConfigurationRoot = Path.GetFullPath(
                Path.Combine(hostPaths.Value.DefaultVmPath, safeName));
            session.TargetDiskRoot = string.IsNullOrWhiteSpace(hostPaths.Value.DefaultVhdPath)
                ? session.TargetConfigurationRoot
                : Path.GetFullPath(Path.Combine(hostPaths.Value.DefaultVhdPath, safeName));
            foreach (string target in new[]
                     {
                         session.TargetConfigurationRoot,
                         session.TargetDiskRoot
                     }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TargetHasConflict(target))
                    preview.CompatibilityIssues.Add($"目标已存在：{target}");
            }
        }

        await PopulateDiskPreviewsAsync(session, data.Disks, cancellationToken);

        return session;
    }

    public static async Task<ApiResponse<Guid>> ImportAsync(
        VmImportSession session,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var createdPaths = new List<string>();
        bool realized = false;
        try
        {
            progress?.Report(2);
            if (session.SourceKind == VmImportSourceKind.Zip && !session.ArchiveFullyExtracted)
            {
                await ExtractArchiveToTargetStagingAsync(session, progress, cancellationToken);
                CommitZipStaging(session, createdPaths);
                session.ArchiveFullyExtracted = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await CreatePlannedSystemAsync(session, cancellationToken);
            await ImportSnapshotDefinitionsAsync(session, cancellationToken);
            await DisconnectUnavailableNetworksAsync(session, cancellationToken);

            if (session.PlacementMode == VmImportPlacementMode.HostDirectories)
            {
                string configRoot = session.TargetConfigurationRoot;
                string diskRoot = session.TargetDiskRoot;
                if (string.IsNullOrWhiteSpace(configRoot) || string.IsNullOrWhiteSpace(diskRoot))
                    throw new InvalidOperationException("虚拟机目标目录尚未准备完成。");

                if (session.SourceKind != VmImportSourceKind.Zip)
                {
                    foreach (string root in new[] { configRoot, diskRoot }
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                        EnsureTargetAbsent(root);
                    foreach (string root in new[] { configRoot, diskRoot }
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(root);
                        createdPaths.Add(root);
                    }
                }
                else if (!Directory.Exists(configRoot) || !Directory.Exists(diskRoot))
                    throw new DirectoryNotFoundException("ZIP 导入暂存目录未能提交到目标位置。");

                await MovePlannedDataRootsAsync(session.PlannedSystemPath, configRoot, cancellationToken);
                await CopyAndRelinkDisksAsync(session, diskRoot, createdPaths, progress, cancellationToken);
            }
            else
            {
                // ImportSystemDefinition 只创建计划虚拟机；不修改数据根并不等于“原地注册”。
                // 导出配置可能仍携带源主机的默认根，RealizePlannedSystem 会据此把配置
                // 写进当前主机默认目录。显式指向本次选择的单 VM 根目录，才能保证
                // 配置、检查点与分页文件继续留在来源目录中。
                await MovePlannedDataRootsAsync(
                    session.PlannedSystemPath,
                    session.SourceRoot,
                    cancellationToken);
                // 导出配置会保留源主机上的绝对 VHD 路径。即使那个旧路径当前仍存在，
                // “使用现有目录”也必须把计划虚拟机显式改指向用户选择目录内的磁盘，
                // 否则注册成功后会悄悄继续使用源盘，目录本身并不自包含。
                await RelinkPlannedDisksToSourceAsync(session, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(82);
            var validate = await ValidateAsync(session.PlannedSystemPath, cancellationToken);
            if (!validate.Success)
                throw new InvalidOperationException(validate.Error);

            progress?.Report(88);
            var realize = await WmiApi.InvokeWithResultAsync(
                ServiceWql,
                "RealizePlannedSystem",
                p => p["PlannedSystem"] = session.PlannedSystemPath,
                resultField: "ResultingSystem",
                cancellationToken: CancellationToken.None);
            if (!realize.Success)
                throw new InvalidOperationException(realize.Error);

            session.IsRealized = true;
            realized = true;
            session.PlannedSystemPath = string.Empty;
            progress?.Report(100);
            if (session.OwnsTemporaryRoot)
                TryDeleteTemporaryRoot(session.TemporaryRoot);

            Guid realizedGuid = session.PlannedGuid;
            string? resultingPath = realize.Data?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(resultingPath))
            {
                try
                {
                    using var realizedSystem = new ManagementObject(resultingPath);
                    realizedSystem.Get();
                    if (Guid.TryParse(realizedSystem["Name"]?.ToString(), out Guid returnedGuid))
                        realizedGuid = returnedGuid;
                }
                catch { }
            }

            return ApiResponse<Guid>.Ok(realizedGuid);
        }
        catch (OperationCanceledException ex)
        {
            if (!realized) await CleanupFailedImportAsync(session, createdPaths);
            return ApiResponse<Guid>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
        }
        catch (Exception ex)
        {
            if (!realized) await CleanupFailedImportAsync(session, createdPaths);
            return ApiResponse<Guid>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
        }
    }

    public static async Task<ApiResponse> PrepareBatchImportAsync(
        VmImportBatchSession batch,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HashSet<string> availableSwitchIds = await GetAvailableSwitchIdsAsync(cancellationToken);
            foreach (VmImportSession session in batch.VirtualMachines)
                session.AvailableSwitchIds = availableSwitchIds;
            return ApiResponse.Ok();
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex);
        }
    }

    private static IReadOnlyList<SourceLayout> PrepareFolderLayouts(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException("所选文件夹不存在。");

        string fullRoot = Path.GetFullPath(sourcePath);
        string[] candidates = Directory.GetFiles(fullRoot, "*.vmcx", SearchOption.AllDirectories)
            .Where(IsMainConfigurationPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("所选文件夹中没有找到可导入的虚拟机配置文件。");

        return candidates
            .Select(main =>
            {
                string root = Directory.GetParent(Path.GetDirectoryName(main)!)?.FullName ?? fullRoot;
                return new SourceLayout(root, main, root, null, null, null, null);
            })
            .ToArray();
    }

    private static async Task<(IReadOnlyList<SourceLayout> Layouts, string TemporaryRoot)> PrepareZipLayoutsAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        string temp = Path.Combine(Path.GetTempPath(), "ExHyperV", "Import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = ValidateArchiveEntries(archive);
            IReadOnlyDictionary<string, long> entryIndex = entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .ToDictionary(
                    entry => NormalizeEntryName(entry.FullName),
                    entry => entry.Length,
                    StringComparer.OrdinalIgnoreCase);
            var candidates = entries.Where(e =>
                    e.FullName.EndsWith(".vmcx", StringComparison.OrdinalIgnoreCase)
                    && IsMainConfigurationEntry(e.FullName))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidDataException("ZIP 中没有找到可导入的虚拟机配置文件。");

            // 预览只读取 Virtual Machines 目录下的 .vmcx；Snapshots 中的配置属于检查点，
            // 不应被当成另一台待导入虚拟机。VMRS/VMGS 是 Hyper-V 实体化时使用的状态文件，
            // 与卡片字段无关；尤其保存态 VMRS 可能很大，不应为预览提前解压。
            foreach (var entry in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExtractEntryAsync(entry, temp, cancellationToken);
            }

            var layouts = new List<SourceLayout>(candidates.Length);
            foreach (ZipArchiveEntry candidate in candidates)
            {
                string mainEntry = NormalizeEntryName(candidate.FullName);
                string marker = "/Virtual Machines/";
                int markerIndex = ("/" + mainEntry).LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                string rootPrefix = markerIndex <= 0 ? string.Empty : mainEntry[..markerIndex];
                string mainLocal = SafeDestination(temp, mainEntry);
                string sourceRoot = string.IsNullOrEmpty(rootPrefix) ? temp : SafeDestination(temp, rootPrefix);
                layouts.Add(new SourceLayout(
                    sourceRoot,
                    mainLocal,
                    sourceRoot,
                    temp,
                    rootPrefix,
                    mainEntry,
                    entryIndex));
            }

            return (layouts, temp);
        }
        catch
        {
            TryDeleteTemporaryRoot(temp);
            throw;
        }
    }

    private static void AddBatchTargetConflicts(
        IReadOnlyList<VmImportSession> sessions,
        VmImportPlacementMode placementMode)
    {
        if (placementMode != VmImportPlacementMode.HostDirectories || sessions.Count < 2)
            return;

        foreach (IGrouping<string, VmImportSession> duplicate in sessions
                     .GroupBy(session => SanitizeFileName(session.Preview.Name), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            string issue = $"多台虚拟机会写入同一主机目录：{duplicate.Key}";
            foreach (VmImportSession session in duplicate)
                session.Preview.CompatibilityIssues.Add(issue);
        }
    }

    private static Task<VmcxPreviewData> ReadVmcxPreviewAsync(
        string configurationPath,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var store = VmcxStore.Open(configurationPath);
            Dictionary<string, VmcxNode> values = store.Enumerate()
                .Where(node => node.IsValue)
                .GroupBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            string ReadString(string path)
                => values.TryGetValue(path, out VmcxNode node) ? node.Value ?? string.Empty : string.Empty;
            long ReadInteger(string path, long fallback = 0)
                => values.TryGetValue(path, out VmcxNode node)
                   && long.TryParse(node.Value, out long value)
                    ? value
                    : fallback;

            string name = ReadString("/configuration/properties/name");
            if (string.IsNullOrWhiteSpace(name))
            {
                DirectoryInfo? virtualMachines = Directory.GetParent(Path.GetDirectoryName(configurationPath)!);
                name = virtualMachines?.Parent?.Name
                       ?? Path.GetFileNameWithoutExtension(configurationPath);
            }

            Guid guid = Guid.TryParse(ReadString("/configuration/properties/global_id"), out Guid parsedGuid)
                ? parsedGuid
                : TryReadGuidFromConfigurationName(configurationPath);

            long subtype = ReadInteger("/configuration/properties/subtype", -1);
            bool hasGen2Firmware = values.ContainsKey(
                "/configuration/_ac6b8dc1-3257-4a70-b1b2-a9c9215659ad_/secure_boot_enabled");
            int generation = subtype switch
            {
                0 => 1,
                1 => 2,
                _ => hasGen2Firmware ? 2 : 1
            };

            long packedVersion = ReadInteger("/configuration/properties/version");
            string configurationVersion = packedVersion > 0
                ? $"{(packedVersion >> 8) & 0xffff}.{packedVersion & 0xff}"
                : string.Empty;

            DateTime created = DateTime.MinValue;
            string creationText = ReadString("/configuration/properties/creation_time");
            if (!string.IsNullOrWhiteSpace(creationText))
            {
                try
                {
                    byte[] raw = Convert.FromBase64String(creationText);
                    if (raw.Length >= sizeof(long))
                    {
                        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(raw);
                        if (fileTime > 0) created = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
                    }
                }
                catch (ArgumentOutOfRangeException) { }
                catch (FormatException) { }
            }

            VmcxDiskReference[] disks = values.Values
                .Select(node => (Node: node, Match: VmcxDiskPathRegex.Match(node.Path)))
                .Where(item => item.Match.Success
                               && !string.IsNullOrWhiteSpace(item.Node.Value)
                               && DiskExtensions.Contains(
                                   Path.GetExtension(item.Node.Value),
                                   StringComparer.OrdinalIgnoreCase))
                .OrderBy(item => int.Parse(item.Match.Groups["controller"].Value))
                .ThenBy(item => int.Parse(item.Match.Groups["drive"].Value))
                .Select(item => new VmcxDiskReference(
                    item.Node.Value,
                    $"{item.Match.Groups["controller"].Value}:{item.Match.Groups["drive"].Value}"))
                .ToArray();

            return new VmcxPreviewData(
                name,
                guid,
                generation,
                configurationVersion,
                created,
                ReadString("/configuration/properties/notes"),
                checked((int)Math.Max(0, ReadInteger("/configuration/settings/processors/count"))),
                checked((ulong)Math.Max(0, ReadInteger("/configuration/settings/memory/bank/size"))),
                disks);
        }, cancellationToken);
    }

    private static async Task PopulateDiskPreviewsAsync(
        VmImportSession session,
        IReadOnlyList<VmcxDiskReference> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0) return;

        if (session.SourceKind == VmImportSourceKind.Zip)
        {
            using ZipArchive archive = ZipFile.OpenRead(session.SourcePath);
            Dictionary<string, ZipArchiveEntry> entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .ToDictionary(
                    entry => NormalizeEntryName(entry.FullName),
                    entry => entry,
                    StringComparer.OrdinalIgnoreCase);

            foreach (VmcxDiskReference reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry? entry = null;
                bool found = TryFindArchiveDiskEntry(
                    session,
                    reference.ConfiguredPath,
                    null,
                    out string? entryName)
                    && entryName != null
                    && entries.TryGetValue(entryName, out entry);

                if (!found || entry == null)
                {
                    session.Preview.Disks.Add(CreateMissingDiskPreview(reference));
                    session.Preview.CompatibilityIssues.Add(
                        $"找不到虚拟硬盘：{Path.GetFileName(reference.ConfiguredPath)}");
                    continue;
                }

                VhdMetadata metadata = await ReadArchiveDiskMetadataAsync(
                    entry,
                    cancellationToken);
                session.Preview.Disks.Add(new VmImportDiskPreview
                {
                    Name = Path.GetFileName(entry.FullName),
                    SourcePath = entry.FullName,
                    Controller = reference.Controller,
                    Format = metadata.Format,
                    Type = metadata.Type,
                    VirtualSize = metadata.VirtualSize == 0
                        ? checked((ulong)entry.Length)
                        : metadata.VirtualSize,
                    ActualSize = entry.Length,
                    ParentPath = metadata.ParentPath,
                    Exists = true
                });
            }

            return;
        }

        foreach (VmcxDiskReference reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = ResolveSourceDiskPath(session, reference.ConfiguredPath);
            if (!File.Exists(sourcePath))
            {
                session.Preview.Disks.Add(CreateMissingDiskPreview(reference));
                session.Preview.CompatibilityIssues.Add(
                    $"找不到虚拟硬盘：{Path.GetFileName(reference.ConfiguredPath)}");
                continue;
            }

            VhdMetadata metadata = await ReadLocalVhdMetadataAsync(sourcePath, cancellationToken);
            long actualSize = new FileInfo(sourcePath).Length;
            session.Preview.Disks.Add(new VmImportDiskPreview
            {
                Name = Path.GetFileName(sourcePath),
                SourcePath = sourcePath,
                Controller = reference.Controller,
                Format = metadata.Format,
                Type = metadata.Type,
                VirtualSize = metadata.VirtualSize == 0
                    ? checked((ulong)actualSize)
                    : metadata.VirtualSize,
                ActualSize = actualSize,
                ParentPath = metadata.ParentPath,
                Exists = true
            });
        }
    }

    private static async Task<VhdMetadata> ReadArchiveDiskMetadataAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(entry.FullName);
        // VHD 的关键页脚位于文件末尾；压缩条目若不可寻址，为了预览它而顺序读取整块
        // 大磁盘得不偿失。此时以条目大小兜底，VHDX/AVHDX 的元数据位于文件头部，可快速读取。
        if (extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avhd", StringComparison.OrdinalIgnoreCase))
        {
            return new VhdMetadata(
                extension.TrimStart('.').ToUpperInvariant(),
                string.Empty,
                checked((ulong)entry.Length),
                null);
        }

        return await ReadVhdMetadataFromFactoryAsync(
            () => entry.Open(),
            entry.Length,
            extension,
            cancellationToken);
    }

    private static VmImportDiskPreview CreateMissingDiskPreview(VmcxDiskReference reference) => new()
    {
        Name = Path.GetFileName(reference.ConfiguredPath),
        SourcePath = reference.ConfiguredPath,
        Controller = reference.Controller,
        Format = Path.GetExtension(reference.ConfiguredPath).TrimStart('.').ToUpperInvariant(),
        Exists = false
    };

    private static async Task CreatePlannedSystemAsync(
        VmImportSession session,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(session.PlannedSystemPath)) return;

        Guid originalGuid = session.Preview.OriginalGuid;
        if (originalGuid != Guid.Empty)
        {
            bool conflict = (await GetUsedVmGuidsAsync()).Contains(originalGuid);
            if (conflict && session.PlacementMode == VmImportPlacementMode.ExistingDirectory)
            {
                throw new InvalidOperationException(
                    $"主机上已存在 GUID 为 {originalGuid:D} 的虚拟机。使用现有目录时不能更改虚拟机 GUID。");
            }
            if (conflict) session.GenerateNewGuid = true;
        }

        var importResult = await WmiApi.InvokeWithResultAsync(
            ServiceWql,
            "ImportSystemDefinition",
            p =>
            {
                p["SystemDefinitionFile"] = session.MainConfigurationPath;
                p["SnapshotFolder"] = session.SnapshotFolder;
                p["GenerateNewSystemIdentifier"] = session.GenerateNewGuid;
            },
            resultField: "ImportedSystem",
            cancellationToken: cancellationToken);

        string? plannedPath = importResult.Data?.FirstOrDefault(path =>
            path.Contains("Msvm_PlannedComputerSystem", StringComparison.OrdinalIgnoreCase));
        if (!importResult.Success || string.IsNullOrWhiteSpace(plannedPath))
        {
            throw new InvalidOperationException(importResult.Error.Length > 0
                ? importResult.Error
                : "Hyper-V 没有返回计划虚拟机。");
        }

        session.PlannedSystemPath = plannedPath;
        using var planned = new ManagementObject(plannedPath);
        planned.Get();
        if (!Guid.TryParse(planned["Name"]?.ToString(), out Guid plannedGuid))
            throw new InvalidDataException("计划虚拟机没有返回有效 GUID。");
        session.PlannedGuid = plannedGuid;
    }

    private static async Task ImportSnapshotDefinitionsAsync(
        VmImportSession session,
        CancellationToken cancellationToken)
    {
        string[] snapshotFolders = Directory
            .EnumerateFiles(session.SourceRoot, "*.vmcx", SearchOption.AllDirectories)
            .Where(path => !IsMainConfigurationPath(path))
            .Select(path => Path.GetDirectoryName(path)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string snapshotFolder in snapshotFolders)
        {
            var result = await WmiApi.InvokeAsync(
                ServiceWql,
                "ImportSnapshotDefinitions",
                p =>
                {
                    p["PlannedSystem"] = session.PlannedSystemPath;
                    p["SnapshotFolder"] = snapshotFolder;
                },
                cancellationToken: cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException(result.Error);
        }
    }

    private static async Task DisconnectUnavailableNetworksAsync(
        VmImportSession session,
        CancellationToken cancellationToken)
    {
        string prefix = WmiApi.Escape(session.PlannedGuid.ToString("D"));
        var allocations = await WmiApi.QueryAsync(
            $"SELECT * FROM Msvm_EthernetPortAllocationSettingData WHERE InstanceID LIKE 'Microsoft:{prefix}%'",
            obj => new
            {
                Path = obj.Path.Path,
                Host = (obj["HostResource"] as string[])?.FirstOrDefault() ?? string.Empty
            });
        if (!allocations.Success)
            throw new InvalidOperationException(allocations.Error);

        using var service = WmiApi.GetVirtualSystemManagementService();
        foreach (var item in allocations.Data ?? [])
        {
            string switchGuid = ExtractNameKey(item.Host);
            if (string.IsNullOrWhiteSpace(switchGuid)
                || session.AvailableSwitchIds.Contains(switchGuid))
                continue;

            using var allocation = new ManagementObject(item.Path);
            allocation.Get();
            allocation["EnabledState"] = (ushort)3;
            allocation["HostResource"] = Array.Empty<string>();
            var modified = await WmiApi.InvokeOnObjectAsync(
                service,
                "ModifyResourceSettings",
                p => p["ResourceSettings"] = new[] { allocation.GetText(TextFormat.CimDtd20) },
                cancellationToken: cancellationToken);
            if (!modified.Success)
            {
                throw new InvalidOperationException(modified.Error);
            }
        }
    }

    private static async Task MovePlannedDataRootsAsync(
        string plannedPath,
        string configRoot,
        CancellationToken cancellationToken)
    {
        using var planned = new ManagementObject(plannedPath);
        planned.Get();
        using var settingsCollection = planned.GetRelated("Msvm_VirtualSystemSettingData");
        using var settings = settingsCollection.Cast<ManagementObject>().FirstOrDefault(item =>
                string.Equals(item["VirtualSystemType"]?.ToString(),
                    "Microsoft:Hyper-V:System:Planned", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("计划虚拟机缺少主配置数据。");
        // Hyper-V accepts the same writable data roots here as it does during DefineSystem.
        // Suspend/guest/log roots are derived by the platform and serializing them back causes
        // ModifySystemSettings to reject the otherwise valid planned system with WBEM_E_INVALID_PARAMETER.
        foreach (string property in new[]
                 {
                     "ConfigurationDataRoot", "SnapshotDataRoot", "SwapFileDataRoot"
                 })
        {
            if (settings.Properties[property] != null)
                settings[property] = configRoot;
        }

        using var service = WmiApi.GetVirtualSystemManagementService();
        var result = await WmiApi.InvokeOnObjectAsync(
            service,
            "ModifySystemSettings",
            p => p["SystemSettings"] = settings.GetText(TextFormat.CimDtd20),
            cancellationToken: cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }

    private static async Task CopyAndRelinkDisksAsync(
        VmImportSession session,
        string diskRoot,
        List<string> createdPaths,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var allocations = ReadAllPlannedDiskAllocations(session.PlannedSystemPath);

        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentByChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var allocation in allocations)
        {
            string resolved = ResolveSourceDiskPath(session, allocation.HostResource);
            if (!File.Exists(resolved)) throw new FileNotFoundException("找不到虚拟硬盘。", allocation.HostResource);
            await AddDiskChainAsync(session, resolved, sourcePaths, parentByChild, cancellationToken);
        }

        var destinationBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedDestinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sourcePaths)
        {
            string destination;
            if (session.SourceKind == VmImportSourceKind.Zip && IsWithin(source, diskRoot))
            {
                destination = Path.GetFullPath(source);
            }
            else
            {
                string relative = IsWithin(source, session.SourceRoot)
                    ? Path.GetRelativePath(session.SourceRoot, source)
                    : Path.GetFileName(source);
                relative = TrimVirtualHardDiskDirectory(relative);
                destination = Path.GetFullPath(Path.Combine(diskRoot, relative));
            }

            if (!IsWithin(destination, diskRoot)) throw new InvalidDataException("虚拟硬盘目标路径越界。");
            if (usedDestinations.TryGetValue(destination, out string? other)
                && !string.Equals(other, source, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"多个源磁盘会写入同一目标：{destination}");
            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                EnsureTargetAbsent(destination);
            usedDestinations[destination] = source;
            destinationBySource[source] = destination;
        }

        long total = destinationBySource
            .Where(pair => !string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase))
            .Sum(pair => new FileInfo(pair.Key).Length);
        long copied = 0;
        byte[] buffer = new byte[1024 * 1024];
        foreach (var pair in destinationBySource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(pair.Value)!);
            await using var input = new FileStream(pair.Key, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(pair.Value, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                int start = session.SourceKind == VmImportSourceKind.Zip ? 40 : 5;
                int span = 80 - start;
                progress?.Report(start + (int)Math.Min(span, total == 0 ? span : copied * span / total));
            }
            createdPaths.Add(pair.Value);
        }
        progress?.Report(80);

        foreach (var pair in parentByChild)
        {
            if (!destinationBySource.TryGetValue(pair.Key, out string? child)
                || !destinationBySource.TryGetValue(pair.Value, out string? parent))
                continue;
            await SetParentAsync(child, parent, cancellationToken);
        }

        using var service = WmiApi.GetVirtualSystemManagementService();
        foreach (var allocationInfo in allocations)
        {
            string source = ResolveSourceDiskPath(session, allocationInfo.HostResource);
            if (!destinationBySource.TryGetValue(source, out string? destination)) continue;
            using var allocation = new ManagementObject(allocationInfo.Path);
            allocation.Get();
            allocation["HostResource"] = new[] { destination };
            var modified = await WmiApi.InvokeOnObjectAsync(
                service,
                "ModifyResourceSettings",
                p => p["ResourceSettings"] = new[] { allocation.GetText(TextFormat.CimDtd20) },
                cancellationToken: cancellationToken);
            if (!modified.Success) throw new InvalidOperationException(modified.Error);
        }
    }

    private static async Task RelinkPlannedDisksToSourceAsync(
        VmImportSession session,
        CancellationToken cancellationToken)
    {
        List<DiskAllocation> allocations = ReadAllPlannedDiskAllocations(
            session.PlannedSystemPath);
        var sourceByAllocation = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (DiskAllocation allocation in allocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = ResolveSourceDiskPath(session, allocation.HostResource);
            if (!File.Exists(source))
                throw new FileNotFoundException("所选目录中找不到虚拟硬盘。", allocation.HostResource);
            sourceByAllocation[allocation.Path] = source;
        }

        using var service = WmiApi.GetVirtualSystemManagementService();
        foreach (DiskAllocation allocationInfo in allocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var allocation = new ManagementObject(allocationInfo.Path);
            allocation.Get();
            allocation["HostResource"] = new[] { sourceByAllocation[allocationInfo.Path] };
            var modified = await WmiApi.InvokeOnObjectAsync(
                service,
                "ModifyResourceSettings",
                p => p["ResourceSettings"] = new[] { allocation.GetText(TextFormat.CimDtd20) },
                cancellationToken: cancellationToken);
            if (!modified.Success) throw new InvalidOperationException(modified.Error);
        }
    }

    private static List<DiskAllocation> ReadAllPlannedDiskAllocations(string plannedSystemPath)
    {
        var result = new List<DiskAllocation>();
        using var planned = new ManagementObject(plannedSystemPath);
        planned.Get();
        using var settingsCollection = planned.GetRelated("Msvm_VirtualSystemSettingData");
        foreach (ManagementObject settings in settingsCollection)
        using (settings)
        using (var disks = settings.GetRelated("Msvm_StorageAllocationSettingData"))
        {
            foreach (ManagementObject disk in disks)
            using (disk)
            {
                string hostResource = (disk["HostResource"] as string[])?.FirstOrDefault()
                    ?? disk["HostResource"]?.ToString()
                    ?? string.Empty;
                if (!DiskExtensions.Contains(Path.GetExtension(hostResource), StringComparer.OrdinalIgnoreCase))
                    continue;
                result.Add(new DiskAllocation(
                    disk.Path.Path,
                    disk["InstanceID"]?.ToString() ?? string.Empty,
                    hostResource,
                    disk["Parent"]?.ToString() ?? string.Empty));
            }
        }
        return result
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static async Task AddDiskChainAsync(
        VmImportSession session,
        string path,
        HashSet<string> paths,
        Dictionary<string, string> parents,
        CancellationToken cancellationToken)
    {
        string current = Path.GetFullPath(path);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(current)) throw new FileNotFoundException("差分磁盘链不完整。", current);
            paths.Add(current);
            VhdMetadata metadata = await ReadLocalVhdMetadataAsync(current, cancellationToken);
            if (string.IsNullOrWhiteSpace(metadata.ParentPath)) return;
            string parent = ResolveParentDiskPath(session, current, metadata.ParentPath);
            parents[current] = parent;
            current = parent;
        }
        throw new InvalidDataException("检测到循环差分磁盘链。");
    }

    private static async Task SetParentAsync(string child, string parent, CancellationToken cancellationToken)
    {
        var result = await WmiApi.InvokeAsync(
            "SELECT * FROM Msvm_ImageManagementService",
            "SetParentVirtualHardDisk",
            p =>
            {
                p["ChildPath"] = child;
                p["ParentPath"] = parent;
                p["LeafPath"] = null;
                p["IgnoreIDMismatch"] = false;
            },
            cancellationToken: cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }

    private static Task<ApiResponse> ValidateAsync(string plannedPath, CancellationToken cancellationToken)
        => WmiApi.InvokeAsync(
            ServiceWql,
            "ValidatePlannedSystem",
            p => p["PlannedSystem"] = plannedPath,
            cancellationToken: cancellationToken);

    internal static async Task DestroyPlannedSystemAsync(string plannedPath)
    {
        try
        {
            await WmiApi.InvokeAsync(
                ServiceWql,
                "DestroySystem",
                p => p["AffectedSystem"] = plannedPath,
                cancellationToken: CancellationToken.None);
        }
        catch { }
    }

    private static async Task ExtractArchiveToTargetStagingAsync(
        VmImportSession session,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.TargetConfigurationRoot)
            || string.IsNullOrWhiteSpace(session.TargetDiskRoot)
            || string.IsNullOrWhiteSpace(session.MainConfigurationEntry))
        {
            throw new InvalidOperationException("ZIP 导入目标目录尚未准备完成。");
        }

        string token = Guid.NewGuid().ToString("N");
        string configStaging = session.TargetConfigurationRoot + ".importing-" + token;
        string diskStaging = string.Equals(
                session.TargetConfigurationRoot,
                session.TargetDiskRoot,
                StringComparison.OrdinalIgnoreCase)
            ? configStaging
            : session.TargetDiskRoot + ".importing-" + token;

        foreach (string path in new[]
                 {
                     session.TargetConfigurationRoot,
                     session.TargetDiskRoot,
                     configStaging,
                     diskStaging
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
            EnsureTargetAbsent(path);

        session.ConfigurationStagingRoot = configStaging;
        session.DiskStagingRoot = diskStaging;
        try
        {
            using var archive = ZipFile.OpenRead(session.SourcePath);
            var entries = ValidateArchiveEntries(archive)
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .Where(entry => IsArchiveEntryForSession(session, NormalizeEntryName(entry.FullName)))
                .ToList();
            if (!entries.Any(entry => string.Equals(
                    NormalizeEntryName(entry.FullName),
                    session.MainConfigurationEntry,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("ZIP 中找不到当前虚拟机的主配置文件。");
            }

            EnsureZipTargetSpace(session, entries, configStaging, diskStaging);
            Directory.CreateDirectory(configStaging);
            if (!string.Equals(configStaging, diskStaging, StringComparison.OrdinalIgnoreCase))
                Directory.CreateDirectory(diskStaging);

            long total = entries.Sum(entry => entry.Length);
            long done = 0;
            int lastReportedProgress = -1;
            byte[] buffer = new byte[1024 * 1024];
            var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryName = NormalizeEntryName(entry.FullName);
                string relative = GetSessionArchiveRelativePath(session, entryName);
                bool isDisk = IsVirtualDiskEntry(relative);
                if (isDisk)
                    relative = TrimVirtualHardDiskDirectory(relative);
                string target = SafeDestination(isDisk ? diskStaging : configStaging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await CopyArchiveEntryWithValidationAsync(
                    entry,
                    output,
                    buffer,
                    bytesCopied =>
                    {
                        done += bytesCopied;
                        int currentProgress = 2 + (int)Math.Min(
                            78,
                            total == 0 ? 78 : done * 78 / total);
                        if (currentProgress != lastReportedProgress)
                        {
                            lastReportedProgress = currentProgress;
                            progress?.Report(currentProgress);
                        }
                    },
                    cancellationToken);

                extracted[entryName] = target;
            }

            progress?.Report(80);
            session.ExtractedArchivePaths = extracted;
            session.SourceRoot = configStaging;
            session.MainConfigurationPath = extracted[session.MainConfigurationEntry];
            session.SnapshotFolder = configStaging;
        }
        catch
        {
            TryDeleteImportStaging(session);
            throw;
        }
    }

    private static void CommitZipStaging(
        VmImportSession session,
        ICollection<string> createdPaths)
    {
        string configStaging = session.ConfigurationStagingRoot
            ?? throw new InvalidOperationException("ZIP 配置暂存目录不存在。");
        string diskStaging = session.DiskStagingRoot
            ?? throw new InvalidOperationException("ZIP 磁盘暂存目录不存在。");

        foreach (string target in new[]
                 {
                     session.TargetConfigurationRoot,
                     session.TargetDiskRoot
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
            PrepareTargetForDirectoryMove(target);

        var moves = new[]
            {
                (Staging: configStaging, Target: session.TargetConfigurationRoot),
                (Staging: diskStaging, Target: session.TargetDiskRoot)
            }
            .DistinctBy(item => item.Staging, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var move in moves)
        {
            Directory.Move(move.Staging, move.Target);
            createdPaths.Add(move.Target);
        }

        var committed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in session.ExtractedArchivePaths
                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            committed[pair.Key] = MovePathRoot(
                pair.Value,
                configStaging,
                session.TargetConfigurationRoot,
                diskStaging,
                session.TargetDiskRoot);
        }

        session.ExtractedArchivePaths = committed;
        session.SourceRoot = session.TargetConfigurationRoot;
        session.MainConfigurationPath = committed[session.MainConfigurationEntry!];
        session.SnapshotFolder = session.TargetConfigurationRoot;
        session.ConfigurationStagingRoot = null;
        session.DiskStagingRoot = null;
    }

    private static void PrepareTargetForDirectoryMove(string path)
    {
        EnsureTargetAbsent(path);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: false);
    }

    private static bool IsArchiveEntryForSession(VmImportSession session, string entryName)
    {
        string prefix = NormalizeEntryName(session.ZipRootPrefix ?? string.Empty).TrimEnd('/');
        return prefix.Length == 0
               || entryName.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSessionArchiveRelativePath(
        VmImportSession session,
        string entryName)
    {
        string normalized = NormalizeEntryName(entryName);
        string prefix = NormalizeEntryName(session.ZipRootPrefix ?? string.Empty).TrimEnd('/');
        if (prefix.Length == 0)
            return normalized;

        string expectedPrefix = prefix + "/";
        if (!normalized.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"ZIP 条目不属于当前虚拟机：{entryName}");
        return normalized[expectedPrefix.Length..];
    }

    private static bool IsVirtualDiskEntry(string relativePath)
        => DiskExtensions.Contains(
            Path.GetExtension(relativePath),
            StringComparer.OrdinalIgnoreCase);

    private static void EnsureZipTargetSpace(
        VmImportSession session,
        IReadOnlyCollection<ZipArchiveEntry> entries,
        string configStaging,
        string diskStaging)
    {
        var requiredByStaging = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in entries)
        {
            string relative = GetSessionArchiveRelativePath(
                session,
                NormalizeEntryName(entry.FullName));
            string staging = IsVirtualDiskEntry(relative) ? diskStaging : configStaging;
            requiredByStaging[staging] = requiredByStaging.GetValueOrDefault(staging) + entry.Length;
        }

        foreach (var group in requiredByStaging
                     .GroupBy(
                         pair => Path.GetPathRoot(Path.GetFullPath(pair.Key)) ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
                continue;

            long required = group.Sum(pair => pair.Value);
            try
            {
                var drive = new DriveInfo(group.Key);
                if (drive.IsReady && drive.AvailableFreeSpace < required)
                {
                    throw new IOException(
                        $"目标驱动器 {drive.Name} 空间不足，需要至少 " +
                        $"{required / 1024d / 1024d / 1024d:F2} GiB，当前可用 " +
                        $"{drive.AvailableFreeSpace / 1024d / 1024d / 1024d:F2} GiB。");
                }
            }
            catch (ArgumentException)
            {
                // UNC 和部分自定义文件系统不支持 DriveInfo；实际写入仍会返回准确错误。
            }
            catch (DriveNotFoundException)
            {
                // 同上，让后续目录创建提供具体错误。
            }
        }
    }

    private static string MovePathRoot(
        string path,
        string configStaging,
        string configTarget,
        string diskStaging,
        string diskTarget)
    {
        if (IsWithin(path, diskStaging))
            return Path.Combine(diskTarget, Path.GetRelativePath(diskStaging, path));
        if (IsWithin(path, configStaging))
            return Path.Combine(configTarget, Path.GetRelativePath(configStaging, path));
        throw new InvalidDataException($"解压文件不在导入暂存目录中：{path}");
    }

    private static List<ZipArchiveEntry> ValidateArchiveEntries(ZipArchive archive)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ZipArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            string name = NormalizeEntryName(entry.FullName);
            if (Path.IsPathRooted(name) || name.Split('/').Any(part => part == ".."))
                throw new InvalidDataException($"ZIP 包含不安全路径：{entry.FullName}");
            if (!seen.Add(name)) throw new InvalidDataException($"ZIP 包含重复路径：{entry.FullName}");
            result.Add(entry);
        }
        return result;
    }

    private static async Task ExtractEntryAsync(ZipArchiveEntry entry, string root, CancellationToken cancellationToken)
    {
        string destination = SafeDestination(root, NormalizeEntryName(entry.FullName));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[81920];
        await CopyArchiveEntryWithValidationAsync(
            entry,
            target,
            buffer,
            null,
            cancellationToken);
    }

    private static async Task CopyArchiveEntryWithValidationAsync(
        ZipArchiveEntry entry,
        Stream output,
        byte[] buffer,
        Action<int>? copied,
        CancellationToken cancellationToken)
    {
        await using Stream input = entry.Open();
        var crc32 = new ZipCrc32();
        long actualLength = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (actualLength > entry.Length || read > entry.Length - actualLength)
                throw CreateZipEntryValidationException(entry);

            crc32.Append(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            actualLength += read;
            copied?.Invoke(read);
        }

        if (actualLength != entry.Length || crc32.Value != entry.Crc32)
            throw CreateZipEntryValidationException(entry);
    }

    private static InvalidDataException CreateZipEntryValidationException(ZipArchiveEntry entry)
        => new($"ZIP 条目校验失败，文件可能已损坏：{entry.FullName}");

    private static Task<VhdMetadata> ReadLocalVhdMetadataAsync(string path, CancellationToken cancellationToken)
        => ReadVhdMetadataFromFactoryAsync(
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            new FileInfo(path).Length,
            Path.GetExtension(path),
            cancellationToken);

    private static async Task<VhdMetadata> ReadVhdMetadataFromFactoryAsync(
        Func<Stream> streamFactory,
        long length,
        string extension,
        CancellationToken cancellationToken)
    {
        if (extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avhdx", StringComparison.OrdinalIgnoreCase))
            return await ReadVhdxMetadataAsync(streamFactory, length, cancellationToken);
        if (extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avhd", StringComparison.OrdinalIgnoreCase))
            return await ReadVhdMetadataAsync(streamFactory, length, cancellationToken);
        return new VhdMetadata(extension.TrimStart('.').ToUpperInvariant(), string.Empty, 0, null);
    }

    private static async Task<VhdMetadata> ReadVhdxMetadataAsync(
        Func<Stream> factory,
        long length,
        CancellationToken cancellationToken)
    {
        byte[] identifier = await ReadRangeAsync(factory, 0, 8, cancellationToken);
        if (Encoding.ASCII.GetString(identifier) != "vhdxfile")
            throw new InvalidDataException("VHDX 文件标识无效。");

        Guid metadataRegionGuid = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
        (long Offset, int Length)? metadataRegion = null;
        foreach (long regionOffset in new[] { 192L * 1024, 256L * 1024 })
        {
            byte[] table = await ReadRangeAsync(factory, regionOffset, 64 * 1024, cancellationToken);
            if (Encoding.ASCII.GetString(table, 0, 4) != "regi") continue;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8, 4));
            for (int i = 0; i < Math.Min((int)count, 2047); i++)
            {
                int pos = 16 + i * 32;
                Guid id = new(table.AsSpan(pos, 16));
                if (id != metadataRegionGuid) continue;
                metadataRegion = (
                    checked((long)BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(pos + 16, 8))),
                    checked((int)BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(pos + 24, 4))));
                break;
            }
            if (metadataRegion.HasValue) break;
        }
        if (!metadataRegion.HasValue) throw new InvalidDataException("VHDX 缺少元数据区域。");

        byte[] metadata = await ReadRangeAsync(factory, metadataRegion.Value.Offset, metadataRegion.Value.Length, cancellationToken);
        if (Encoding.ASCII.GetString(metadata, 0, 8) != "metadata")
            throw new InvalidDataException("VHDX 元数据表无效。");
        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(10, 2));
        Guid fileParametersGuid = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
        Guid sizeGuid = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
        Guid parentLocatorGuid = new("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C");
        uint flags = 0;
        ulong virtualSize = 0;
        string? parentPath = null;
        for (int i = 0; i < Math.Min((int)entryCount, 2047); i++)
        {
            int pos = 32 + i * 32;
            Guid id = new(metadata.AsSpan(pos, 16));
            int itemOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(pos + 16, 4)));
            int itemLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(pos + 20, 4)));
            if (itemOffset < 0 || itemLength < 0 || itemOffset + itemLength > metadata.Length) continue;
            if (id == fileParametersGuid && itemLength >= 8)
                flags = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(itemOffset + 4, 4));
            else if (id == sizeGuid && itemLength >= 8)
                virtualSize = BinaryPrimitives.ReadUInt64LittleEndian(metadata.AsSpan(itemOffset, 8));
            else if (id == parentLocatorGuid)
                parentPath = ParseVhdxParentLocator(metadata.AsSpan(itemOffset, itemLength));
        }

        bool hasParent = (flags & 2) != 0;
        string type = hasParent
            ? Properties.Resources.VmImport_DiskDifferencing
            : (flags & 1) != 0
                ? Properties.Resources.VmImport_DiskFixed
                : Properties.Resources.VmImport_DiskDynamic;
        return new VhdMetadata("VHDX", type, virtualSize, parentPath);
    }

    private static string? ParseVhdxParentLocator(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return null;
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(18, 2));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Min((int)count, 128); i++)
        {
            int pos = 20 + i * 12;
            if (pos + 12 > data.Length) break;
            int keyOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4)));
            int valueOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4)));
            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 8, 2));
            int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 10, 2));
            if (keyOffset + keyLength > data.Length || valueOffset + valueLength > data.Length) continue;
            string key = Encoding.Unicode.GetString(data.Slice(keyOffset, keyLength)).TrimEnd('\0');
            string value = Encoding.Unicode.GetString(data.Slice(valueOffset, valueLength)).TrimEnd('\0');
            values[key] = value;
        }
        foreach (string key in new[] { "relative_path", "absolute_win32_path", "volume_path" })
            if (values.TryGetValue(key, out string? value) && value.Length > 0) return value;
        return null;
    }

    private static async Task<VhdMetadata> ReadVhdMetadataAsync(
        Func<Stream> factory,
        long length,
        CancellationToken cancellationToken)
    {
        if (length < 512) throw new InvalidDataException("VHD 文件过小。");
        byte[] footer = await ReadRangeAsync(factory, length - 512, 512, cancellationToken);
        if (Encoding.ASCII.GetString(footer, 0, 8) != "conectix")
            throw new InvalidDataException("VHD 文件标识无效。");
        ulong virtualSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48, 8));
        uint type = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60, 4));
        string? parentPath = null;
        if (type == 4)
        {
            ulong dataOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16, 8));
            if (dataOffset <= (ulong)Math.Max(0, length - 1024))
            {
                byte[] header = await ReadRangeAsync(factory, checked((long)dataOffset), 1024, cancellationToken);
                if (Encoding.ASCII.GetString(header, 0, 8) == "cxsparse")
                {
                    string parentName = Encoding.BigEndianUnicode.GetString(header, 64, 512).TrimEnd('\0');
                    if (!string.IsNullOrWhiteSpace(parentName)) parentPath = parentName;
                }
            }
        }
        return new VhdMetadata(
            "VHD",
            type == 2 ? Properties.Resources.VmImport_DiskFixed
                : type == 3 ? Properties.Resources.VmImport_DiskDynamic
                : type == 4 ? Properties.Resources.VmImport_DiskDifferencing
                : string.Empty,
            virtualSize,
            parentPath);
    }

    private static async Task<byte[]> ReadRangeAsync(
        Func<Stream> factory,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        await using Stream stream = factory();
        if (stream.CanSeek) stream.Seek(offset, SeekOrigin.Begin);
        else
        {
            byte[] skip = new byte[1024 * 1024];
            long remaining = offset;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException();
                remaining -= read;
            }
        }
        byte[] buffer = new byte[length];
        int total = 0;
        while (total < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, length - total), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
        return buffer;
    }

    private static async Task<HashSet<Guid>> GetUsedVmGuidsAsync()
    {
        var result = new HashSet<Guid>();
        var realized = await WmiApi.QueryAsync(
            "SELECT Name FROM Msvm_ComputerSystem",
            obj => obj["Name"]?.ToString() ?? string.Empty);
        if (!realized.Success) throw new InvalidOperationException(realized.Error);
        foreach (string value in realized.Data ?? [])
            if (Guid.TryParse(value, out Guid guid)) result.Add(guid);

        var planned = await WmiApi.QueryAsync(
            "SELECT Name FROM Msvm_PlannedComputerSystem",
            obj => obj["Name"]?.ToString() ?? string.Empty);
        if (!planned.Success) throw new InvalidOperationException(planned.Error);
        foreach (string value in planned.Data ?? [])
            if (Guid.TryParse(value, out Guid guid)) result.Add(guid);
        return result;
    }

    private static async Task<HashSet<string>> GetAvailableSwitchIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var switches = await WmiApi.QueryAsync(
            "SELECT Name FROM Msvm_VirtualEthernetSwitch",
            obj => obj["Name"]?.ToString() ?? string.Empty);
        if (!switches.Success) throw new InvalidOperationException(switches.Error);
        return new HashSet<string>(
            (switches.Data ?? []).Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryFindArchiveDiskEntry(
        VmImportSession session,
        string configuredPath,
        string? childEntryName,
        out string? entryName)
    {
        entryName = null;
        if (session.SourceKind != VmImportSourceKind.Zip) return false;

        IReadOnlyCollection<string> files = session.ArchiveEntries?.Keys as IReadOnlyCollection<string>
            ?? Array.Empty<string>();

        var exactCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(session.TemporaryRoot)
            && Path.IsPathRooted(configuredPath)
            && IsWithin(configuredPath, session.TemporaryRoot))
            exactCandidates.Add(NormalizeEntryName(Path.GetRelativePath(session.TemporaryRoot, configuredPath)));
        if (!string.IsNullOrWhiteSpace(session.SourceRoot)
            && Path.IsPathRooted(configuredPath)
            && IsWithin(configuredPath, session.SourceRoot))
        {
            string relative = NormalizeEntryName(Path.GetRelativePath(session.SourceRoot, configuredPath));
            exactCandidates.Add(string.IsNullOrWhiteSpace(session.ZipRootPrefix)
                ? relative
                : NormalizeEntryName(session.ZipRootPrefix + "/" + relative));
        }
        if (!string.IsNullOrWhiteSpace(childEntryName) && !Path.IsPathRooted(configuredPath))
        {
            string? parentDirectory = Path.GetDirectoryName(
                childEntryName.Replace('/', Path.DirectorySeparatorChar));
            string combined = Path.GetFullPath(Path.Combine(
                Path.DirectorySeparatorChar.ToString(),
                parentDirectory ?? string.Empty,
                configuredPath));
            exactCandidates.Add(NormalizeEntryName(combined));
        }
        if (!Path.IsPathRooted(configuredPath))
        {
            string relative = NormalizeEntryName(configuredPath);
            exactCandidates.Add(relative);
            if (!string.IsNullOrWhiteSpace(session.ZipRootPrefix))
                exactCandidates.Add(NormalizeEntryName(session.ZipRootPrefix + "/" + relative));
        }

        string? exact = files.FirstOrDefault(path => exactCandidates.Contains(path));
        if (exact != null)
        {
            entryName = exact;
            return true;
        }

        string fileName = Path.GetFileName(configuredPath);
        IEnumerable<string> scopedFiles = files;
        if (!string.IsNullOrWhiteSpace(session.ZipRootPrefix))
        {
            string prefix = NormalizeEntryName(session.ZipRootPrefix).TrimEnd('/') + "/";
            string[] localMatches = files
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (localMatches.Length > 0)
                scopedFiles = localMatches;
        }

        var matches = scopedFiles
            .Where(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count > 1)
            throw new InvalidDataException($"ZIP 中存在多个同名虚拟硬盘，无法确定引用：{fileName}");
        if (matches.Count == 0) return false;
        entryName = matches[0];
        return true;
    }

    private static string ResolveSourceDiskPath(VmImportSession session, string configuredPath)
    {
        if (session.SourceKind == VmImportSourceKind.Zip && session.ArchiveFullyExtracted)
        {
            if (TryFindArchiveDiskEntry(session, configuredPath, null, out string? entryName)
                && entryName != null
                && session.ExtractedArchivePaths?.TryGetValue(entryName, out string? extracted) == true)
            {
                if (File.Exists(extracted)) return extracted;
            }
        }

        // 来源目录优先于配置里记录的旧绝对路径。Hyper-V 导出会复制 VHD，但 VMCX
        // 仍可能保留源路径；先接受旧路径会让导入后的 VM 继续使用源盘。
        if (Path.IsPathRooted(configuredPath)
            && IsWithin(configuredPath, session.SourceRoot)
            && File.Exists(configuredPath))
            return Path.GetFullPath(configuredPath);

        string candidate = Path.Combine(session.SourceRoot, "Virtual Hard Disks", Path.GetFileName(configuredPath));
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        string? imported = FindUniqueSourceFile(session.SourceRoot, Path.GetFileName(configuredPath));
        if (imported != null) return imported;
        imported = FindUniqueSourceFile(session.TargetDiskRoot, Path.GetFileName(configuredPath));
        if (imported != null) return imported;

        // 非导出布局可以合法引用目录外的既有磁盘；仅当所选目录内确实没有同名盘时回退。
        return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : configuredPath;
    }

    private static string ResolveParentDiskPath(
        VmImportSession session,
        string childPath,
        string parentLocator)
    {
        string relative = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(childPath)!, parentLocator));
        if (!Path.IsPathRooted(parentLocator) && File.Exists(relative)) return relative;

        string? imported = FindUniqueSourceFile(session.TargetDiskRoot, Path.GetFileName(parentLocator));
        imported ??= FindUniqueSourceFile(session.SourceRoot, Path.GetFileName(parentLocator));
        if (imported != null) return imported;
        if (Path.IsPathRooted(parentLocator)) return Path.GetFullPath(parentLocator);
        return relative;
    }

    private static string? FindUniqueSourceFile(string root, string fileName)
    {
        if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(fileName)) return null;
        string[] matches = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => Path.GetFullPath(matches[0]),
            _ => throw new InvalidDataException($"来源中存在多个同名虚拟硬盘，无法确定引用：{fileName}")
        };
    }

    private static bool IsMainConfigurationPath(string path)
        => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Virtual Machines", StringComparison.OrdinalIgnoreCase);

    private static bool IsMainConfigurationEntry(string entry)
    {
        string normalized = NormalizeEntryName(entry);
        string? directory = Path.GetDirectoryName(normalized.Replace('/', Path.DirectorySeparatorChar));
        return string.Equals(Path.GetFileName(directory), "Virtual Machines", StringComparison.OrdinalIgnoreCase);
    }

    private static Guid TryReadGuidFromConfigurationName(string path)
        => Guid.TryParse(Path.GetFileNameWithoutExtension(path), out Guid guid) ? guid : Guid.Empty;

    private static string NormalizeEntryName(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string SafeDestination(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(destination, fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP 路径超出临时目录。");
        return destination;
    }

    private static string ExtractNameKey(string path)
    {
        Match match = Regex.Match(path ?? string.Empty, "Name=\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static bool IsWithin(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimVirtualHardDiskDirectory(string relative)
    {
        string normalized = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string prefix = "Virtual Hard Disks" + Path.DirectorySeparatorChar;
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized[prefix.Length..] : normalized;
    }

    private static string SanitizeFileName(string name)
    {
        string result = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return result.Length == 0 ? "Imported VM" : result;
    }

    private static void EnsureTargetAbsent(string path)
    {
        if (TargetHasConflict(path))
            throw new IOException($"目标已存在：{path}");
    }

    private static bool TargetHasConflict(string path)
    {
        if (File.Exists(path)) return true;
        if (!Directory.Exists(path)) return false;
        try
        {
            // 失败导入可能只留下一个空目录；它不包含可覆盖的数据，可以安全复用。
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            // 无法枚举就按有内容处理，保持不覆盖原则。
            return true;
        }
    }

    private static async Task CleanupCreatedPathsAsync(IEnumerable<string> paths)
    {
        foreach (string path in paths.OrderByDescending(x => x.Length))
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    else if (Directory.Exists(path)) Directory.Delete(path, true);
                    break;
                }
                catch when (attempt < 5)
                {
                    // 销毁计划虚拟机后 VMMS 可能短暂占用配置/VHD；等待后重试，
                    // 避免遗留空目录阻塞下一次导入。
                    await Task.Delay(250);
                }
                catch
                {
                    // 清理失败不能覆盖原始导入错误；下次导入仍可复用真正的空目录。
                }
            }
        }
    }

    private static async Task CleanupFailedImportAsync(
        VmImportSession session,
        IEnumerable<string> createdPaths)
    {
        if (!string.IsNullOrWhiteSpace(session.PlannedSystemPath))
            await DestroyPlannedSystemAsync(session.PlannedSystemPath);
        session.PlannedSystemPath = string.Empty;
        await CleanupCreatedPathsAsync(createdPaths);
        TryDeleteImportStaging(session);
        if (session.OwnsTemporaryRoot)
        {
            TryDeleteTemporaryRoot(session.TemporaryRoot);
            session.TemporaryRoot = null;
        }
    }

    internal static void TryDeleteImportStaging(VmImportSession session)
    {
        foreach (string path in new[]
                 {
                     session.ConfigurationStagingRoot,
                     session.DiskStagingRoot
                 }
                 .Where(path => !string.IsNullOrWhiteSpace(path))
                 .Select(path => path!)
                 .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // Preserve the original import error; .importing-* remains identifiable.
            }
        }

        session.ConfigurationStagingRoot = null;
        session.DiskStagingRoot = null;
    }

    internal static void TryDeleteTemporaryRoot(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }
}
