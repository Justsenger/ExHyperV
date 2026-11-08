using System.ComponentModel;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed record VmPurgeDiskFile(string Path, string Identity);

internal sealed record VmPurgeDiskPlan(IReadOnlyList<VmPurgeDiskFile> Deletable,
    IReadOnlyList<string> Shared, IReadOnlyList<string> Uncertain, IReadOnlyList<string> Issues);

/// <summary>只把已登记的其他虚拟机及检查点的磁盘父链用于保护，不扩大删除候选。</summary>
internal static class VmPurgeDiskProtection
{
    internal static async Task<VmPurgeDiskPlan> EvaluateAsync(IEnumerable<string> candidates,
        IEnumerable<string> foreignDisks, Func<string, Task<ApiResponse<string?>>> readParent,
        Func<string, ApiResponse<string>> identify, int maximumDepth = 256)
    {
        var issues = new List<string>();
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedIdentities = new HashSet<string>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        bool unknownDependency = false;
        foreach (string start in foreignDisks.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var chain = new HashSet<string>(StringComparer.Ordinal);
            var visited = new List<string>();
            string current = start;
            bool complete = false;
            for (int depth = 0; depth < maximumDepth; depth++)
            {
                try
                {
                    if (!Path.IsPathFullyQualified(current)) throw new IOException("已登记的磁盘引用不是完整路径。");
                    current = Path.GetFullPath(current);
                    protectedPaths.Add(current);
                    var identity = identify(current);
                    if (!identity.HasData)
                        throw new IOException(identity.Success ? "引用的磁盘文件不存在。" : identity.Error);
                    protectedIdentities.Add(identity.Data);
                    if (completed.Contains(identity.Data)) { complete = true; break; }
                    if (!chain.Add(identity.Data)) throw new IOException("磁盘父链出现循环。");
                    visited.Add(identity.Data);
                    var parent = await readParent(current).ConfigureAwait(false);
                    if (!parent.Success) throw new IOException(parent.Error);
                    var afterRead = identify(current);
                    if (!afterRead.HasData || afterRead.Data != identity.Data)
                        throw new IOException("读取父链期间磁盘文件身份发生变化。");
                    if (!parent.HasData) { complete = true; break; }
                    if (string.IsNullOrWhiteSpace(parent.Data)) throw new IOException("父磁盘路径为空，无法确认依赖。");
                    // 相对父路径相对于子盘目录解析，不能使用进程工作目录。
                    current = Path.IsPathFullyQualified(parent.Data) ? parent.Data :
                        Path.GetFullPath(parent.Data, Path.GetDirectoryName(current)!);
                }
                catch (Exception error)
                {
                    issues.Add($"无法核对磁盘依赖：{current}。{error.Message}");
                    break;
                }
            }
            if (!complete)
            {
                unknownDependency = true;
                if (chain.Count >= maximumDepth) issues.Add($"磁盘父链超过核对上限：{start}。");
            }
            else foreach (string identity in visited) completed.Add(identity);
        }

        var deletable = new List<VmPurgeDiskFile>();
        var shared = new List<string>();
        var uncertain = new List<string>();
        foreach (string rawPath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = rawPath;
            try
            {
                if (!Path.IsPathFullyQualified(path)) throw new IOException("待删除磁盘不是完整路径。");
                path = Path.GetFullPath(path);
                if (protectedPaths.Contains(path)) { shared.Add(path); continue; }
                var identity = identify(path);
                if (identity.Success && !identity.HasData) continue; // 已确认不存在的文件无需删除。
                if (!identity.HasData) throw new IOException(identity.Error);
                if (protectedIdentities.Contains(identity.Data)) { shared.Add(path); continue; }
                if (unknownDependency) uncertain.Add(path);
                else
                {
                    // 候选本身的格式/元数据也须可核对；不能把无法读取的文件视为独占普通盘。
                    var metadata = await readParent(path).ConfigureAwait(false);
                    if (!metadata.Success) throw new IOException(metadata.Error);
                    deletable.Add(new(path, identity.Data));
                }
            }
            catch (Exception error)
            {
                uncertain.Add(path);
                issues.Add($"无法确认待删除磁盘身份：{path}。{error.Message}");
            }
        }
        return new(deletable, shared, uncertain, issues.Distinct().ToArray());
    }

    internal static ApiResponse<string> IdentifyFile(string path)
    {
        try { return ApiResponse<string>.Ok(HostDiskResource.Image(path)); }
        catch (FileNotFoundException) { return ApiResponse<string>.Empty(); }
        catch (DirectoryNotFoundException) { return ApiResponse<string>.Empty(); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        { return ApiResponse<string>.Empty(); }
        catch (Exception error)
        { return ApiResponse<string>.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed); }
    }
}
