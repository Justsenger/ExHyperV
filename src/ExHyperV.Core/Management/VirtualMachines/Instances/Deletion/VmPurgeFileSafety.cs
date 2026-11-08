using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed record VmPurgeConfigFiles(IReadOnlyList<VmPurgeDiskFile> Files,
    IReadOnlyList<string> Directories, IReadOnlyDictionary<string, string> Retained);

/// <summary>彻底删除只处理明确的配置文件与空目录，不沿重解析点或递归删除未知内容。</summary>
internal static class VmPurgeFileSafety
{
    // Hyper-V 的配置/运行状态格式；虚拟磁盘、ISO、RCT/MRT 等不属于此清理入口。
    // https://learn.microsoft.com/windows-server/virtualization/hyper-v/deploy/upgrade-virtual-machine-version-in-hyper-v-on-windows-or-windows-server
    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".vmcx", ".vmrs", ".vmgs", ".xml", ".bin", ".vsv" };

    internal static VmPurgeConfigFiles Collect(IEnumerable<string> roots, IEnumerable<string> ids,
        Func<string, ApiResponse<string>> identify)
    {
        var files = new Dictionary<string, VmPurgeDiskFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddFile(string file)
        {
            file = Path.GetFullPath(file);
            if (!ConfigExtensions.Contains(Path.GetExtension(file)))
            { retained[file] = "文件不是已确认的虚拟机配置或状态格式，已保留。"; return; }
            try
            {
                EnsureNoReparse(file);
                var identity = identify(file);
                if (!identity.HasData)
                {
                    if (!identity.Success) retained[file] = identity.Error;
                    return;
                }
                files[file] = new(file, identity.Data);
            }
            catch (Exception error) { retained[file] = error.Message; }
        }
        void VisitDirectory(string directory)
        {
            try
            {
                EnsureNoReparse(directory);
                directories.Add(Path.GetFullPath(directory));
                foreach (string file in Directory.EnumerateFiles(directory)) AddFile(file);
                foreach (string child in Directory.EnumerateDirectories(directory)) VisitDirectory(child);
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) { retained[directory] = error.Message; }
        }
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (string container in new[] { Path.Combine(root, "Virtual Machines"), Path.Combine(root, "Snapshots") })
        {
            try
            {
                EnsureNoReparse(container);
                foreach (string id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string file in Directory.EnumerateFiles(container, id + ".*")) AddFile(file);
                    string directory = Path.Combine(container, id);
                    try
                    {
                        var attributes = File.GetAttributes(directory);
                        if ((attributes & FileAttributes.Directory) != 0) VisitDirectory(directory);
                    }
                    catch (DirectoryNotFoundException) { }
                    catch (FileNotFoundException) { }
                    catch (Exception error) { retained[directory] = error.Message; }
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
            catch (Exception error) { retained[container] = error.Message; }
        }
        return new(files.Values.ToArray(), directories.ToArray(), retained);
    }

    internal static void EnsureNoReparse(string path)
    {
        string full = Path.GetFullPath(path);
        for (string? part = full; part != null; part = Path.GetDirectoryName(part))
        {
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"路径包含重解析点，未执行配置清理：{part}");
        }
    }

    internal static async Task<ApiResponse> DeleteFileAsync(VmPurgeDiskFile expected)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                EnsureNoReparse(expected.Path);
                var current = VmPurgeDiskProtection.IdentifyFile(expected.Path);
                if (current.Success && !current.HasData) return ApiResponse.Ok();
                if (!current.HasData || current.Data != expected.Identity)
                    return ApiResponse.Fail("文件身份已变化或无法确认，已保留。", kind: ApiErrorKind.Conflict);
                File.Delete(expected.Path);
                return ApiResponse.Ok();
            }
            catch (DirectoryNotFoundException) { return ApiResponse.Ok(); }
            catch (FileNotFoundException) { return ApiResponse.Ok(); }
            catch (Exception error)
            {
                if (attempt == 5) return ApiResponse.Fail(error.Message, exception: error, kind: ApiErrorKind.CleanupRequired);
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        return ApiResponse.Fail("未能删除文件。", kind: ApiErrorKind.CleanupRequired);
    }

    internal static ApiResponse<IReadOnlyList<string>> DeleteEmptyDirectory(string path)
    {
        try
        {
            EnsureNoReparse(path);
            if (Directory.EnumerateFileSystemEntries(path).Any())
                return ApiResponse<IReadOnlyList<string>>.Ok(EnumerateRemaining(path));
            Directory.Delete(path, recursive: false);
            return ApiResponse<IReadOnlyList<string>>.Ok(Array.Empty<string>());
        }
        catch (DirectoryNotFoundException) { return ApiResponse<IReadOnlyList<string>>.Ok(Array.Empty<string>()); }
        catch (FileNotFoundException) { return ApiResponse<IReadOnlyList<string>>.Ok(Array.Empty<string>()); }
        catch (Exception error) { return ApiResponse<IReadOnlyList<string>>.Fail(error.Message, exception: error, kind: ApiErrorKind.CleanupRequired); }
    }

    private static IReadOnlyList<string> EnumerateRemaining(string root)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            EnsureNoReparse(directory);
            string[] entries = Directory.GetFileSystemEntries(directory);
            if (entries.Length == 0 && directory != root) result.Add(directory);
            foreach (string entry in entries)
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(entry);
                else result.Add(entry); // 文件或重解析点只报告，不沿它们继续遍历。
            }
        }
        return result;
    }

    internal static bool ContainsOnlyFiles(string root, IEnumerable<string> allowed)
    {
        try
        {
            EnsureNoReparse(root);
            var names = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return EnumerateRemaining(root).All(path => names.Contains(path) ||
                (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory);
        }
        catch { return false; }
    }
}
