using ExHyperV.Services;

internal static class DiskSettingsReaderChecks
{
    public static void CheckOwnershipAndRecovery(Action<bool, string> check)
    {
        int created = 0;
        int reads = 0;
        var owners = new List<ServiceOwner>();
        ServiceOwner Create()
        {
            created++;
            var owner = new ServiceOwner();
            owners.Add(owner);
            return owner;
        }

        using (var unused = new VmQueryService.DiskSettingsReader(Create, (_, _) => (true, 100, "Fixed"))) { }
        check(created == 0, "a disk-reader scope with no cache misses never opens the image service");

        var reader = new VmQueryService.DiskSettingsReader(Create, (owner, path) =>
        {
            reads++;
            check(ReferenceEquals(owner, owners[^1]), "a disk read uses the scope's current service owner");
            return path == "failed" ? (false, 0, "Fixed") : (true, 100, "Dynamic");
        });
        check(reader.Read("first") == (100, "Dynamic") && reader.Read("second") == (100, "Dynamic") &&
            created == 1 && reads == 2 && owners[0].DisposeCount == 0,
            "successful disk reads reuse one service without releasing it between disks");
        check(reader.Read("failed") == (0, "Fixed") && created == 1 && reads == 3 && owners[0].DisposeCount == 1,
            "a failed read preserves partial metadata and releases its service without retrying that disk");
        check(reader.Read("next") == (100, "Dynamic") && created == 2 && reads == 4,
            "the next disk rebuilds the service after a failed read");
        reader.Dispose();
        reader.Dispose();
        check(owners.All(owner => owner.DisposeCount == 1), "scope disposal releases each owned service exactly once");
        bool disposedReadRejected = false;
        try { reader.Read("after close"); }
        catch (ObjectDisposedException) { disposedReadRejected = true; }
        check(disposedReadRejected && created == 2, "a closed reader cannot reopen or use a service");

        using (var nextScope = new VmQueryService.DiskSettingsReader(Create, (_, _) => (true, 200, "Fixed")))
            check(nextScope.Read("new scope") == (200, "Fixed") && created == 3,
                "a later VM-list scope acquires its own service instead of reusing a disposed owner");
        check(owners[2].DisposeCount == 1, "a later scope releases its own service");

        int attempts = 0;
        int errorReads = 0;
        var broken = new ServiceOwner { ThrowOnDispose = true };
        var recovered = new ServiceOwner();
        using (var failures = new VmQueryService.DiskSettingsReader(() =>
        {
            attempts++;
            return attempts switch
            {
                1 => throw new IOException("expected service acquisition failure"),
                2 => null,
                3 => broken,
                _ => recovered
            };
        }, (owner, _) =>
        {
            errorReads++;
            if (ReferenceEquals(owner, broken)) throw new IOException("expected metadata failure");
            return (true, 300, "Differencing");
        }))
        {
            check(failures.Read("factory throws") == (0, "Unknown") &&
                failures.Read("no service") == (0, "Unknown") && attempts == 2 && errorReads == 0,
                "failed or empty service discovery preserves fallback and is retried only on the next read");
            check(failures.Read("read throws") == (0, "Unknown") && broken.DisposeCount == 1,
                "a thrown metadata read releases the owner even when its cleanup also throws");
            check(failures.Read("recovered") == (300, "Differencing") && attempts == 4 && errorReads == 2,
                "cleanup failure does not retain a broken service or prevent the next disk from recovering");
        }
        check(broken.DisposeCount == 1 && recovered.DisposeCount == 1,
            "failed cleanup is not repeated and the final successful service is disposed");
    }

    public static void CheckMissingFiles(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExHyperV-DiskReader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "disk.bin");
        string absentDirectoryPath = Path.Combine(directory, "absent", "disk.bin");
        int queries = 0;
        (long Max, string Type) Read(string _) { queries++; return (100, "Fixed"); }
        try
        {
            check(VmQueryService.GetDiskSizes(path, Read) == (0, 0, "Unknown") &&
                VmQueryService.GetDiskSizes(path, Read) == (0, 0, "Unknown") && queries == 0,
                "an explicitly absent local disk never submits a metadata request on repeated polls");
            check(VmQueryService.GetDiskSizes(absentDirectoryPath, Read) == (0, 0, "Unknown") && queries == 0,
                "an explicitly absent parent directory also avoids a metadata request");

            File.WriteAllBytes(path, new byte[10]);
            check(VmQueryService.GetDiskSizes(path, Read) == (10, 100, "Fixed") && queries == 1,
                "a restored disk is queried immediately because absence was never cached");
            check(VmQueryService.GetDiskSizes(path, Read) == (10, 100, "Fixed") && queries == 1,
                "the existing successful file-stamp cache still handles unchanged restored disks");

            File.Delete(path);
            check(VmQueryService.GetDiskSizes(path, Read) == (0, 0, "Unknown") && queries == 1,
                "a disk removed after a successful query cannot return its stale cached capacity");
            File.WriteAllBytes(path, new byte[20]);
            check(VmQueryService.GetDiskSizes(path, _ => { queries++; return (200, "Differencing"); }) ==
                (20, 200, "Differencing") && queries == 2,
                "restoring a previously cached disk obtains fresh capacity and type");
        }
        finally
        {
            File.Delete(path);
            // This directory was created above for this check; it contains no VM or user files.
            Directory.Delete(directory);
        }
    }

    public static void CheckCacheHitsAndUncertainErrors(Action<bool, string> check)
    {
        const string localPath = @"C:\disk-reader-check\disk.vhdx";
        check(VmQueryService.IsMissingLocalDiskFile(localPath, new FileNotFoundException()) &&
            VmQueryService.IsMissingLocalDiskFile(localPath, new DirectoryNotFoundException()),
            "only precise local file/directory absence qualifies for the fast fallback");
        check(!VmQueryService.IsMissingLocalDiskFile(localPath, new UnauthorizedAccessException()) &&
            !VmQueryService.IsMissingLocalDiskFile(localPath, new IOException()) &&
            !VmQueryService.IsMissingLocalDiskFile(localPath, new ArgumentException()),
            "access denied, other IO failures and invalid paths are not treated as missing disks");
        check(!VmQueryService.IsMissingLocalDiskFile(@"\\host\share\disk.vhdx", new FileNotFoundException()) &&
            !VmQueryService.IsMissingLocalDiskFile(@"\\?\C:\disk.vhdx", new FileNotFoundException()) &&
            !VmQueryService.IsMissingLocalDiskFile("relative.vhdx", new FileNotFoundException()),
            "UNC, device and relative paths retain the conservative metadata fallback");

        int uncertainReads = 0;
        var unknownPath = VmQueryService.GetDiskSizes("invalid\0path", _ =>
        {
            uncertainReads++;
            return (100, "Fixed");
        });
        check(uncertainReads == 1 && unknownPath == (0, 100, "Fixed"),
            "a file-probe error with unknown existence still reaches the metadata reader");

        string path = Path.Combine(Path.GetTempPath(), "ExHyperV-DiskReader-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, new byte[10]);
            var seeded = VmQueryService.GetDiskSizes(path, _ => (100, "Dynamic"));
            int acquired = 0;
            using var reader = new VmQueryService.DiskSettingsReader(() =>
            {
                acquired++;
                return new ServiceOwner();
            }, (_, _) => throw new InvalidOperationException("a cache hit must not query metadata"));
            check(VmQueryService.GetDiskSizes(path, reader.Read) == seeded && acquired == 0,
                "a successful cache hit does not acquire an image-service owner");
        }
        finally { File.Delete(path); }
    }

    private sealed class ServiceOwner : IDisposable
    {
        public int DisposeCount { get; private set; }
        public bool ThrowOnDispose { get; init; }

        public void Dispose()
        {
            DisposeCount++;
            if (ThrowOnDispose) throw new IOException("expected cleanup failure");
        }
    }
}
