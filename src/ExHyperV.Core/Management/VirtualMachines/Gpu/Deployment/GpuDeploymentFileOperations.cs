using System.Diagnostics;
using System.IO;
using System.Text;

namespace ExHyperV.Services;

// 每次离线部署独享；不在服务实例或进程之间缓存来宾目录权限。
internal sealed class GpuDeploymentFileOperations(Action<string> reportWarning)
{
    private readonly HashSet<string> preparedDirectories = new(StringComparer.OrdinalIgnoreCase);

    internal void PrepareDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        lock (preparedDirectories)
        {
            if (preparedDirectories.Contains(fullPath)) return;

            var ownership = RunCommand("takeown.exe", "/f", fullPath, "/a");
            var access = RunCommand("icacls.exe", fullPath, "/grant", "*S-1-5-32-544:F");
            if (ownership.ExitCode != 0) Warn($"[GPU Permissions] {fullPath}: {ownership.Error}");
            if (access.ExitCode != 0) Warn($"[GPU Permissions] {fullPath}: {access.Error}");
            if (ownership.ExitCode == 0 && access.ExitCode == 0)
                preparedDirectories.Add(fullPath);
        }
    }

    internal void ReplaceWithLink(string linkPath, string guestTarget, bool replaceExisting)
    {
        string? backupPath = null;
        if (replaceExisting)
        {
            EnsureFileEntry(linkPath);
            backupPath = $"{linkPath}.exhyperv-backup-{Guid.NewGuid():N}";
            // 移动链接本身，不复制或打开其来宾目标；失败时原样移回。
            File.Move(linkPath, backupPath);
        }

        try
        {
            // guestTarget 是来宾盘内的绝对路径，不解析成宿主挂载路径。
            File.CreateSymbolicLink(linkPath, guestTarget);
        }
        catch (Exception createError)
        {
            if (backupPath != null)
            {
                try { File.Move(backupPath, linkPath); }
                catch (Exception restoreError)
                {
                    // 目标若被其他操作占用，不删除它；保留备份并报告恢复位置。
                    throw new AggregateException(
                        $"Unable to create link {linkPath}; rollback failed. Backup retained at {backupPath}.",
                        createError, restoreError);
                }
            }
            throw;
        }

        if (backupPath != null)
        {
            try
            {
                FileAttributes attributes = EnsureFileEntry(backupPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(backupPath, attributes & ~FileAttributes.ReadOnly);
                File.Delete(backupPath);
            }
            catch (Exception error)
            {
                Warn($"[GPU Link] Link created, but backup cleanup failed for {backupPath}: {error.Message}");
            }
        }
    }

    private static FileAttributes EnsureFileEntry(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException($"Refusing to replace directory {path}.");
        if ((attributes & FileAttributes.ReparsePoint) != 0 && new FileInfo(path).LinkTarget == null)
            throw new IOException($"Refusing to replace unrecognized reparse point {path}.");
        return attributes;
    }

    private void Warn(string message)
    {
        Debug.WriteLine(message);
        try { reportWarning(message); }
        catch { /* 诊断回调不得改变文件操作的结果。 */ }
    }

    internal readonly record struct CommandResult(int ExitCode, string Error);

    internal static CommandResult RunCommand(string executable, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), executable),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new IOException($"Unable to start {executable}.");

            // 两路同时排空，达到诊断长度上限后仍继续读取，避免子进程阻塞。
            Task<string> output = ReadOutputAsync(process.StandardOutput);
            Task<string> error = ReadOutputAsync(process.StandardError);
            Task.WhenAll(output, error, process.WaitForExitAsync()).GetAwaiter().GetResult();
            int code = process.ExitCode;
            string details = string.Join(Environment.NewLine,
                new[] { error.Result.Trim(), output.Result.Trim() }.Where(text => text.Length != 0));
            return new(code, code == 0 ? string.Empty :
                $"{Path.GetFileName(executable)} exited with code {code}. {details}".TrimEnd());
        }
        catch (Exception error)
        {
            return new(-1, $"{Path.GetFileName(executable)}: {error.Message}");
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader)
    {
        const int limit = 4096;
        var text = new StringBuilder(limit);
        var buffer = new char[4096];
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            int retained = Math.Min(count, limit - text.Length);
            text.Append(buffer, 0, retained);
            truncated |= retained < count;
        }
        if (truncated) text.Append("\n[output truncated]");
        return text.ToString();
    }
}
