using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;
using Microsoft.Win32;
using R = ExHyperV.Properties.Resources;

namespace ExHyperV.Services;

internal static class LinuxGpuPvRuntimeService
{
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly SecurityIdentifier HyperVVirtualMachinesSid = new("S-1-5-83-0");
    private static readonly object IsoAclLock = new();
    private static readonly Regex ElfSoRegex = new(@"\.so(?:\.\d+)*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RegValueFilterRegex = new(@"WSL|OpenCLDriverName|LevelZeroDriverPath|DriverStorePathForComputeRuntime", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CopyInfNameRegex = new(@"^[\w.\\-]+\.inf$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InfArchSectionRegex = new(@"(?:^|\.)(?:NT)?(amd64|arm64|x86|arm)(?:\.|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DestDirsLxssRegex = new(@"^lxss\\lib$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SafeDriverFilenameRegex = new(@"^[A-Za-z0-9_+.-]+$", RegexOptions.Compiled);

    internal sealed record GpuLinuxRuntime(
        string PrimaryPackageName,
        IReadOnlyDictionary<string, string> Packages,
        IReadOnlyList<string> LinuxFiles);

    internal static GpuLinuxRuntime ResolveHostGpuRuntime(HostGpuInfo card, string arch)
    {
        GpuDriverPackagePlan plan = GpuDriverPackageResolver.Resolve(card.InstanceId, card.Manu);
        string storeRoot = Path.GetFullPath(plan.HostFileRepository).TrimEnd(Path.DirectorySeparatorChar);
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void AddPackageDir(string dirPath)
        {
            string full = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar);
            var info = new DirectoryInfo(full);
            if (!info.Exists) return;
            if (!string.Equals(info.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar), storeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(string.Format(R.GpuDriverPackage_OutsideFileRepository, full));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(string.Format(R.Error_Gpu_LinuxPackageReparse, full));
            packages[info.Name] = info.FullName;
        }

        AddPackageDir(Path.Combine(storeRoot, plan.PrimaryPackageName));
        foreach (string pkgName in plan.PackageNames)
            AddPackageDir(Path.Combine(storeRoot, pkgName));

        // 1. 宿主显示类注册表中声明的 WSL / Compute / OpenCL Linux 运行库
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? adapterKey = baseKey.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{DisplayClassGuid}\{plan.DisplayClassSubKeyName}");
        if (adapterKey != null)
        {
            foreach (string valueName in adapterKey.GetValueNames())
            {
                if (!RegValueFilterRegex.IsMatch(valueName)) continue;
                object? rawVal = adapterKey.GetValue(valueName);
                IEnumerable<string> paths = rawVal switch
                {
                    string single => [single],
                    string[] multi => multi,
                    _ => []
                };
                foreach (string entry in paths)
                {
                    if (string.IsNullOrWhiteSpace(entry) || !ElfSoRegex.IsMatch(entry) || !File.Exists(entry)) continue;
                    string actual = Path.GetFullPath(entry);
                    if (!actual.StartsWith(storeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(string.Format(R.Error_Gpu_LinuxOutsideStore, actual));
                    string relative = actual[(storeRoot.Length + 1)..];
                    string pkgName = relative.Split(Path.DirectorySeparatorChar)[0];
                    AddPackageDir(Path.Combine(storeRoot, pkgName));
                }
            }
        }

        // 2. SoftwareComponent 子设备（Intel 等厂商常将 WSL UMD 置于独立组件驱动包中）
        foreach (string childId in Win32Api.GetDeviceChildren(card.InstanceId))
        {
            string compInfName = Win32Api.GetPresentSoftwareComponentInfPath(childId);
            if (string.IsNullOrWhiteSpace(compInfName)) continue;
            string compInfPath = Win32Api.GetInfDriverStoreLocation(compInfName);
            DirectoryInfo? compDir = new FileInfo(compInfPath).Directory;
            if (compDir == null || !compDir.Exists) continue;
            if (!string.Equals(compDir.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar), storeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(string.Format(R.GpuDriverPackage_OutsideFileRepository, compInfPath));
            if (EnumerateElfSoFiles(compDir.FullName).Count > 0)
                AddPackageDir(compDir.FullName);
        }

        // 3. 主驱动 INF CopyINF 引用的伴随包（含 NVIDIA/AMD 独立计算包）
        string primaryDirPath = Path.Combine(storeRoot, plan.PrimaryPackageName);
        foreach (string primaryInf in Directory.EnumerateFiles(primaryDirPath, "*.inf", SearchOption.TopDirectoryOnly))
        {
            foreach (string refEntry in ReadCopyInfReferences(primaryInf))
            {
                string leaf = Path.GetFileName(refEntry);
                string embeddedPath = Path.Combine(primaryDirPath, refEntry);
                string? embeddedHash = File.Exists(embeddedPath) ? ComputeSha256Hex(embeddedPath) : null;

                var candidates = new List<DirectoryInfo>();
                foreach (string dir in Directory.EnumerateDirectories(storeRoot, leaf + "_*", SearchOption.TopDirectoryOnly))
                {
                    string infInCandidate = Path.Combine(dir, leaf);
                    if (!File.Exists(infInCandidate)) continue;
                    if (embeddedHash != null && !string.Equals(ComputeSha256Hex(infInCandidate), embeddedHash, StringComparison.OrdinalIgnoreCase))
                        continue;
                    candidates.Add(new DirectoryInfo(dir));
                }

                var withElf = candidates.Where(c => EnumerateElfSoFiles(c.FullName).Count > 0).ToList();
                if (withElf.Count > 1)
                    throw new InvalidOperationException(string.Format(R.Error_Gpu_LinuxAmbiguousCompanion, refEntry));
                if (withElf.Count == 1)
                    AddPackageDir(withElf[0].FullName);
            }
        }

        var allLinuxFiles = packages.Values
            .SelectMany(EnumerateElfSoFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allLinuxFiles.Length == 0)
            throw new InvalidOperationException(string.Format(R.Error_Gpu_LinuxNoHostUmd, card.Name));

        ushort expectedMachine = LinuxGpuPvBundleService.GetExpectedElfMachine(arch);
        foreach (string soFile in allLinuxFiles)
            LinuxGpuPvBundleService.AssertValidElf64File(soFile, expectedMachine, [3], Path.GetFileName(soFile));

        return new GpuLinuxRuntime(plan.PrimaryPackageName, packages, allLinuxFiles);
    }

    internal static IReadOnlyDictionary<string, string> ReadWslFileMap(FileInfo infFile, string arch)
    {
        string targetDecoration = arch == "aarch64" ? "arm64" : "amd64";
        bool IsApplicableSection(string sectionName)
        {
            var m = InfArchSectionRegex.Match(sectionName);
            return !m.Success || string.Equals(m.Groups[1].Value, targetDecoration, StringComparison.OrdinalIgnoreCase);
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = new DriverInfFile(infFile);
        foreach (string sectionName in document.ReadSectionNames())
        {
            if (!sectionName.StartsWith("DestinationDirs", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsApplicableSection(sectionName)) continue;

            foreach (var (copySectionName, fields) in document.ReadKeyValueSection(sectionName))
            {
                if (string.IsNullOrWhiteSpace(copySectionName) || fields.Length < 2) continue;
                if (!string.Equals(fields[0].Trim(), "11", StringComparison.Ordinal)) continue;
                if (!DestDirsLxssRegex.IsMatch(fields[1].Trim())) continue;
                if (!IsApplicableSection(copySectionName)) continue;

                foreach (string[] copyRow in document.ReadListSection(copySectionName))
                {
                    if (copyRow.Length == 0) continue;
                    string dst = copyRow[0].Trim();
                    string src = copyRow.Length > 1 && !string.IsNullOrWhiteSpace(copyRow[1])
                        ? copyRow[1].Trim()
                        : dst;
                    if (!SafeDriverFilenameRegex.IsMatch(dst) || !SafeDriverFilenameRegex.IsMatch(src)
                        || dst is "." or ".." || src is "." or "..")
                        throw new InvalidOperationException(string.Format(R.Error_Gpu_LinuxInvalidInfEntry, infFile.Name, string.Join(',', copyRow)));

                    if (aliases.TryGetValue(dst, out string? existingSrc) && !string.Equals(existingSrc, src, StringComparison.Ordinal))
                        throw new InvalidOperationException(string.Format(R.Error_Gpu_LinuxAliasConflict, dst));
                    aliases[dst] = src;
                }
            }
        }
        return aliases;
    }

    internal static async Task<string> SynthesizeIsoAsync(
        Guid vmId,
        IReadOnlyList<HostGpuInfo> selectedGpus,
        string bundleZipPath,
        Action<int, string>? progress,
        CancellationToken cancellationToken)
    {
        string arch = LinuxGpuPvBundleService.GetTargetArchitecture();
        string stageRoot = Path.Combine(Path.GetTempPath(), $"exhyperv-gpupv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageRoot);

        try
        {
            string payloadDir = Path.Combine(stageRoot, "payload");
            progress?.Invoke(0, string.Format(R.Msg_Gpu_VerifyingBundlePct, 0));
            await LinuxGpuPvBundleService.ExtractAndVerifyAsync(
                bundleZipPath,
                payloadDir,
                arch,
                pct =>
                {
                    int mapped = pct * 45 / 100;
                    string phaseMsg = pct < 50
                        ? string.Format(R.Msg_Gpu_VerifyingBundlePct, pct * 2)
                        : string.Format(R.Msg_Gpu_SynthesizingIsoPct, mapped);
                    progress?.Invoke(mapped, phaseMsg);
                },
                cancellationToken).ConfigureAwait(false);

            string libDir = Path.Combine(payloadDir, "lib");
            string driversDir = Path.Combine(payloadDir, "drivers");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(driversDir);

            ushort expectedMachine = LinuxGpuPvBundleService.GetExpectedElfMachine(arch);
            var adapterManifests = new List<LinuxIsoAdapterEntry>();

            await Task.Run(() =>
            {
                for (int gpuIdx = 0; gpuIdx < selectedGpus.Count; gpuIdx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HostGpuInfo card = selectedGpus[gpuIdx];
                    progress?.Invoke(45 + gpuIdx * 10 / Math.Max(1, selectedGpus.Count),
                        string.Format(R.Msg_Gpu_SynthesizingIsoPct, 45 + gpuIdx * 10 / Math.Max(1, selectedGpus.Count)));

                    GpuLinuxRuntime runtime = ResolveHostGpuRuntime(card, arch);
                    var claimed = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var (pkgName, pkgDir) in runtime.Packages)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string destPkgDir = Path.Combine(driversDir, pkgName);
                        if (!Directory.Exists(destPkgDir))
                            CopyDirectorySkipReparse(pkgDir, destPkgDir, cancellationToken);

                        foreach (string infPath in Directory.EnumerateFiles(pkgDir, "*.inf", SearchOption.TopDirectoryOnly))
                        {
                            var aliases = ReadWslFileMap(new FileInfo(infPath), arch);
                            foreach (var (dstName, srcName) in aliases)
                            {
                                string sourcePath = Path.Combine(pkgDir, srcName);
                                if (!File.Exists(sourcePath))
                                    throw new FileNotFoundException(string.Format(R.Error_Gpu_LinuxMissingMappedFile, srcName, pkgName), sourcePath);
                                if (ElfSoRegex.IsMatch(dstName))
                                    LinuxGpuPvBundleService.AssertValidElf64File(sourcePath, expectedMachine, [3], dstName);
                                claimed.Add(dstName);
                                AddDriverFileStrict(sourcePath, libDir, dstName);
                            }
                        }
                    }

                    foreach (string soFile in runtime.LinuxFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string leaf = Path.GetFileName(soFile);
                        if (!claimed.Contains(leaf))
                            AddDriverFileStrict(soFile, libDir, leaf);
                    }

                    adapterManifests.Add(new LinuxIsoAdapterEntry
                    {
                        Name = card.Name,
                        Device = card.InstanceId,
                        Version = card.DriverVersion,
                        Package = runtime.PrimaryPackageName
                    });
                }

                progress?.Invoke(72, string.Format(R.Msg_Gpu_SynthesizingIsoPct, 72));
                var deviceManifest = new LinuxIsoDeviceManifest
                {
                    SchemaVersion = 1,
                    Arch = arch,
                    Adapters = adapterManifests
                };
                string deviceJson = JsonSerializer.Serialize(deviceManifest, CoreJson.Default.LinuxIsoDeviceManifest);
                File.WriteAllText(Path.Combine(payloadDir, "device.json"), deviceJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                string setupPath = Path.Combine(payloadDir, "setup");
                if (!File.Exists(setupPath))
                {
                    File.WriteAllText(
                        setupPath,
                        "#!/bin/sh\nexec sh \"$(dirname \"$0\")/guest/setup\" \"$@\"\n",
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }, cancellationToken).ConfigureAwait(false);

            string targetIsoPath = await ResolveIsoDestinationPathAsync(vmId).ConfigureAwait(false);
            string targetDir = Path.GetDirectoryName(targetIsoPath)!;
            Directory.CreateDirectory(targetDir);
            string partIsoPath = Path.Combine(targetDir, $"{Path.GetFileNameWithoutExtension(targetIsoPath)}.{Guid.NewGuid():N}.part");

            try
            {
                await Task.Run(() =>
                {
                    IsoBuilderService.BuildUdfIso(
                        [new IsoDirectorySource(payloadDir, string.Empty)],
                        partIsoPath,
                        LinuxGpuPvBundleService.IsoVolumeLabel,
                        pct => progress?.Invoke(75 + pct * 25 / 100, string.Format(R.Msg_Gpu_SynthesizingIsoPct, 75 + pct * 25 / 100)),
                        forceUdfOnly: true,
                        cancellationToken);
                }, cancellationToken).ConfigureAwait(false);

                File.Move(partIsoPath, targetIsoPath, overwrite: true);
                EnsureHyperVReadAccess(targetIsoPath);
                progress?.Invoke(100, string.Format(R.Msg_Gpu_SynthesizingIsoPct, 100));
                return targetIsoPath;
            }
            catch
            {
                try { if (File.Exists(partIsoPath)) File.Delete(partIsoPath); } catch { }
                throw;
            }
        }
        finally
        {
            try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); } catch { }
        }
    }

    internal static async Task MountIsoToVmAsync(Guid vmId, string isoPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
            throw new FileNotFoundException(R.Error_Storage_FileNotExist, isoPath);

        string fullIsoPath = Path.GetFullPath(isoPath);
        EnsureHyperVReadAccess(fullIsoPath);

        var itemsResponse = await VmStorageService.GetVmStorageItemsAsync(vmId).ConfigureAwait(false);
        if (!itemsResponse.HasData)
            throw new InvalidOperationException(itemsResponse.Error, itemsResponse.Exception);

        var items = itemsResponse.Data!;
        var virtualDvds = items
            .Where(item => item.DriveType == "DvdDrive" && item.DiskType == "Virtual")
            .ToList();

        // 1. 若已有搭载 GPUPV ISO 的虚拟光驱，直接替换或确认路径。
        var existingGpupvDvd = virtualDvds.FirstOrDefault(d =>
            !string.IsNullOrWhiteSpace(d.PathOrDiskNumber)
            && string.Equals(Path.GetFileName(d.PathOrDiskNumber), Path.GetFileName(fullIsoPath), StringComparison.OrdinalIgnoreCase));
        if (existingGpupvDvd != null)
        {
            if (string.Equals(Path.GetFullPath(existingGpupvDvd.PathOrDiskNumber), fullIsoPath, StringComparison.OrdinalIgnoreCase))
                return;
            var swapExisting = await VmStorageService.ModifyDvdDrivePathAsync(vmId, existingGpupvDvd, fullIsoPath).ConfigureAwait(false);
            if (!swapExisting.Success)
                throw new InvalidOperationException(swapExisting.Error, swapExisting.Exception);
            return;
        }

        // 2. 优先复用空的虚拟光驱槽位，避免占用原有系统安装镜像光驱。
        var emptyDvd = virtualDvds.FirstOrDefault(d => string.IsNullOrWhiteSpace(d.PathOrDiskNumber));
        if (emptyDvd != null)
        {
            var insertResult = await VmStorageService.ModifyDvdDrivePathAsync(vmId, emptyDvd, fullIsoPath).ConfigureAwait(false);
            if (!insertResult.Success)
                throw new InvalidOperationException(insertResult.Error, insertResult.Exception);
            return;
        }

        // 3. 其次尝试新挂载一个虚拟光驱。
        var configState = await VmConfigurationState.ReadAsync(vmId).ConfigureAwait(false);
        if (!configState.HasData)
            throw new InvalidOperationException(configState.Error, configState.Exception);

        var slotPlan = VmStorageService.GetBestSlot(
            configState.Data!.Generation,
            !configState.Data.IsPoweredOff,
            "DvdDrive",
            items);
        if (slotPlan.Slot != null)
        {
            var addResult = await VmStorageService.AddDriveAsync(vmId, new VmStorageAddRequest
            {
                ControllerType = slotPlan.Slot.ControllerType,
                ControllerNumber = slotPlan.Slot.ControllerNumber,
                Location = slotPlan.Slot.Location,
                DriveType = "DvdDrive",
                PathOrNumber = fullIsoPath
            }).ConfigureAwait(false);
            if (!addResult.Success)
                throw new InvalidOperationException(addResult.Error, addResult.Exception);
            return;
        }

        // 4. 若无空闲槽位但存在现有虚拟光驱，回退替换首个虚拟光驱介质。
        if (virtualDvds.Count > 0)
        {
            var replaceResult = await VmStorageService.ModifyDvdDrivePathAsync(vmId, virtualDvds[0], fullIsoPath).ConfigureAwait(false);
            if (!replaceResult.Success)
                throw new InvalidOperationException(replaceResult.Error, replaceResult.Exception);
            return;
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(slotPlan.Error) ? R.Error_Storage_NoSlots : slotPlan.Error);
    }

    private static async Task<string> ResolveIsoDestinationPathAsync(Guid vmId)
    {
        var configRoot = await WmiApi.QueryFirstAsync(
            $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => obj["ConfigurationDataRoot"]?.ToString() ?? string.Empty).ConfigureAwait(false);

        if (configRoot.HasData && !string.IsNullOrWhiteSpace(configRoot.Data) && Directory.Exists(configRoot.Data))
            return Path.Combine(configRoot.Data, "exhyperv-gpupv.iso");

        string commonFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ExHyperV",
            "gpupv-iso");
        return Path.Combine(commonFolder, $"{vmId:D}.iso");
    }

    private static void EnsureHyperVReadAccess(string filePath)
    {
        try
        {
            lock (IsoAclLock)
            {
                var file = new FileInfo(filePath);
                if (!file.Exists) return;
                FileSecurity security = file.GetAccessControl(AccessControlSections.Access);
                bool hasRule = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>()
                    .Any(r => r.AccessControlType == AccessControlType.Allow
                        && HyperVVirtualMachinesSid.Equals(r.IdentityReference)
                        && (r.FileSystemRights & FileSystemRights.ReadData) != 0);
                if (!hasRule)
                {
                    security.AddAccessRule(new FileSystemAccessRule(
                        HyperVVirtualMachinesSid,
                        FileSystemRights.Read,
                        AccessControlType.Allow));
                    file.SetAccessControl(security);
                }
            }
        }
        catch (Exception ex)
        {
            CoreDiagnostics.Report("LinuxGpuPvIsoAclWarning", error: ex);
        }
    }

    private static void AddDriverFileStrict(string sourcePath, string destinationDirectory, string fileName)
    {
        if (!SafeDriverFilenameRegex.IsMatch(fileName) || fileName is "." or "..")
            throw new InvalidOperationException(string.Format(R.Error_Gpu_LinuxAliasConflict, fileName));

        string dst = Path.Combine(destinationDirectory, fileName);
        if (File.Exists(dst))
        {
            if (!string.Equals(ComputeSha256Hex(dst), ComputeSha256Hex(sourcePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(string.Format(R.Error_Gpu_LinuxAliasConflict, fileName));
            return;
        }
        File.Copy(sourcePath, dst);
    }

    private static IReadOnlyList<string> EnumerateElfSoFiles(string packageDirectory)
    {
        var list = new List<string>();
        foreach (string path in Directory.EnumerateFiles(packageDirectory, "*.so*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (ElfSoRegex.IsMatch(Path.GetFileName(path)))
                list.Add(path);
        }
        return list;
    }

    private static IReadOnlyList<string> ReadCopyInfReferences(string infPath)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var document = new DriverInfFile(new FileInfo(infPath));
        foreach (string[] fields in document.ReadAllFields("CopyINF"))
        {
            foreach (string raw in fields)
            {
                string cleaned = raw.Trim().Replace('/', '\\');
                if (cleaned.Length == 0 || Path.IsPathRooted(cleaned)
                    || cleaned.Split('\\').Contains("..", StringComparer.Ordinal))
                    continue;
                if (CopyInfNameRegex.IsMatch(cleaned))
                    refs.Add(cleaned);
            }
        }
        return refs.ToArray();
    }

    private static void CopyDirectorySkipReparse(string sourceDir, string targetDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string subDir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectorySkipReparse(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)), cancellationToken);
        }
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
