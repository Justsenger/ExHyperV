using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExHyperV.Services;
using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class IsoApiCases
{
    internal const string ImageName = "roundtrip.iso";
    private sealed record ImageFile(string Path, long Length, string Hash);

    internal static async Task RunAsync(string id, ApiCaseContext context)
    {
        var source = Path.Combine(context.DirectoryPath, "source");
        Directory.CreateDirectory(Path.Combine(source, "子目录"));
        File.WriteAllText(Path.Combine(source, "中文 空格.txt"), "光盘内容核验\r\n0123456789", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(source, "子目录", "binary.bin"), Enumerable.Range(0, 8193).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllBytes(Path.Combine(source, "empty.txt"), []);
        var target = Path.Combine(context.DirectoryPath, ImageName);
        if (id.EndsWith("-2", StringComparison.Ordinal))
        {
            OnSta(() => context.Throws<Exception>(() => context.Call(() => IsoBuilderService.BuildUdfIso(
                Path.Combine(source, "missing"), target, "API_TEST")), "源目录缺失时返回异常"));
            OnSta(() => context.Throws<Exception>(() => context.Call(() => IsoBuilderService.BuildUdfIso(
                source, source, "API_TEST")), "输出指向目录时返回异常"));
            // Exclusive access and deletion prove the failed calls released their file handles.
            if (File.Exists(target)) { using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } File.Delete(target); }
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            context.Evidence.Add("失败后的源文件可独占打开；残留输出已清理。");
            return;
        }
        var expected = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(file => new ImageFile(
            Path.GetRelativePath(source, file), new FileInfo(file).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        OnSta(() => context.Call(() => IsoBuilderService.BuildUdfIso(source, target, "API_TEST")));
        context.Require(File.Exists(target) && new FileInfo(target).Length > 0, "生成具有内容的 ISO 文件");
        long completeLength = new FileInfo(target).Length;
        using (var output = File.Open(target, FileMode.Append, FileAccess.Write, FileShare.None))
            output.Write(new byte[8192]);
        OnSta(() => context.Call(() => IsoBuilderService.BuildUdfIso(source, target, "API_TEST")));
        context.Equal(completeLength, new FileInfo(target).Length, "重复构建截断旧输出尾部，长度与首次完整映像一致");
        var json = await PowerShellAsync(target, """
            $mounted = $false
            try {
                $image = Mount-DiskImage -ImagePath $env:EXHYPERV_TEST_IMAGE -Access ReadOnly -NoDriveLetter -PassThru -ErrorAction Stop
                $mounted = $true
                $volume = @($image | Get-Volume -ErrorAction Stop)
                if ($volume.Count -ne 1) { throw 'Expected one volume' }
                $root = $volume[0].Path
                if ([string]::IsNullOrEmpty($root)) { throw 'Mounted volume path missing' }
                $files = @([IO.Directory]::EnumerateFiles($root, '*', [IO.SearchOption]::AllDirectories) | ForEach-Object {
                    $path = $_
                    $stream = [IO.File]::OpenRead($path)
                    $sha = [Security.Cryptography.SHA256]::Create()
                    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                    finally { $sha.Dispose(); $stream.Dispose() }
                    @{ path = $path.Substring($root.Length).TrimStart('\'); length = ([IO.FileInfo]$path).Length; hash = $hash }
                })
                ConvertTo-Json -InputObject $files -Depth 4 -Compress
            } finally {
                if ($mounted) { Dismount-DiskImage -ImagePath $env:EXHYPERV_TEST_IMAGE -ErrorAction Stop | Out-Null }
            }
            """);
        var actual = JsonSerializer.Deserialize<ImageFile[]>(json, InspectionJson.Options)
            ?? throw new InvalidDataException("挂载后的文件清单缺失。");
        context.Sequence(expected.Select(f => f.Path), actual.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => f.Path), "文件路径（含中文、子目录、空文件）");
        foreach (var file in expected)
        {
            var found = actual.Single(f => f.Path.Equals(file.Path, StringComparison.Ordinal));
            context.Equal(file.Length, found.Length, $"{file.Path} 字节数");
            context.Equal(file.Hash, found.Hash, $"{file.Path} SHA-256");
        }
        context.Evidence.Add("映像已只读挂载核验并卸载。");
    }

    internal static async Task ReleaseImageAsync(string directory)
    {
        var path = Path.Combine(directory, ImageName);
        if (!File.Exists(path)) return;
        // This exact path is allocated by the parent for this case, including timeout recovery.
        await PowerShellAsync(path, """
            $image = Get-DiskImage -ImagePath $env:EXHYPERV_TEST_IMAGE -ErrorAction Stop
            if ($image.Attached) { Dismount-DiskImage -ImagePath $env:EXHYPERV_TEST_IMAGE -ErrorAction Stop | Out-Null }
            """);
    }

    internal static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static async Task<string> PowerShellAsync(string image, string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["EXHYPERV_TEST_IMAGE"] = image;
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("映像核验进程启动失败。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(output, error); throw new TimeoutException("映像核验超过 45 秒。"); }
        if (process.ExitCode != 0) throw new IOException((await error).Trim());
        return (await output).Trim();
    }
}
