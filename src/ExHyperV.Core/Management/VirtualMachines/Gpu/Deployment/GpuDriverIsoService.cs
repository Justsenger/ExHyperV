using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed record GpuDriverIsoManifest(
    IReadOnlyList<GpuDriverIsoRuleEntry> Rules,
    IReadOnlyList<GpuNvidiaServiceSpec> NvidiaServices,
    IReadOnlyList<GpuIntelRuntimeSpec> IntelRuntimes,
    IReadOnlyList<GpuProgramFolderSpec> ProgramFolders);

internal sealed record GpuDriverIsoRuleEntry(
    string SourceName,
    string TargetName,
    string TargetSubDir,
    string Mode,
    IReadOnlyList<string>? PreferredPackageNames,
    bool RequirePreferredPackage);

internal static class GpuDriverIsoService
{
    internal static string GetDefaultIsoPath(Guid vmId)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExHyperV",
            "DriverIsos",
            $"{vmId:D}-GpuDrivers.iso");

    internal static async Task<ApiResponse<string>> BuildAndMountDriverIsoAsync(
        Guid vmId,
        IReadOnlyList<HostGpuInfo> gpus,
        Action<int>? progressPercent = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<string>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        if (gpus == null || gpus.Count == 0)
            return ApiResponse<string>.Fail("请至少选择一张宿主显卡。", kind: ApiErrorKind.InvalidArgument);

        string isoPath = GetDefaultIsoPath(vmId);
        return await BuildAndMountDriverIsoToPathAsync(vmId, gpus, isoPath, progressPercent, log, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ApiResponse<string>> BuildAndMountDriverIsoToPathAsync(
        Guid vmId,
        IReadOnlyList<HostGpuInfo> gpus,
        string isoPath,
        Action<int>? progressPercent = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string stagingDir = Path.Combine(Path.GetTempPath(), $"ExHyperV-GpuIso-{Guid.NewGuid():N}");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke("正在解析显卡驱动包与注册表依赖...");

            var (manifest, hostFileRepository) = await Task.Run(() => BuildManifestFromHost(gpus, log), cancellationToken).ConfigureAwait(false);

            long estimatedBytes = await Task.Run(
                () => EstimateTotalSourceBytes(hostFileRepository, manifest.ProgramFolders, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            string? freeSpaceError = ValidateFreeDiskSpace(isoPath, estimatedBytes);
            if (freeSpaceError != null)
                return ApiResponse<string>.Fail(freeSpaceError, kind: ApiErrorKind.OperationFailed);

            CreateStagingPayload(stagingDir, manifest);

            var sources = new List<IsoDirectorySource>
            {
                new(stagingDir, string.Empty),
                new(hostFileRepository, Path.Combine("_payload", "HostDriverStore", "FileRepository"))
            };
            foreach (GpuProgramFolderSpec folder in manifest.ProgramFolders)
                sources.Add(new IsoDirectorySource(folder.SourcePath, folder.IsoSubDirectory));

            log?.Invoke($"正在生成驱动光盘：{isoPath}");
            await Task.Run(() => ComApi.RunOnSta(() =>
            {
                IsoBuilderService.BuildUdfIso(sources, isoPath, "EXHYPERV_GPU", progressPercent, cancellationToken);
                return true;
            }), cancellationToken).ConfigureAwait(false);

            log?.Invoke("正在将驱动光盘挂载至虚拟机...");
            var mountResult = await MountIsoToVmAsync(vmId, isoPath).ConfigureAwait(false);
            if (!mountResult.Success)
                return ApiResponse<string>.Fail(mountResult.Error, mountResult.Code, mountResult.ErrorSource, mountResult.Exception, mountResult.ErrorKind);

            return ApiResponse<string>.Ok(isoPath);
        }
        catch (OperationCanceledException)
        {
            return ApiResponse<string>.Fail("已取消生成驱动光盘。", kind: ApiErrorKind.Cancelled);
        }
        catch (Exception ex)
        {
            return ApiResponse<string>.Fail(ex.Message, exception: ex);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); }
                catch (Exception diagnosticError) { CoreDiagnostics.Report("DriverStagingCleanupFailed", error: diagnosticError); }
            }
        }
    }

    internal static (GpuDriverIsoManifest Manifest, string HostFileRepository) BuildManifestFromHost(
        IReadOnlyList<HostGpuInfo> gpus,
        Action<string>? log = null)
    {
        var rules = new List<GpuDriverIsoRuleEntry>();
        var nvidiaServices = new List<GpuNvidiaServiceSpec>();
        var intelRuntimes = new List<GpuIntelRuntimeSpec>();
        var vendors = new HashSet<GpuDriverVendor>();
        string? hostFileRepository = null;

        foreach (HostGpuInfo gpu in gpus)
        {
            string gpuPath = string.IsNullOrEmpty(gpu.PartitionableGpuPath) ? gpu.InstanceId : gpu.PartitionableGpuPath;
            GpuDriverPackagePlan plan = GpuDriverPackageResolver.Resolve(gpuPath, gpu.Manu);
            foreach (string warning in plan.Warnings) log?.Invoke(warning);
            hostFileRepository ??= plan.HostFileRepository;
            if (plan.Vendor != GpuDriverVendor.Unknown) vendors.Add(plan.Vendor);

            foreach (GpuFilePromotionRule rule in GpuDriverPromotionBuilder.CollectRegistryPromotions(plan, includeWow64: true))
                rules.Add(ToEntry(rule));
            foreach (GpuFilePromotionRule rule in GpuDriverPromotionBuilder.GetVendorPromotions(plan))
                rules.Add(ToEntry(rule));

            if (plan.Vendor == GpuDriverVendor.Nvidia)
            {
                if (GpuDriverPromotionBuilder.ReadNvidiaServiceSpec(plan) is { } nvidiaSpec)
                    nvidiaServices.Add(nvidiaSpec);
            }
            else if (plan.Vendor == GpuDriverVendor.Intel)
            {
                if (GpuDriverPromotionBuilder.ReadIntelRuntimeSpec(plan) is { } intelSpec)
                    intelRuntimes.Add(intelSpec);
            }
        }

        if (string.IsNullOrWhiteSpace(hostFileRepository) || !Directory.Exists(hostFileRepository))
            throw new DirectoryNotFoundException("未找到宿主机的 DriverStore\\FileRepository 目录。");

        IReadOnlyList<GpuProgramFolderSpec> programFolders = GpuDriverPromotionBuilder.CollectVendorProgramFolders(vendors);
        return (new GpuDriverIsoManifest(rules, nvidiaServices, intelRuntimes, programFolders), hostFileRepository);
    }

    internal static string? ValidateFreeDiskSpace(string isoPath, long estimatedSourceBytes, Func<string, long>? availableSpaceReader = null)
    {
        string fullIsoPath = Path.GetFullPath(isoPath);
        string root = Path.GetPathRoot(fullIsoPath)
            ?? throw new IOException($"无法识别驱动光盘输出路径的磁盘根目录：{isoPath}");

        long freeBytes = availableSpaceReader != null
            ? availableSpaceReader(root)
            : new DriveInfo(root).AvailableFreeSpace;
        long requiredBytes = (long)(estimatedSourceBytes * 1.12) + 32L * 1024 * 1024;

        if (freeBytes < requiredBytes)
        {
            double needGiB = requiredBytes / (1024.0 * 1024 * 1024);
            double availGiB = freeBytes / (1024.0 * 1024 * 1024);
            return $"磁盘 {root.TrimEnd('\\')} 空间不足以生成驱动光盘：预计需要 {needGiB:F1} GB，当前可用 {availGiB:F1} GB。";
        }
        return null;
    }

    private static long EstimateTotalSourceBytes(
        string hostFileRepository,
        IReadOnlyList<GpuProgramFolderSpec> programFolders,
        CancellationToken cancellationToken)
    {
        long total = SumDirectoryBytes(hostFileRepository, cancellationToken);
        foreach (GpuProgramFolderSpec folder in programFolders)
            total += SumDirectoryBytes(folder.SourcePath, cancellationToken);
        return total;
    }

    private static long SumDirectoryBytes(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return 0;
        long sum = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(path));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo dir = stack.Pop();
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                foreach (FileInfo file in dir.GetFiles())
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    sum += file.Length;
                }
                foreach (DirectoryInfo sub in dir.GetDirectories())
                    stack.Push(sub);
            }
            catch (UnauthorizedAccessException ex) { CoreDiagnostics.Report("DriverDirectoryAccessDenied", error: ex); }
            catch (IOException ex) { CoreDiagnostics.Report("DriverDirectoryReadFailed", error: ex); }
        }
        return sum;
    }

    internal static void CreateStagingPayload(string stagingDir, GpuDriverIsoManifest manifest)
    {
        Directory.CreateDirectory(stagingDir);
        string payloadDir = Path.Combine(stagingDir, "_payload");
        Directory.CreateDirectory(payloadDir);

        using Stream resource = typeof(GpuDriverIsoService).Assembly.GetManifestResourceStream("ExHyperV.GpuGuestInstaller.zip")
            ?? throw new InvalidOperationException("缺少内嵌显卡驱动安装资源包：ExHyperV.GpuGuestInstaller.zip");
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        ExtractArchiveEntry(archive, "install.cmd", Path.Combine(stagingDir, "安装显卡驱动.cmd"));
        ExtractArchiveEntry(archive, "install.ps1", Path.Combine(payloadDir, "install.ps1"));
        File.WriteAllText(
            Path.Combine(payloadDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, DriverManifestJson.Default.GpuDriverIsoManifest),
            new UTF8Encoding(false));
    }

    private static void ExtractArchiveEntry(ZipArchive archive, string entryName, string targetPath)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"内嵌显卡驱动资源包缺少条目：{entryName}");
        using Stream input = entry.Open();
        using FileStream output = File.Create(targetPath);
        input.CopyTo(output);
    }

    internal static async Task<ApiResponse> MountIsoToVmAsync(Guid vmId, string isoPath)
    {
        var stateResult = await VmConfigurationState.ReadAsync(vmId).ConfigureAwait(false);
        if (!stateResult.HasData)
            return ApiResponse.Fail(stateResult.Error, stateResult.Code, stateResult.ErrorSource, stateResult.Exception, stateResult.ErrorKind);

        var itemsResult = await VmStorageService.GetVmStorageItemsAsync(vmId).ConfigureAwait(false);
        if (!itemsResult.HasData)
            return ApiResponse.Fail(itemsResult.Error, itemsResult.Code, itemsResult.ErrorSource, itemsResult.Exception, itemsResult.ErrorKind);

        var state = stateResult.Data!;
        List<VmStorageAttachment> items = itemsResult.Data!;

        // 优先复用现有的空闲 DVD 光驱
        VmStorageAttachment? emptyDvd = items.FirstOrDefault(item =>
            item.DriveType == "DvdDrive" &&
            !item.HasIsoMedia &&
            string.IsNullOrWhiteSpace(item.PathOrDiskNumber) &&
            VmStorageService.GetMediaModificationCondition(item, state.State).CanModify);

        if (emptyDvd != null)
        {
            var modified = await VmStorageService.ModifyDvdDrivePathAsync(vmId, emptyDvd, isoPath).ConfigureAwait(false);
            return modified.Success ? await VerifyMedia(emptyDvd.ControllerType, emptyDvd.ControllerNumber, emptyDvd.ControllerLocation, emptyDvd.DeviceToken) : modified;
        }

        VmStorageSlotPlan slotPlan = VmStorageService.GetBestSlot(state.Generation, state.IsRunning, "DvdDrive", items);
        if (slotPlan.Slot == null)
            return ApiResponse.Fail(slotPlan.Error ?? "没有可用的光驱槽位可挂载驱动光盘。", kind: ApiErrorKind.InvalidState);

        var addResult = await VmStorageService.AddDriveAsync(vmId, new VmStorageAddRequest
        {
            ControllerType = slotPlan.Slot.ControllerType,
            ControllerNumber = slotPlan.Slot.ControllerNumber,
            Location = slotPlan.Slot.Location,
            DriveType = "DvdDrive",
            PathOrNumber = isoPath
        }).ConfigureAwait(false);

        if (!addResult.HasData) return addResult.Success ? WmiJobWaiter.Unknown("未取得光驱槽位回执。") : ApiResponse.FromFailure(addResult);
        return await VerifyMedia(addResult.Data.ControllerType, addResult.Data.ControllerNumber, addResult.Data.Location, null);

        async Task<ApiResponse> VerifyMedia(string controller, int number, int location, string? deviceToken)
        {
            var actual = await VmStorageService.GetVmStorageItemsAsync(vmId).ConfigureAwait(false);
            if (!actual.HasData) return ApiResponse.Fail("无法核实驱动光盘挂载状态。" + actual.Error,
                actual.Code, actual.ErrorSource, actual.Exception, ApiErrorKind.OutcomeUnknown);
            var slot = actual.Data.Where(d => d.ControllerType == controller && d.ControllerNumber == number && d.ControllerLocation == location).ToArray();
            return slot.Length == 1 && (deviceToken == null || slot[0].DeviceToken == deviceToken) && slot[0].HasIsoMedia && string.Equals(slot[0].PathOrDiskNumber, isoPath, StringComparison.OrdinalIgnoreCase)
                ? ApiResponse.Ok() : ApiResponse.Fail("光驱回读与提交的介质不符。", kind: ApiErrorKind.Conflict);
        }
    }

    private static GpuDriverIsoRuleEntry ToEntry(GpuFilePromotionRule rule) => new(
        rule.SourceName,
        rule.TargetName,
        rule.TargetSubDir,
        rule.Mode.ToString(),
        rule.PreferredPackageNames,
        rule.RequirePreferredPackage);
}
