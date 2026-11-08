using System.Text.Json;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class TemporaryDisk
{
    internal static async Task<string> AttachAsync(TemporaryVm vm, string marker = "before", long sizeBytes = 512L * 1024 * 1024)
    {
        // Each VM has its own root. Leave room for Hyper-V's checkpoint suffix in exported paths.
        int index = 0;
        string path;
        do { path = Path.Combine(vm.Root, $"d{++index}.vhdx"); } while (File.Exists(path));
        await WindowsTestCommand.RunAsync("""
            New-VHD -Path $a.path -SizeBytes $a.sizeBytes -Dynamic|Out-Null
            $mounted=$false
            try {
                $disk=Mount-VHD -Path $a.path -NoDriveLetter -Passthru|Get-Disk; $mounted=$true
                $disk|Initialize-Disk -PartitionStyle GPT|Out-Null
                $partition=$disk|New-Partition -UseMaximumSize
                $volume=$partition|Format-Volume -FileSystem NTFS -NewFileSystemLabel 'API_TEST' -Confirm:$false
                [IO.File]::WriteAllText((Join-Path $volume.Path 'marker.txt'),$a.marker,[Text.Encoding]::UTF8)
            } finally {if($mounted){Dismount-VHD -Path $a.path}}
            Get-VM -Id ([guid]$a.id)|Add-VMHardDiskDrive -Path $a.path
            @{path=$a.path}|ConvertTo-Json
            """, new { id = vm.Id, path, marker, sizeBytes }, 90);
        return path;
    }

    internal static async Task<string[]> PathsAsync(Guid id) => (await WindowsTestCommand.RunAsync("ConvertTo-Json -InputObject @((Get-VM -Id ([guid]$a.id)|Get-VMHardDiskDrive).Path)", new { id }))
        .EnumerateArray().Select(x => x.GetString()!).ToArray();

    internal static async Task<string> MarkerAsync(string path, string? value = null)
    {
        var json = await WindowsTestCommand.RunAsync("""
            $mounted=$false
            try {
                $disk=Mount-VHD -Path $a.path -NoDriveLetter -Passthru -ReadOnly:(!$a.write)|Get-Disk; $mounted=$true
                $volume=@($disk|Get-Partition|Get-Volume|Where-Object FileSystemLabel -eq 'API_TEST')
                if($volume.Count -ne 1){throw '标记卷数量异常'}
                $file=Join-Path $volume[0].Path 'marker.txt'
                if($a.write){[IO.File]::WriteAllText($file,$a.value,[Text.Encoding]::UTF8)}
                @{marker=[IO.File]::ReadAllText($file)}|ConvertTo-Json
            } finally {if($mounted){Dismount-VHD -Path $a.path}}
            """, new { path, write = value is not null, value }, 60);
        return json.GetProperty("marker").GetString()!;
    }

    internal static async Task CleanupMountsAsync(string directory)
    {
        // Every candidate is a newly allocated file below this worker's private directory.
        var paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".vhd" or ".vhdx" or ".avhd" or ".avhdx").ToArray();
        if (paths.Length == 0) return;
        await WindowsTestCommand.RunAsync("""
            foreach($path in $a.paths){
                $image=Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
                if($image -and $image.Attached){Dismount-DiskImage -ImagePath $path -ErrorAction Stop|Out-Null}
                $after=Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
                if($after -and $after.Attached){throw ('测试磁盘仍挂载：'+$path)}
            }
            @{cleaned=$true}|ConvertTo-Json
            """, new { paths }, 120);
    }
}
