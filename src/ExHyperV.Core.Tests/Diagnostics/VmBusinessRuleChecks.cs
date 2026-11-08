using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using R = ExHyperV.Properties.Resources;

internal static class VmBusinessRuleChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var originalCulture = R.Culture;
        string scratch = Path.Combine(Path.GetTempPath(), "ExHyperV-input-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string disk = Path.Combine(scratch, "existing.vhdx");
        string iso = Path.Combine(scratch, "install.iso");
        string firmware = Path.Combine(scratch, "firmware.igvm");
        foreach (string file in new[] { disk, iso, firmware }) File.WriteAllText(file, "rule fixture");
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN" })
            {
                R.Culture = CultureInfo.GetCultureInfo(culture);
                void Verify(bool result, string name) => check(result, "VM business rules: " + name + " (" + culture + ")");
                VmCreationParameters Valid() => new()
                {
                    Name = "validation",
                    Path = Path.Combine(scratch, "uncreated"),
                    ProcessorCount = 1,
                    MemoryMiB = 32,
                    DiskMode = 0,
                    DiskSizeGiB = 1,
                    VhdPath = string.Empty
                };

                var p = Valid();
                Verify(VmCreateService.GetCreationError(p) == null,
                    "minimum valid values retain the optional new-disk directory");
                Verify(p.GetNewDiskDirectory("actual-name") == Path.Combine(p.Path, "actual-name"),
                    "omitted new-disk directory retains the existing VM-directory fallback");
                p.StoreNewDisksWithVm = true;
                Verify(VmCreateService.GetCreationError(p) == null &&
                    p.GetNewDiskDirectory("actual-name") == Path.Combine(p.Path, "actual-name", "Virtual Hard Disks"),
                    "VM-local new disks keep their explicit placement option");

                p = Valid(); p.Name = " "; p.IsoPath = "missing.iso"; p.ProcessorCount = 0;
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_NameEmpty,
                    "name remains the first error when several fields are invalid");
                var invalidCreate = await VmCreateService.CreateVirtualMachineAsync(p);
                Verify(!invalidCreate.Success && invalidCreate.Error == R.VmPage_NameEmpty && !Directory.Exists(p.Path),
                    "create rejects invalid input before filesystem or Hyper-V work");
                p = Valid(); p.DiskMode = 1;
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_SelectExistVhdPath, "existing disk still requires a path");
                p.VhdPath = Path.Combine(scratch, "missing.vhdx");
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_ExistVhdNotFound, "missing existing disk is rejected");
                p.VhdPath = disk;
                Verify(VmCreateService.GetCreationError(p) == null, "existing disk presence is accepted");
                p.IsoPath = Path.Combine(scratch, "missing.iso");
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_IsoNotFound, "nonempty missing ISO is never silently omitted");
                p.IsoPath = iso;
                Verify(VmCreateService.GetCreationError(p) == null, "optional ISO retains its existing presence rule");

                p = Valid(); p.IsolationType = "OpenHCL";
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_OpenHclIgvmRequired, "OpenHCL firmware remains required");
                p.OpenHclIgvmPath = firmware; p.Version = "11.0";
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_OpenHclRequiresV12, "OpenHCL retains its version threshold");
                p.Version = "12.0";
                Verify(VmCreateService.GetCreationError(p) == null, "OpenHCL version boundary is accepted without changing firmware permissions");

                p = Valid(); p.ProcessorCount = 0;
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_InvalidCpuCount, "zero or failed CPU parse is rejected");
                p.ProcessorCount = 1; p.MemoryMiB = 31;
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_InvalidMemory, "memory below 32 MB is rejected");
                p.MemoryMiB = 32; p.DiskSizeGiB = 0;
                Verify(VmCreateService.GetCreationError(p) == R.VmPage_InvalidDiskSize, "new disks below 1 GB are rejected");
                p.DiskMode = 2;
                Verify(VmCreateService.GetCreationError(p) == null, "no-disk mode ignores new-disk size and directory");

                Verify(VmEditService.GetNameError("  actual name  ") == null, "valid names are not trimmed or otherwise normalized");
                var rename = await VmEditService.RenameVmAsync(Guid.Empty, "\t");
                Verify(!rename.Success && rename.Error == R.VmPage_NameEmpty, "rename rejects an empty name before WMI lookup");

                var clear = new VmImportPreview { Name = "ready" };
                var blocked = new VmImportPreview { Name = "blocked" };
                blocked.AddIssue("target conflict");
                Verify(VmImportService.GetPreviewError([]) != null, "an empty import batch cannot start");
                Verify(VmImportService.GetPreviewError([clear]) == null, "an issue-free import preview can start");
                Verify(VmImportService.GetPreviewError([clear, blocked]) == "target conflict", "one blocked VM prevents a batch from starting");
                VmImportSession session = new()
                {
                    SourcePath = Path.Combine(scratch, "missing.zip"),
                    SourceKind = VmImportSourceKind.Zip,
                    PlacementMode = VmImportPlacementMode.HostDirectories,
                    Preview = blocked
                };
                var progress = new ImmediateProgress();
                var import = await VmImportService.ImportAsync(session, new VmImportOptions(VmImportGuidMode.GenerateNew), progress);
                Verify(!import.Success && import.Error == "target conflict" && progress.Reports == 0 &&
                    !session.ArchiveFullyExtracted && session.PlannedSystemPath.Length == 0,
                    "single import rejects preview issues before extraction, progress or native preparation");
                var batch = new VmImportBatchSession
                {
                    SourcePath = session.SourcePath,
                    SourceKind = session.SourceKind,
                    PlacementMode = session.PlacementMode,
                    VirtualMachines = [session]
                };
                var prepare = await VmImportService.PrepareBatchImportAsync(batch);
                Verify(!prepare.Success && prepare.Error == "target conflict", "batch preparation checks all previews before querying host switches");
                blocked = new VmImportPreview { Name = "refreshed" };
                Verify(VmImportService.GetPreviewError([clear, blocked]) == null, "a refreshed issue-free preview is accepted");
                blocked.AddIssue(string.Empty);
                Verify(VmImportService.GetPreviewError([blocked]) != null, "issue count remains blocking even when an issue has empty text");
            }
        }
        finally
        {
            R.Culture = originalCulture;
            foreach (string file in new[] { disk, iso, firmware }) File.Delete(file);
            Directory.Delete(scratch);
        }
    }

    private sealed class ImmediateProgress : IProgress<int>
    {
        public int Reports { get; private set; }
        public void Report(int value) => Reports++;
    }
}
