using System.Buffers.Binary;
using System.IO;
using ZipArchive = ExHyperV.Tools.CoreZipArchive;
using ZipArchiveEntry = ExHyperV.Tools.CoreZipArchive.CoreZipEntry;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Vmcx;

namespace ExHyperV.Services;

/// <summary>解析和准备导入预览，再按选择实现虚拟机；会话负责暂存资源的释放。</summary>
public static partial class VmImportService
{
    // Preview issues block the existing import action; callers must prepare a new
    // preview after resolving them. Native validation still runs before realization.
    /// <summary>检查预览集合是否为空或包含阻止导入的兼容性问题。</summary>
    /// <param name="previews">待校验的单机预览集合。</param>
    /// <returns>首个阻止继续的说明；可进入下一阶段时为 null。修复源问题后需要重新生成预览。</returns>
    public static string? GetPreviewError(IEnumerable<VmImportPreview> previews)
    {
        bool any = false;
        foreach (VmImportPreview preview in previews)
        {
            any = true;
            if (preview.CompatibilityIssues.Count > 0)
            {
                string error = string.Join(Environment.NewLine, preview.CompatibilityIssues);
                return string.IsNullOrWhiteSpace(error) ? Properties.Resources.VmImport_Failed : error;
            }
        }
        return any ? null : "没有可导入的虚拟机预览。";
    }

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
        string OsType,
        int ProcessorCount,
        ulong StartupMemoryMiB,
        IReadOnlyList<VmcxDiskReference> Disks,
        IReadOnlyList<Guid> SourceSwitchIds);

    /// <summary>解析目录或 ZIP 中的虚拟机配置，生成待导入预览及拥有暂存资源的批次。</summary>
    /// <param name="sourcePath">包含导出配置的目录或 ZIP 文件路径。</param>
    /// <param name="placementMode">在原目录就地导入或复制到新位置；ZIP 不支持原目录模式。</param>
    /// <param name="progress">可选的预览扫描计数回调，Current 和 Total 表示当前处理进度，不是百分比。</param>
    /// <param name="cancellationToken">协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。</param>
    /// <returns>Success 表示预览是否准备完成；Batch 非 null 时始终由调用方管理，失败时也应检查清理结果并保留待重试对象。ZIP 不支持原目录就地导入。</returns>
    public static Task<VmImportPreparationResult> PreparePreviewsAsync(
        string sourcePath,
        VmImportPlacementMode placementMode,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
        => PreparePreviewsAsync(sourcePath, new VmImportArchiveOptions(), placementMode, progress, cancellationToken);

    /// <summary>使用指定的 ZIP 密码、解压额度和预览工作目录准备导入预览。</summary>
    /// <param name="sourcePath">包含导出配置的目录或 ZIP 文件路径。</param>
    /// <param name="archiveOptions">ZIP 读取选项，贯穿预览和后续导入；目录来源不使用这些选项。一个批次的各单机会话共享累计额度。</param>
    /// <param name="placementMode">文件放置方式；ZIP 只能复制到目标目录。</param>
    /// <param name="progress">预览处理计数；null 不通知。</param>
    /// <param name="cancellationToken">取消预览准备；已创建的工作文件仍按批次清理规则处理。</param>
    /// <returns>预览批次或失败信息；密码、加密方式和额度失败可从 Result.Exception 取得 VmImportArchiveException。失败时也应检查并释放 Batch。</returns>
    /// <remarks>未加密 ZIP 和 AES-128/AES-256 ZIP 可读取，传统 ZipCrypto 不支持。预览只完整校验配置条目；磁盘头仅作预览，整盘内容与 AES 认证在正式解压时校验。缺密码或密码错误时，释放失败批次后用正确密码重新准备。密码仅由批次在内存中持有，批次释放后 Core 解除其引用，不承诺清除调用方持有的字符串。</remarks>
    public static async Task<VmImportPreparationResult> PreparePreviewsAsync(
        string sourcePath,
        VmImportArchiveOptions archiveOptions,
        VmImportPlacementMode placementMode,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        VmImportBatchSession? batch = null;
        try
        {
            ArgumentNullException.ThrowIfNull(archiveOptions);
            if (string.IsNullOrWhiteSpace(sourcePath))
                return new(ApiResponse.Fail("请选择导入来源。"), null);
            if (!Enum.IsDefined(placementMode))
                return new(ApiResponse.Fail("导入放置方式无效。", kind: ApiErrorKind.InvalidArgument), null);

            VmImportSourceKind kind = File.Exists(sourcePath)
                && string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase)
                    ? VmImportSourceKind.Zip
                    : VmImportSourceKind.Folder;

            if (kind == VmImportSourceKind.Zip && placementMode == VmImportPlacementMode.ExistingDirectory)
                return new(ApiResponse.Fail("ZIP 来源只能导入主机目录。"), null);

            cancellationToken.ThrowIfCancellationRequested();
            batch = new VmImportBatchSession
            {
                SourcePath = sourcePath,
                SourceKind = kind,
                PlacementMode = placementMode,
                ArchiveContext = kind == VmImportSourceKind.Zip ? new ImportArchiveContext(archiveOptions) : null
            };

            IReadOnlyList<SourceLayout> layouts = kind == VmImportSourceKind.Zip
                ? await PrepareZipLayoutsAsync(sourcePath, batch, cancellationToken)
                : PrepareFolderLayouts(sourcePath);

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
                    batch.ArchiveContext,
                    cancellationToken);
                sessions.Add(session);
                batch.VirtualMachines = sessions.AsReadOnly();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new(ApiResponse.Ok(), batch);
        }
        catch (Exception ex)
        {
            if (batch != null)
            {
                try { await batch.DisposeAsync(); }
                catch (Exception cleanupError) { batch.RecordCleanupFailure(cleanupError); }
            }
            return new(ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex,
                ex is OperationCanceledException ? ApiErrorKind.Cancelled : ex is ArgumentException ? ApiErrorKind.InvalidArgument : ApiErrorKind.OperationFailed), batch);
        }
    }

    private static async Task<VmImportSession> PrepareSessionAsync(
        string sourcePath,
        VmImportSourceKind kind,
        VmImportPlacementMode placementMode,
        SourceLayout layout,
        ImportArchiveContext? archiveContext,
        CancellationToken cancellationToken)
    {
        VmcxPreviewData data = await ReadVmcxPreviewAsync(layout.MainConfigurationPath, cancellationToken);
        Guid originalGuid = data.Guid != Guid.Empty
            ? data.Guid
            : TryReadGuidFromConfigurationName(layout.MainConfigurationPath);
        var preview = new VmImportPreview
        {
            Name = data.Name,
            OriginalGuid = originalGuid,
            SourceSwitchIds = Array.AsReadOnly(data.SourceSwitchIds.ToArray()),
            Generation = data.Generation,
            ConfigurationVersion = data.ConfigurationVersion,
            Created = data.Created,
            OsType = data.OsType,
            ProcessorCount = data.ProcessorCount,
            StartupMemoryMiB = data.StartupMemoryMiB
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
            ArchiveContext = archiveContext,
            Preview = preview
        };

        await PopulateDiskPreviewsAsync(session, data.Disks, cancellationToken);

        return session;
    }

    /// <summary>将一个已准备的导入会话注册为虚拟机，按所选模式复制或重用配置和磁盘。</summary>
    /// <param name="session">由预览创建的单机会话；目录来源沿用预览解析的源磁盘路径，文件消失后返回失败，不改用其他同名磁盘。此方法按需解压和复制文件，不支持并发重复导入同一会话。</param>
    /// <param name="options">该虚拟机的导入选择，必须明确 GUID 策略；名称、目录及交换机映射未指定时沿用原规则。选项校验失败不会开始导入，可以修正选项后重试。</param>
    /// <param name="progress">可选进度接收器；null 时不通知。回调线程由具体 IProgress 实现决定。</param>
    /// <param name="cancellationToken">协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。</param>
    /// <returns>返回独立的虚拟机 GUID、已实现标记、原生状态是否未知及清理结果；失败不表示未注册或没有留下资源。</returns>
    /// <remarks>进入最后的实现计划系统步骤后不再取消，返回实际实现结果；此前的原生写入先确认终态再响应取消。已导入的虚拟机予以保留，由调用者管理；会话释放清理工作资源，原生状态未确认时保留资源并报告。网络引用不存在时按导入规则断开相关连接。一旦开始执行导入，无论成功或失败，该会话都不再重新导入；重试需先清理并重新准备预览。</remarks>
    /// <exception cref="ObjectDisposedException">会话已请求释放，不能再次导入。</exception>
    /// <exception cref="InvalidOperationException">同一会话正用于其他导入或批次准备操作。</exception>
    /// <instanceentry label="导入虚拟机" category="导入" order="10">
    /// <step>调用 <c>PreparePreviewsAsync</c> 解析来源目录或 ZIP，取得导入批次并检查预览。使用 <c>await using</c> 管理返回批次。</step>
    /// <step>ZIP 需要密码、解压额度或指定预览工作目录时，在准备入口传入 <c>VmImportArchiveOptions</c>；这些参数及累计额度会沿用到后续导入，不在 ImportAsync 重新设置。</step>
    /// <step>检查预览并创建 <c>VmImportOptions</c>，明确 Preserve 或 GenerateNew；按需填写名称、完整目标目录和源交换机 GUID 映射。预览不用于修改选项。</step>
    /// <step>从 <c>batch.VirtualMachines</c> 选择一个会话，调用 <c>ImportAsync(session, options)</c>；查看结果中的 GUID、注册状态和清理信息。方法会自行校验来源、目标和网络；释放批次只清理工作资源，不删除已导入虚拟机。</step>
    /// </instanceentry>
    public static async Task<VmImportResult> ImportAsync(
        VmImportSession session,
        VmImportOptions options,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var operation = session.Lifetime.Enter(cancellationToken);
        return await ImportSessionCoreAsync(session, options, progress, operation.Token);
    }

    private static async Task<VmImportResult> ImportSessionCoreAsync(
        VmImportSession session, VmImportOptions options, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        RefreshRetainedImportPaths(session);
        if (session.IsRealized)
            return GetImportResult(session, false, "此会话已成功导入虚拟机，不能重复执行。");
        if (session.OperationStateUnknown)
            return GetImportResult(session, false, "此前原生操作状态尚未确认，不能重复执行导入。");
        if (!string.IsNullOrWhiteSpace(session.PlannedSystemPath)
            || session.CleanupState is VmImportCleanupState.Failed or VmImportCleanupState.Deferred)
            return GetImportResult(session, false, "此前导入仍有待清理资源，请释放该会话并完成清理后重新准备预览。");
        if (session.ImportStarted)
            return GetImportResult(session, false, "此会话已经执行过导入，请释放并重新准备预览后再试。");
        if (GetPreviewError([session.Preview]) is string previewError)
            return GetImportResult(session, false, previewError);

        try { await ConfigureImportOptionsAsync(session, options, cancellationToken); }
        catch (Exception ex) { return GetImportResult(session, false, ex.Message) with { Failure = ApiFailure.From(ex) }; }

        var createdPaths = session.OwnedCreatedPaths;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.ImportStarted = true;
            progress?.Report(2);
            if (session.SourceKind == VmImportSourceKind.Zip && !session.ArchiveFullyExtracted)
            {
                await ExtractArchiveToTargetStagingAsync(session, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                CommitZipStaging(session, createdPaths);
                session.ArchiveFullyExtracted = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await CreatePlannedSystemAsync(session, cancellationToken);
            await ImportSnapshotDefinitionsAsync(session, cancellationToken);
            await ApplyImportNetworksAsync(session, cancellationToken);

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

                await MovePlannedDataRootsAsync(session, configRoot, cancellationToken);
                await CopyAndRelinkDisksAsync(session, diskRoot, createdPaths, progress, cancellationToken);
            }
            else
            {
                // ImportSystemDefinition 创建计划虚拟机，后续由 RealizePlannedSystem 完成注册。
                // 导出配置可能仍携带源主机的默认根，RealizePlannedSystem 会据此把配置
                // 写进当前主机默认目录。显式指向本次选择的单 VM 根目录，才能保证
                // 配置、检查点与分页文件继续留在来源目录中。
                await MovePlannedDataRootsAsync(
                    session,
                    session.SourceRoot,
                    cancellationToken);
                // 导出配置会保留源主机上的绝对 VHD 路径。即使那个旧路径当前仍存在，
                // “使用现有目录”也必须把计划虚拟机显式改指向用户选择目录内的磁盘，
                // 否则注册成功后会悄悄继续使用源盘，目录本身并不自包含。
                await RelinkPlannedDisksToSourceAsync(session, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(82);
            session.RemovedMissingGroupCount += await VmGroupService.RemoveMissingImportGroupsAsync(
                session.PlannedSystemPath, session.CleanupGuard, cancellationToken);
            await ValidatePlannedSystemAsync(session.PlannedSystemPath, session.CleanupGuard, cancellationToken);

            progress?.Report(88);
            cancellationToken.ThrowIfCancellationRequested();
            await InvokeObservedImportMethodAsync(session, "RealizePlannedSystem",
                input => input["PlannedSystem"] = session.PlannedSystemPath,
                "ResultingSystem", "Msvm_ComputerSystem", path =>
                {
                    session.IsRealized = true;
                    session.PlannedSystemPath = string.Empty;
                    session.OwnedCreatedPaths.Clear();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        using var realized = new ManagementObject(path);
                        realized.Get();
                        if (Guid.TryParse(realized["Name"]?.ToString(), out Guid id)) session.PlannedGuid = id;
                    }
                }, CancellationToken.None);
            progress?.Report(100);
            RefreshRetainedImportPaths(session);
            return GetImportResult(session, true);
        }
        catch (Exception ex)
        {
            try
            {
                if (!session.IsRealized) await CleanupFailedImportAsync(session);
                else if (session.OperationStateUnknown) RecordDeferredImportCleanup(session);
                else RefreshRetainedImportPaths(session);
            }
            catch (Exception cleanupError) { session.RecordCleanupFailure(cleanupError); }
            return GetImportResult(session, false, ex.Message) with { Failure = ApiFailure.From(ex), ArchiveFailure = (ex as VmImportArchiveException)?.ToFailure() };
        }
    }

    /// <summary>预先检查同一来源的预览和宿主交换机查询是否可用；不应用导入选项。</summary>
    /// <param name="batch">由 PreparePreviewsAsync 返回的批次；使用完成后必须释放。</param>
    /// <param name="cancellationToken">协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。</param>
    /// <returns>预览或交换机查询结果。此可选入口不执行导入，也不预留目录或 GUID；ImportAsync 会按自己的选项重新校验。</returns>
    /// <exception cref="ObjectDisposedException">批次或其中某个会话已请求释放。</exception>
    /// <exception cref="InvalidOperationException">批次或其中某个会话正在使用，不能同时更改准备信息。</exception>
    public static async Task<ApiResponse> PrepareBatchImportAsync(
        VmImportBatchSession batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var operation = batch.EnterOperation(cancellationToken);
        return await PrepareBatchImportCoreAsync(batch, operation.Token);
    }

    private static async Task<ApiResponse> PrepareBatchImportCoreAsync(
        VmImportBatchSession batch, CancellationToken cancellationToken)
    {
        if (GetPreviewError(batch.VirtualMachines.Select(session => session.Preview)) is string previewError)
            return ApiResponse.Fail(previewError);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GetAvailableSwitchesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ApiResponse.Ok();
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex,
                ex is OperationCanceledException ? ApiErrorKind.Cancelled : ApiErrorKind.OperationFailed);
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

    private static async Task<IReadOnlyList<SourceLayout>> PrepareZipLayoutsAsync(
        string archivePath,
        VmImportBatchSession batch,
        CancellationToken cancellationToken)
    {
        var context = batch.ArchiveContext ?? throw new InvalidOperationException("缺少 ZIP 读取选项。");
        using var archive = ZipArchive.OpenRead(archivePath, context);
        var entries = ValidateArchiveEntries(archive);
        string temp = Path.Combine(context.TemporaryDirectory, "ExHyperV-Import-" + Guid.NewGuid().ToString("N"));
        // 在首次写文件前记录所有者，预览失败时仍能返回清理与重试入口。
        batch.TemporaryRoot = temp;
        Directory.CreateDirectory(temp);
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

        return layouts;
    }

    private static Task<VmcxPreviewData> ReadVmcxPreviewAsync(
        string configurationPath,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<VmcxNode> nodes = VmcxMetadata.ReadSnapshot(configurationPath);
            IReadOnlyDictionary<string, string> hostOnlyItems = VmcxMetadata.ReadHostOnlyItems(nodes);
            hostOnlyItems.TryGetValue(VmMetadataService.OsTypeKey, out string? osType);
            Dictionary<string, VmcxNode> values = nodes
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
                OsImages.Canonical(osType),
                checked((int)Math.Max(0, ReadInteger("/configuration/settings/processors/count"))),
                checked((ulong)Math.Max(0, ReadInteger("/configuration/settings/memory/bank/size"))),
                disks,
                values.Values.Where(node => Regex.IsMatch(node.Path,
                    @"^/configuration/_[^/]+_/SwitchName$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    .Select(node => Guid.TryParse(node.Value, out Guid id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty).Distinct().Order().ToArray());
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
            using ZipArchive archive = ZipArchive.OpenRead(session.SourcePath, session.ArchiveContext!);
            Dictionary<string, ZipArchiveEntry> entries = ValidateArchiveEntries(archive)
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
                    session.Preview.AddDisk(CreateMissingDiskPreview(reference));
                    session.Preview.AddIssue(
                        $"找不到虚拟硬盘：{Path.GetFileName(reference.ConfiguredPath)}");
                    continue;
                }

                VhdMetadata metadata = await ReadArchiveDiskMetadataAsync(
                    entry,
                    cancellationToken);
                session.Preview.AddDisk(new VmImportDiskPreview
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
                session.Preview.AddDisk(CreateMissingDiskPreview(reference));
                session.Preview.AddIssue(
                    $"找不到虚拟硬盘：{Path.GetFileName(reference.ConfiguredPath)}");
                continue;
            }

            VhdMetadata metadata = await ReadLocalVhdMetadataAsync(sourcePath, cancellationToken);
            long actualSize = new FileInfo(sourcePath).Length;
            session.Preview.AddDisk(new VmImportDiskPreview
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
        session.PlannedSystemDestructionConfirmed = false;

        // 在原生创建前再核对；不把保留身份的冲突悄悄变成复制新身份。
        if (!session.GenerateNewGuid)
            ValidateImportGuid(session.Preview.OriginalGuid, VmImportGuidMode.Preserve, await GetUsedVmGuidsAsync());

        await InvokeObservedImportMethodAsync(session, "ImportSystemDefinition", input =>
        {
            input["SystemDefinitionFile"] = session.MainConfigurationPath;
            input["SnapshotFolder"] = session.SnapshotFolder;
            input["GenerateNewSystemIdentifier"] = session.GenerateNewGuid;
        }, "ImportedSystem", "Msvm_PlannedComputerSystem", path =>
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Hyper-V 没有返回计划虚拟机。");
            session.PlannedSystemPath = path;
            using var planned = new ManagementObject(path);
            planned.Get();
            if (!Guid.TryParse(planned["Name"]?.ToString(), out Guid id) || id == Guid.Empty)
                throw new InvalidDataException("计划虚拟机没有返回有效 GUID。");
            session.PlannedGuid = id;
        }, cancellationToken);
        // 身份已取得且原生创建已结束；策略不符是已知失败，仍可清理本次计划系统。
        if ((!session.GenerateNewGuid && session.PlannedGuid != session.Preview.OriginalGuid)
            || (session.GenerateNewGuid && session.PlannedGuid == session.Preview.OriginalGuid))
            throw new InvalidDataException("Hyper-V 返回的身份不符合本次 GUID 选择，未继续实现虚拟机。");
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
            await InvokeObservedImportMethodAsync(session, "ImportSnapshotDefinitions", input =>
            {
                input["PlannedSystem"] = session.PlannedSystemPath;
                input["SnapshotFolder"] = snapshotFolder;
            }, null, null, null, cancellationToken);
        }
    }

    private static async Task ApplyImportNetworksAsync(
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

        var available = await GetAvailableSwitchesAsync(cancellationToken);
        var items = allocations.Data ?? [];
        var sourceIds = items.Select(item => Guid.TryParse(ExtractNameKey(item.Host), out Guid id) ? id : Guid.Empty).ToHashSet();
        if (session.SwitchMappings.Keys.Any(id => !sourceIds.Contains(id)))
            throw new InvalidOperationException("计划虚拟机的网络连接与来源预览不一致，未应用交换机映射。");
        // 先解析全部明确指定的目标，防止发现缺失目标前已修改其他连接。
        var changes = items.Select(item => (item.Path, Target: ResolveImportNetworkTarget(
            Guid.TryParse(ExtractNameKey(item.Host), out Guid id) ? id : Guid.Empty,
            session.SwitchMappings, available))).Where(item => item.Target != null).ToArray();
        foreach (var item in changes)
        {
            using var allocation = new ManagementObject(item.Path);
            allocation.Get();
            allocation["EnabledState"] = (ushort)(item.Target!.Length == 0 ? 3 : 2);
            allocation["HostResource"] = item.Target.Length == 0 ? Array.Empty<string>() : new[] { item.Target };
            await InvokeObservedImportMethodAsync(session, "ModifyResourceSettings",
                input => input["ResourceSettings"] = new[] { allocation.GetText(TextFormat.CimDtd20) },
                null, null, null, cancellationToken);
        }
    }

    private static async Task MovePlannedDataRootsAsync(
        VmImportSession session,
        string configRoot,
        CancellationToken cancellationToken)
    {
        using var planned = new ManagementObject(session.PlannedSystemPath);
        planned.Get();
        using var settingsCollection = planned.GetRelated("Msvm_VirtualSystemSettingData");
        using var settings = settingsCollection.Cast<ManagementObject>().FirstOrDefault(item =>
                string.Equals(item["VirtualSystemType"]?.ToString(),
                    "Microsoft:Hyper-V:System:Planned", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("计划虚拟机缺少主配置数据。");
        settings["ElementName"] = session.TargetName;
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

        await InvokeObservedImportMethodAsync(session, "ModifySystemSettings",
            input => input["SystemSettings"] = settings.GetText(TextFormat.CimDtd20),
            null, null, null, cancellationToken);
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
            await SetParentAsync(session, child, parent, cancellationToken);
        }

        foreach (var allocationInfo in allocations)
        {
            string source = ResolveSourceDiskPath(session, allocationInfo.HostResource);
            if (!destinationBySource.TryGetValue(source, out string? destination)) continue;
            using var allocation = new ManagementObject(allocationInfo.Path);
            allocation.Get();
            allocation["HostResource"] = new[] { destination };
            await InvokeObservedImportMethodAsync(session, "ModifyResourceSettings",
                input => input["ResourceSettings"] = new[] { allocation.GetText(TextFormat.CimDtd20) },
                null, null, null, cancellationToken);
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

        foreach (DiskAllocation allocationInfo in allocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var allocation = new ManagementObject(allocationInfo.Path);
            allocation.Get();
            allocation["HostResource"] = new[] { sourceByAllocation[allocationInfo.Path] };
            await InvokeObservedImportMethodAsync(session, "ModifyResourceSettings",
                input => input["ResourceSettings"] = new[] { allocation.GetText(TextFormat.CimDtd20) },
                null, null, null, cancellationToken);
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

    private static Task SetParentAsync(VmImportSession session, string child, string parent, CancellationToken cancellationToken)
        => InvokeObservedImportMethodAsync(session, "SetParentVirtualHardDisk", input =>
        {
            input["ChildPath"] = child;
            input["ParentPath"] = parent;
            input["LeafPath"] = null;
            input["IgnoreIDMismatch"] = false;
        }, null, null, null, cancellationToken, imageService: true);

    private static Task InvokeObservedImportMethodAsync(VmImportSession session, string method,
        Action<ManagementBaseObject> configure, string? resultField, string? resultClass,
        Action<string?>? captureResult, CancellationToken cancellationToken, bool imageService = false) => Task.Run(async () =>
    {
        using var service = (imageService ? WmiApi.GetImageManagementService() : WmiApi.GetVirtualSystemManagementService())
            ?? throw new InvalidOperationException("未找到 Hyper-V 操作服务。");
        using var input = service.GetMethodParameters(method);
        configure(input);
        string? jobPath = null;
        uint returnCode = 0;
        string? directPath = null;
        string? failure = null;
        ApiResponse? jobCompletion = null;
        await session.CleanupGuard.ObserveAsync(() =>
        {
            using var output = service.InvokeMethod(method, input, null)
                ?? throw new InvalidOperationException($"{method} 没有返回提交结果。");
            object returned = output["ReturnValue"]
                ?? throw new InvalidOperationException($"{method} 没有返回状态码，提交结果尚未确认。");
            returnCode = Convert.ToUInt32(returned);
            if (resultField != null) directPath = output[resultField] as string;
            if (returnCode != 4096) return Task.FromResult<string?>(null);
            jobPath = output["Job"] as string
                ?? throw new InvalidOperationException($"{method} 没有返回原生作业身份。");
            return Task.FromResult<string?>(jobPath);
        }, () =>
        {
            using var job = new ManagementObject(jobPath!);
            job.Get();
            jobCompletion = EvaluateImportJobCompletion(WmiJobWaiter.Read(job));
            return Task.FromResult(jobCompletion is not null);
        }, cancellationToken, TimeSpan.FromMinutes(30), TimeSpan.FromMilliseconds(150), () =>
        {
            if (returnCode != 0 && returnCode != 4096)
                failure = $"{method} 返回错误码 {returnCode}。";
            else if (jobPath != null && jobCompletion?.Success != true)
                failure = jobCompletion?.Error ?? $"{method} 执行结果尚未确认。";
            if (failure != null) return Task.CompletedTask;
            if (captureResult != null)
            {
                string? path = directPath;
                if (jobPath != null && resultClass != null)
                {
                    using var job = new ManagementObject(jobPath);
                    using var related = job.GetRelated(resultClass, "Msvm_AffectedJobElement",
                        null, null, null, null, false, null);
                    foreach (ManagementObject result in related)
                    {
                        using (result) { path = result.Path.Path; }
                        break;
                    }
                }
                captureResult(path);
            }
            return Task.CompletedTask;
        });
        if (failure != null) throw new CoreOperationException(
            jobCompletion is { Success: false } ? jobCompletion : ApiResponse.Fail(failure,
                unchecked((int)returnCode), ApiErrorSource.Wmi,
                kind: returnCode == 4096 ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed));
    }, cancellationToken);

    internal static Task<string[]> ValidatePlannedSystemAsync(string plannedPath, VmImportCleanupGuard guard,
        CancellationToken cancellationToken, bool allowValidationErrors = false, TimeSpan? timeout = null) => Task.Run(async () =>
    {
        if (new ManagementPath(plannedPath).ClassName != "Msvm_PlannedComputerSystem")
            throw new ArgumentException(nameof(plannedPath));
        using var service = WmiApi.GetVirtualSystemManagementService();
        using var input = service.GetMethodParameters("ValidatePlannedSystem");
        input["PlannedSystem"] = plannedPath;
        string? jobPath = null;
        uint result = 0;
        ApiResponse? jobCompletion = null;
        await guard.ObserveAsync(() =>
        {
            using var output = service.InvokeMethod("ValidatePlannedSystem", input, null)
                ?? throw new InvalidOperationException("ValidatePlannedSystem 没有返回提交结果。");
            object returned = output["ReturnValue"]
                ?? throw new InvalidOperationException("ValidatePlannedSystem 没有返回状态码，提交结果尚未确认。");
            result = Convert.ToUInt32(returned);
            if (result != 4096) return Task.FromResult<string?>(null);
            jobPath = output["Job"] as string
                ?? throw new InvalidOperationException("ValidatePlannedSystem 没有返回原生作业身份。");
            return Task.FromResult<string?>(jobPath);
        }, () =>
        {
            using var job = new ManagementObject(jobPath!);
            job.Get();
            jobCompletion = EvaluateImportJobCompletion(WmiJobWaiter.Read(job));
            return Task.FromResult(jobCompletion is not null);
        }, cancellationToken, timeout ?? TimeSpan.FromMinutes(30), TimeSpan.FromMilliseconds(150));
        if (result == 0) return Array.Empty<string>();
        if (result != 4096) throw new InvalidOperationException($"ValidatePlannedSystem: {result}");
        if (jobCompletion?.Success == true)
            return Array.Empty<string>();
        if (!allowValidationErrors)
            throw new InvalidOperationException(jobCompletion?.Error ?? "ValidatePlannedSystem failed.");
        using var job = new ManagementObject(jobPath!);
        using var errorInput = job.GetMethodParameters("GetErrorEx");
        using var errors = job.InvokeMethod("GetErrorEx", errorInput, null);
        if (Convert.ToUInt32(errors["ReturnValue"]) != 0) throw new InvalidOperationException("GetErrorEx");
        return errors["Errors"] as string[] ?? [];
    }, cancellationToken);

    internal static ApiResponse? EvaluateImportJobCompletion(WmiJobObservation observation)
    {
        var result = WmiJobWaiter.Evaluate(observation, hyperV: true);
        // 未知状态必须在 ObserveAsync 解除保护前抛出，保留计划系统及导入工作文件。
        if (result is not null) OperationOutcomeUnknownException.ThrowIfUnknown(result);
        return result;
    }

    internal static async Task<bool> DestroyPlannedSystemAsync(VmImportSession session)
    {
        string plannedPath = session.PlannedSystemPath;
        if (new ManagementPath(plannedPath).ClassName != "Msvm_PlannedComputerSystem")
            throw new InvalidOperationException("计划系统身份类型不正确，不能清理。");
        if (!session.PlannedSystemDestructionConfirmed)
        {
            await InvokeObservedImportMethodAsync(session, "DestroySystem",
                input => input["AffectedSystem"] = plannedPath,
                null, null, _ => session.PlannedSystemDestructionConfirmed = true,
                CancellationToken.None);
        }
        // 已确认本次 Destroy 成功之后才检查对象消失；读取失败时保留收据供下次重试。
        using var planned = new ManagementObject(plannedPath);
        try { planned.Get(); }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound) { return true; }
        return false;
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
        var (configStaging, diskStaging) = GetImportStagingRoots(
            session.TargetConfigurationRoot, session.TargetDiskRoot, token);

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
            using var archive = ZipArchive.OpenRead(session.SourcePath, session.ArchiveContext!);
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
            session.ArchiveContext!.CheckRemaining(entries.Sum(entry => entry.Length));
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

        var moves = new[]
            {
                (Staging: configStaging, Target: session.TargetConfigurationRoot),
                (Staging: diskStaging, Target: session.TargetDiskRoot)
            }
            .DistinctBy(item => item.Staging, StringComparer.OrdinalIgnoreCase)
            .Where(item => !new[] { configStaging, diskStaging }.Any(parent =>
                !string.Equals(item.Staging, parent, StringComparison.OrdinalIgnoreCase) && IsWithin(item.Staging, parent)))
            .ToArray();

        foreach (var move in moves) PrepareTargetForDirectoryMove(move.Target);
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
        long actualLength = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (actualLength > entry.Length || read > entry.Length - actualLength)
                throw CreateZipEntryValidationException(entry);

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            actualLength += read;
            copied?.Invoke(read);
        }

        // CoreZipArchive checks CRC (AE-1/plain) or AES authentication as the entry reaches EOF.
        if (actualLength != entry.Length)
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
        if (offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset)
            throw new InvalidDataException("虚拟磁盘元数据范围超出文件大小。");
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

    private static async Task<IReadOnlyDictionary<Guid, string>> GetAvailableSwitchesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var switches = await WmiApi.QueryAsync(
            "SELECT * FROM Msvm_VirtualEthernetSwitch",
            obj => new { Id = obj["Name"]?.ToString() ?? string.Empty, Path = obj.Path.Path });
        if (!switches.Success) throw new InvalidOperationException(switches.Error);
        var result = new Dictionary<Guid, string>();
        foreach (var item in switches.Data ?? [])
        {
            if (!Guid.TryParse(item.Id, out Guid id) || id == Guid.Empty || string.IsNullOrWhiteSpace(item.Path) || !result.TryAdd(id, item.Path))
                throw new InvalidDataException("交换机清单包含无效或重复身份，未执行导入。");
        }
        return result;
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
        // Folder previews fix the chosen source. A vanished export disk must not
        // silently become the still-existing disk at the original host path.
        if (session.SourceKind != VmImportSourceKind.Folder)
            return ResolveSourceDiskPathCore(session, configuredPath);
        if (session.ResolvedSourceDisks.TryGetValue(configuredPath, out string? selected))
            return selected;
        selected = ResolveSourceDiskPathCore(session, configuredPath);
        session.ResolvedSourceDisks.Add(configuredPath, selected);
        return selected;
    }

    private static string ResolveSourceDiskPathCore(VmImportSession session, string configuredPath)
    {
        if (session.SourceKind == VmImportSourceKind.Zip && session.ArchiveFullyExtracted)
        {
            if (TryFindArchiveDiskEntry(session, configuredPath, null, out string? entryName)
                && entryName != null
                && session.ExtractedArchivePaths?.TryGetValue(entryName, out string? extracted) == true)
            {
                return extracted;
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

    private static async Task<IReadOnlyList<string>> CleanupCreatedPathsAsync(IEnumerable<string> paths)
    {
        var remaining = new List<string>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length))
        {
            bool removed = false;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    else if (Directory.Exists(path)) Directory.Delete(path, true);
                    removed = IsImportPathMissing(path);
                    if (removed) break;
                }
                catch { }
                if (attempt < 5) await Task.Delay(250);
            }
            if (!removed) remaining.Add(path);
        }
        return remaining.AsReadOnly();
    }

    private static async Task CleanupFailedImportAsync(VmImportSession session)
    {
        await CleanupSessionAsync(session);
    }

    internal static async Task CleanupSessionAsync(VmImportSession session)
    {
        if (session.CleanupGuard.IsBlocked && !await session.CleanupGuard.TryResolveAsync())
        {
            RecordDeferredImportCleanup(session);
            return;
        }
        if (!session.IsRealized && !string.IsNullOrWhiteSpace(session.PlannedSystemPath))
        {
            try
            {
                if (!await DestroyPlannedSystemAsync(session))
                    throw new InvalidOperationException("计划系统仍然存在，相关工作资源已保留。");
            }
            catch (Exception ex)
            {
                if (session.OperationStateUnknown) RecordDeferredImportCleanup(session);
                else
                {
                    session.CleanupState = VmImportCleanupState.Failed;
                    session.CleanupError = $"计划系统清理失败，可再次释放以重试：{ex.Message}";
                    session.CleanupFailure = ApiFailure.From(ex);
                    RefreshRetainedImportPaths(session);
                }
                return;
            }
        }
        session.PlannedSystemPath = string.Empty;
        if (!session.IsRealized)
        {
            var remaining = await CleanupCreatedPathsAsync(session.OwnedCreatedPaths);
            session.OwnedCreatedPaths.Clear();
            session.OwnedCreatedPaths.AddRange(remaining);
        }
        else session.OwnedCreatedPaths.Clear();
        TryDeleteImportStaging(session);
        if (session.OwnsTemporaryRoot && TryDeleteTemporaryRoot(session.TemporaryRoot)) session.TemporaryRoot = null;
        RefreshRetainedImportPaths(session);
        // 共享解压根由批次持有，不属于单机会话释放范围。
        bool failed = session.OwnedCreatedPaths.Count > 0 || session.ConfigurationStagingRoot != null
            || session.DiskStagingRoot != null || (session.OwnsTemporaryRoot && session.TemporaryRoot != null);
        session.CleanupState = failed ? VmImportCleanupState.Failed : VmImportCleanupState.Completed;
        session.CleanupError = failed ? "部分导入工作文件或目录未能删除，请检查 RetainedPaths。" : null;
        session.CleanupFailure = failed ? new(ApiErrorKind.CleanupRequired, -1, ApiErrorSource.None, session.CleanupError!) : null;
    }

    private static void RecordDeferredImportCleanup(VmImportSession session)
    {
        session.CleanupState = VmImportCleanupState.Deferred;
        session.CleanupError = "原生任务结束或计划系统移除尚未确认，已保留相关工作资源。";
        session.CleanupFailure = new(ApiErrorKind.OutcomeUnknown, -1, ApiErrorSource.None, session.CleanupError);
        RefreshRetainedImportPaths(session);
    }

    internal static void RefreshRetainedImportPaths(VmImportSession session)
    {
        session.RetainedPaths = Array.AsReadOnly(new[] { session.TemporaryRoot,
                session.ConfigurationStagingRoot, session.DiskStagingRoot }
            .Concat(session.IsRealized ? [] : session.OwnedCreatedPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
            .Where(path => !IsImportPathMissing(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static VmImportResult GetImportResult(VmImportSession session, bool success, string? error = null)
        => new(success, session.PlannedGuid == Guid.Empty ? null : session.PlannedGuid,
            session.IsRealized, session.OperationStateUnknown, session.CleanupState,
            session.RetainedPaths, error, session.CleanupError) { CleanupFailure = session.CleanupFailure };

    internal static bool IsImportPathMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

    internal static void TryDeleteImportStaging(VmImportSession session)
    {
        if (TryDeleteTemporaryRoot(session.ConfigurationStagingRoot)) session.ConfigurationStagingRoot = null;
        if (TryDeleteTemporaryRoot(session.DiskStagingRoot)) session.DiskStagingRoot = null;
    }

    internal static bool TryDeleteTemporaryRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return IsImportPathMissing(path);
        }
        catch { return false; }
    }
}
