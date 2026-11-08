using ExHyperV.Services;
using ExHyperV.Tools;

internal static class ExportPlanChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "ExHyperV-export-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Task<VmExportResult> Export(Guid id, string root, VmExportCheckpointMode mode = VmExportCheckpointMode.None) =>
                VmExportService.ExportAsync(id, root, true, [], mode, null, false);
            var emptyId = await Export(Guid.Empty, scratch);
            check(!emptyId.Success && !emptyId.OperationStateUnknown && emptyId.RetainedPaths.Count == 0,
                "Single export rejects an empty identity before creating output");
            var id = Guid.NewGuid();
            var missing = await Export(id, Path.Combine(scratch, "missing"));
            check(!missing.Success && !Directory.Exists(Path.Combine(scratch, "missing")),
                "Single export requires an existing destination root");
            string output = Path.Combine(scratch, id.ToString("D").ToUpperInvariant());
            Directory.CreateDirectory(output);
            string marker = Path.Combine(output, "owned-by-caller.txt");
            await File.WriteAllTextAsync(marker, "preserve");
            var collision = await Export(id, scratch);
            check(!collision.Success && await File.ReadAllTextAsync(marker) == "preserve" && collision.RetainedPaths.Count == 0,
                "Single export preserves an existing destination without claiming caller files");
            File.Delete(marker); Directory.Delete(output);
            await File.WriteAllTextAsync(output + ".partial", "preserve partial");
            var partial = await Export(id, scratch);
            check(!partial.Success && await File.ReadAllTextAsync(output + ".partial") == "preserve partial",
                "Single export preserves an existing staging file");
            File.Delete(output + ".partial");
            var invalidMode = await Export(id, scratch, (VmExportCheckpointMode)999);
            check(!invalidMode.Success && invalidMode.ErrorKind == ApiErrorKind.InvalidArgument && !Directory.EnumerateFileSystemEntries(scratch).Any(),
                "Single export rejects an undefined checkpoint mode before accessing Hyper-V");
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
