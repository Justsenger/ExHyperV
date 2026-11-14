using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Services;
using R = ExHyperV.Properties.Resources;

internal static class StorageWorkflowChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "ExHyperV-storage-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var previousCulture = R.Culture;
        try
        {
            string existing = Path.Combine(scratch, "existing.vhdx");
            File.WriteAllText(existing, "rule fixture, not a virtual disk");
            foreach (string culture in new[] { "zh-CN", "en-US" })
            {
                R.Culture = CultureInfo.GetCultureInfo(culture);
                void Verify(bool value, string name) => check(value, "Storage workflow: " + name + " (" + culture + ")");
                var empty = Array.Empty<VmStorageAttachment>();
                var allIde = Slots("IDE", 2, 2);
                var allScsi = Slots("SCSI", 4, 64);

                Verify(VmStorageService.GetControllerTypes(1, false, "HardDisk").SequenceEqual(["IDE", "SCSI"])
                    && VmStorageService.GetControllerTypes(1, true, "HardDisk").SequenceEqual(["SCSI"])
                    && VmStorageService.GetControllerTypes(1, true, "DvdDrive").SequenceEqual(["IDE"])
                    && VmStorageService.GetControllerTypes(2, false, "DvdDrive").SequenceEqual(["SCSI"]),
                    "generation, device kind and running state determine controller choices");
                Verify(VmStorageService.GetBestSlot(1, false, "HardDisk", empty).Slot == new VmStorageSlot("IDE", 0, 0)
                    && VmStorageService.GetBestSlot(1, false, "HardDisk", allIde).Slot == new VmStorageSlot("SCSI", 0, 0)
                    && VmStorageService.GetBestSlot(1, true, "HardDisk", empty).Slot == new VmStorageSlot("SCSI", 0, 0)
                    && VmStorageService.GetBestSlot(2, false, "HardDisk", empty).Slot == new VmStorageSlot("SCSI", 0, 0),
                    "automatic assignment retains IDE-first Gen1 and SCSI-only running/Gen2 behavior");
                Verify(VmStorageService.GetBestSlot(1, false, "DvdDrive", allIde).Error == R.Error_Storage_Gen1IdeFull
                    && VmStorageService.GetBestSlot(1, true, "DvdDrive", empty).Error == R.Error_Storage_Gen1Dvd
                    && VmStorageService.GetBestSlot(2, true, "HardDisk", allScsi).Error == R.Error_Storage_NoScsiRunning
                    && VmStorageService.GetBestSlot(2, false, "HardDisk", allScsi).Error == R.Error_Storage_NoScsi
                    && VmStorageService.GetBestSlot(1, false, "HardDisk", allIde.Concat(allScsi)).Error == R.Error_Storage_NoSlots,
                    "full-capacity and hot-add warnings retain their original distinctions");
                var occupied = new[] { Slot("SCSI", 0, 0), Slot("SCSI", 0, 2), Slot("IDE", 0, 1) };
                Verify(VmStorageService.GetBestSlot(2, false, "HardDisk", occupied).Slot == new VmStorageSlot("SCSI", 0, 1)
                    && VmStorageService.GetAvailableLocations("SCSI", 0, occupied).Count == 62
                    && VmStorageService.GetAvailableLocations("IDE", 0, occupied).SequenceEqual([0])
                    && VmStorageService.GetControllerNumbers("IDE").SequenceEqual([0, 1])
                    && VmStorageService.GetControllerNumbers("SCSI").SequenceEqual([0, 1, 2, 3]),
                    "holes are filled in controller/location order without mixing controller types");
                Verify(VmStorageService.GetControllerFullError("IDE", 1, allIde) == string.Format(R.Error_Storage_CtrlFull, "IDE", 1),
                    "manual controller exhaustion uses the shared localized warning");

                var physicalGen2 = VmStorageService.GetSourceChoice(2, true, "DvdDrive", true);
                var physicalGen1 = VmStorageService.GetSourceChoice(1, true, "DvdDrive", true);
                Verify(physicalGen2 == new VmStorageSourceChoice("HardDisk", false, false)
                    && physicalGen1 == new VmStorageSourceChoice("DvdDrive", false, true)
                    && VmStorageService.GetSourceChoice(2, false, "DvdDrive", true) == new VmStorageSourceChoice("DvdDrive", true, true),
                    "physical sources clear new-file mode and Gen2 physical optical selection returns to hard disks");
                Verify(VmStorageService.GetNewDiskSize("0") == 128 && VmStorageService.GetNewDiskSize("invalid") == 128
                    && VmStorageService.GetNewDiskSize("-1") == 128 && VmStorageService.GetNewDiskSize("64") == 64,
                    "disk size parsing retains the desktop's 128 GB fallback");

                var request = new VmStorageAddRequest { PathOrNumber = existing };
                Verify(VmStorageService.PrepareAddRequest(request, 2, false, empty).Success,
                    "existing virtual disks can be submitted through the Core preflight");
                Verify(VmStorageService.GetAddSlotError(2, false, request, empty) == null
                    && VmStorageService.GetAddSlotError(2, false, request, [Slot("SCSI", 0, 0)]) == R.Error_Storage_Occupied,
                    "a slot that became occupied after display is rejected by the submission rule");
                Verify(VmStorageService.PrepareAddRequest(request with { ControllerType = "IDE", PathOrNumber = "missing" },
                    1, true, [Slot("IDE", 0, 0)]).Error == R.Error_Storage_IdeHotAdd,
                    "hot-add rejection still precedes collision and source-path errors");
                Verify(VmStorageService.PrepareAddRequest(request with { PathOrNumber = "missing" },
                    2, false, [Slot("SCSI", 0, 0)]).Error == R.Error_Storage_Occupied,
                    "collision rejection still precedes file-path errors");
                Verify(VmStorageService.GetAddSlotError(2, false, request with { ControllerNumber = 4 }, empty) == R.Error_Storage_ControllerNotFound
                    && VmStorageService.GetAddSlotError(2, false, request with { Location = -1 }, empty) == R.Error_Storage_NoSlots
                    && VmStorageService.GetAddSlotError(2, false, request with { Location = 64 }, empty) == R.Error_Storage_NoSlots,
                    "invalid controller and slot coordinates cannot reach native mutation");
                Verify(VmStorageService.PrepareAddRequest(request with { PathOrNumber = " " }, 2, false, empty).Error == R.Error_Storage_SelectTarget
                    && VmStorageService.PrepareAddRequest(request with { PathOrNumber = Path.Combine(scratch, "absent") }, 2, false, empty).Error == R.Error_Storage_FileNotExist
                    && VmStorageService.PrepareAddRequest(request with { IsNew = true, SizeGiB = 1 }, 2, false, empty).Error == R.Error_Storage_FileExists,
                    "missing targets, missing existing files and new-file collisions retain the original messages");
                var emptyDvd = VmStorageService.PrepareAddRequest(request with { DriveType = "DvdDrive", PathOrNumber = " " }, 2, false, empty);
                Verify(emptyDvd.Success && emptyDvd.Request!.PathOrNumber == string.Empty,
                    "blank virtual DVD input means an empty drive, not a missing-target error");
                Verify(VmStorageService.PrepareAddRequest(request with { DriveType = "DvdDrive", IsPhysical = true, PathOrNumber = @"SCSI\CDROM&fixture" }, 1, false, empty).Error
                    == R.Error_Storage_ControllerNotFound,
                    "a Gen1 optical request must select the IDE controller");
                Verify(VmStorageService.PrepareAddRequest(request with { ControllerType = "IDE", DriveType = "DvdDrive", IsPhysical = true,
                    PathOrNumber = @"SCSI\CDROM&fixture" }, 1, false, empty).Success
                    && VmStorageService.PrepareAddRequest(request with { IsPhysical = true, PathOrNumber = "42", PhysicalDisk = new() { Number = 42, SizeBytes = 1024, UniqueId = "fixture-disk" } }, 2, false, empty).Success,
                    "physical device identities are not treated as file paths");

                var differencing = request with { IsNew = true, VhdType = "Differencing", PathOrNumber = Path.Combine(scratch, "new.vhdx") };
                Verify(!VmStorageService.PrepareAddRequest(differencing, 2, false, empty).Success
                    && VmStorageService.PrepareAddRequest(differencing with { ParentPath = existing }, 2, false, empty).Success
                    && !File.Exists(differencing.PathOrNumber),
                    "differencing requests require an existing parent and preflight never creates the disk");

                var iso = request with { DriveType = "DvdDrive", IsNew = true, PathOrNumber = null };
                Verify(VmStorageService.PrepareAddRequest(iso, 2, false, empty).Error == R.Error_Storage_IsoSource
                    && VmStorageService.PrepareAddRequest(iso with { IsoSourcePath = scratch }, 2, false, empty).Error == R.Error_Storage_IsoPath,
                    "new ISO source/output completeness is checked in the original order");
                string outputDirectory = Path.Combine(scratch, "iso-" + culture);
                var isoReady = VmStorageService.PrepareAddRequest(iso with { IsoSourcePath = scratch,
                    IsoOutputPath = Path.Combine(outputDirectory, "fixture.iso") }, 2, false, empty);
                Verify(isoReady.Success && Directory.Exists(outputDirectory)
                    && isoReady.Request!.PathOrNumber == Path.Combine(outputDirectory, "fixture.iso")
                    && !File.Exists(isoReady.Request.PathOrNumber),
                    "ISO preparation creates only the requested output directory and returns the effective output path");
                var isoAbsentSource = VmStorageService.PrepareAddRequest(iso with { IsoSourcePath = Path.Combine(scratch, "absent-source"),
                    IsoOutputPath = Path.Combine(outputDirectory, "missing.iso") }, 2, false, empty);
                Verify(isoAbsentSource.Error == R.Error_Storage_SourceNoExist,
                    "nonexistent ISO source retains the shared source-directory error");
                var badOutput = VmStorageService.PrepareAddRequest(iso with { IsoSourcePath = scratch,
                    IsoOutputPath = Path.Combine(existing, "invalid", "fixture.iso") }, 2, false, empty);
                Verify(!badOutput.Success && badOutput.IsError && badOutput.Error!.StartsWith(R.Error_Storage_DirFail.Split('{')[0]),
                    "output directory failure retains the error presentation rather than a picker-completeness tip");

                var hardDisk = Slot("IDE", 0, 0);
                hardDisk.DriveType = "HardDisk";
                Verify(VmStorageService.GetMediaModificationCondition(hardDisk, ExHyperV.Models.VmState.Running).RequiredState == ExHyperV.Models.VmState.PoweredOff
                    && VmStorageService.GetEditStoragePathError(hardDisk, false) == null,
                    "IDE virtual disk media changes require a non-running VM");
                hardDisk.ControllerType = "SCSI";
                Verify(VmStorageService.GetEditStoragePathError(hardDisk, true) == null,
                    "SCSI disk media edits retain their existing running-state availability");
                hardDisk.DiskType = "Physical";
                Verify(!VmStorageService.CanEditStoragePath(hardDisk)
                    && VmStorageService.GetEditStoragePathError(hardDisk, false) == R.Error_Storage_PhysicalMod,
                    "physical storage cannot be edited as a virtual image path");
                Verify(VmStorageService.GetMediaPathError(existing) == null && VmStorageService.GetMediaPathError("") == null
                    && VmStorageService.GetMediaPathError(Path.Combine(scratch, "missing.iso")) == R.Error_Storage_FileNotExist,
                    "media validation permits ejection while rejecting missing nonempty image paths");

                var nativeAddGuard = await VmStorageService.AddDriveAsync(Guid.NewGuid(), new VmStorageAddRequest { PathOrNumber = Path.Combine(scratch, "missing.vhdx") });
                var nativeEditGuard = await VmStorageService.ModifyDvdDrivePathAsync(Guid.NewGuid(), Slot("SCSI", 0, 0),
                    Path.Combine(scratch, "missing.iso"));
                Verify(!nativeAddGuard.Success && nativeAddGuard.Error == R.Error_Storage_FileNotExist
                    && !nativeEditGuard.Success && nativeEditGuard.ErrorKind == ApiErrorKind.InvalidArgument,
                    "public mutation APIs reject missing files and absent selection identities before any Hyper-V query");
            }
        }
        finally
        {
            R.Culture = previousCulture;
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static VmStorageAttachment Slot(string type, int number, int location) => new()
    { ControllerType = type, ControllerNumber = number, ControllerLocation = location };

    private static VmStorageAttachment[] Slots(string type, int count, int locations)
        => Enumerable.Range(0, count).SelectMany(number => Enumerable.Range(0, locations).Select(location => Slot(type, number, location))).ToArray();
}
