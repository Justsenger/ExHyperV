using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using ExHyperV.Services;
using Microsoft.Win32;
using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestExecution;

// Write intent and the original value before touching shared host state. Recovery is
// independent of the API being tested, and is repeatable after a worker crash.
internal sealed class HostStateJournal : IAsyncDisposable
{
    private sealed record Entry(string Kind, string Key, string Name, JsonElement Value, int Type = 0, bool Exists = true);
    private sealed record Journal(string Sid, List<Entry> Entries);
    private readonly string path;
    private Journal journal;
    internal HostStateJournal(ApiCaseContext c)
    {
        path = Path.Combine(c.DirectoryPath, "host-state.json");
        journal = System.IO.File.Exists(path) ? Read(path) : new(WindowsIdentity.GetCurrent().User!.Value, []);
    }
    private void Add(Entry entry)
    {
        journal = System.IO.File.Exists(path) ? Read(path) : new(WindowsIdentity.GetCurrent().User!.Value, []);
        if (journal.Entries.Any(e => e.Kind == entry.Kind && e.Key == entry.Key && e.Name == entry.Name)) return;
        journal.Entries.Add(entry); Write(path, journal);
    }
    internal void Registry(string key, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var opened = root.OpenSubKey(key);
        var exists = opened?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
        var kind = exists ? opened!.GetValueKind(name) : RegistryValueKind.None;
        Add(new("registry", key, name, JsonSerializer.SerializeToElement(exists ? opened!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null), (int)kind, exists));
    }
    internal void File(string file)
    {
        string full = Path.GetFullPath(file); bool exists = System.IO.File.Exists(full);
        Add(new("file", full, "", JsonSerializer.SerializeToElement(exists ? Convert.ToBase64String(System.IO.File.ReadAllBytes(full)) : ""), Exists: exists));
    }
    internal void ConfigurationElement(string file, string name)
    {
        string full = Path.GetFullPath(file);
        var document = new XmlConfigurationFile(full).Load();
        var values = document.Root?.Elements(XName.Get(name)).Select(value => value.ToString(SaveOptions.DisableFormatting)).ToArray()
            ?? throw new InvalidDataException("配置文件缺少根元素。");
        Add(new("configuration-element", full, name, JsonSerializer.SerializeToElement(values)));
    }
    internal async Task SettingAsync(string name)
    {
        var original = await WindowsTestCommand.RunAsync("""
            $h=Get-VMHost
            @{VirtualMachinePath=$h.VirtualMachinePath;VirtualHardDiskPath=$h.VirtualHardDiskPath;MacAddressMinimum=$h.MacAddressMinimum;MacAddressMaximum=$h.MacAddressMaximum;EnableEnhancedSessionMode=$h.EnableEnhancedSessionMode;NumaSpanningEnabled=$h.NumaSpanningEnabled}|ConvertTo-Json
            """);
        if (name is not ("VirtualMachinePath" or "VirtualHardDiskPath" or "MacAddressMinimum" or "MacAddressMaximum" or "EnableEnhancedSessionMode" or "NumaSpanningEnabled")) throw new ArgumentException(nameof(name));
        if (name.StartsWith("MacAddress", StringComparison.Ordinal)) Add(new("mac", "", "", original));
        else Add(new("host", "", name, original.GetProperty(name).Clone()));
    }
    internal async Task PowerAsync()
    {
        var state = await WindowsTestCommand.RunAsync("$s=powercfg /getactivescheme; if($LASTEXITCODE){throw $s}; [regex]::Match(($s -join ' '),'[0-9a-fA-F-]{36}').Value|ConvertTo-Json");
        Add(new("power", "", "", state));
    }
    internal async Task DiskAsync(ExHyperV.Services.HostDiskReference disk)
    {
        var original = await WindowsTestCommand.RunAsync("""
            $d=Get-Disk -Number $a.Number
            if($d.IsSystem -or $d.IsBoot -or $d.UniqueId.Trim() -ne $a.UniqueId.Trim() -or $d.Size -ne $a.SizeBytes){throw '磁盘身份或系统盘标志变化'}
            @{number=$d.Number;uniqueId=$d.UniqueId;serial=$d.SerialNumber;size=$d.Size;offline=$d.IsOffline;readOnly=$d.IsReadOnly}|ConvertTo-Json
            """, disk);
        Add(new("disk", disk.UniqueId, "", original));
    }
    private const string MountManagerKey = @"SYSTEM\CurrentControlSet\Services\mountmgr";
    internal static bool ReadAutoMount()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MountManagerKey)
            ?? throw new IOException("无法读取 Mount Manager 注册表项。");
        return key.GetValue("NoAutoMount") switch
        {
            null or 0 => true,
            1 => false,
            _ => throw new InvalidDataException("NoAutoMount 的值不是 0 或 1。")
        };
    }
    internal void AutoMount()
    {
        bool enabled = ReadAutoMount();
        Registry(MountManagerKey, "NoAutoMount");
        Add(new("automount", "", "", JsonSerializer.SerializeToElement(enabled)));
    }
    internal static async Task RecoverAsync(string directory)
    {
        string path = Path.Combine(directory, "host-state.json"); if (!System.IO.File.Exists(path)) return;
        var journal = Read(path);
        if (journal.Sid != WindowsIdentity.GetCurrent().User!.Value) throw new IOException("恢复配置需要原执行账户。");
        while (journal.Entries.Count > 0)
        {
            var e = journal.Entries[^1];
            switch (e.Kind)
            {
                case "registry":
                    using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var key = e.Exists ? root.CreateSubKey(e.Key) : root.OpenSubKey(e.Key, true))
                    {
                        if (!e.Exists) key?.DeleteValue(e.Name, false);
                        else
                        {
                            var kind = (RegistryValueKind)e.Type;
                            object value = kind switch { RegistryValueKind.DWord => e.Value.GetInt32(), RegistryValueKind.QWord => e.Value.GetInt64(), RegistryValueKind.Binary or RegistryValueKind.None => e.Value.GetBytesFromBase64(), RegistryValueKind.MultiString => e.Value.Deserialize<string[]>()!, _ => e.Value.GetString()! };
                            key!.SetValue(e.Name, value, kind);
                        }
                        bool exists = key?.GetValueNames().Contains(e.Name, StringComparer.OrdinalIgnoreCase) == true;
                        if (exists != e.Exists || (exists && (int)key!.GetValueKind(e.Name) != e.Type)) throw new IOException("注册表恢复核验失败：" + e.Name);
                        if (exists && JsonSerializer.SerializeToElement(key!.GetValue(e.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)).GetRawText() != e.Value.GetRawText()) throw new IOException("注册表原值核验失败：" + e.Name);
                    }
                    break;
                case "file":
                    if (e.Exists) { Directory.CreateDirectory(Path.GetDirectoryName(e.Key)!); System.IO.File.WriteAllBytes(e.Key + ".test-restore", Convert.FromBase64String(e.Value.GetString()!)); System.IO.File.Move(e.Key + ".test-restore", e.Key, true); }
                    else System.IO.File.Delete(e.Key);
                    if (System.IO.File.Exists(e.Key) != e.Exists || (e.Exists && Convert.ToBase64String(System.IO.File.ReadAllBytes(e.Key)) != e.Value.GetString())) throw new IOException("配置文件恢复核验失败。");
                    break;
                case "configuration-element":
                    RestoreConfigurationElement(e);
                    break;
                case "host":
                    await WindowsTestCommand.RunAsync("$params=@{}; $params[$a.name]=$a.value; Set-VMHost @params; $v=(Get-VMHost).($a.name); if($v -ne $a.value){throw '宿主设置恢复核验失败'}; $true|ConvertTo-Json", new { name = e.Name, value = e.Value }); break;
                case "mac":
                    await WindowsTestCommand.RunAsync("Set-VMHost -MacAddressMinimum $a.min -MacAddressMaximum $a.max; $v=Get-VMHost; if($v.MacAddressMinimum -ne $a.min -or $v.MacAddressMaximum -ne $a.max){throw 'MAC 范围恢复核验失败'}; $true|ConvertTo-Json", new { min = e.Value.GetProperty("MacAddressMinimum").GetString(), max = e.Value.GetProperty("MacAddressMaximum").GetString() }); break;
                case "disk":
                    await WindowsTestCommand.RunAsync("""
                        $d=Get-Disk -Number $a.number
                        if($d.IsSystem -or $d.IsBoot -or $d.UniqueId.Trim() -ne $a.uniqueId.Trim() -or $d.Size -ne $a.size -or ([string]$d.SerialNumber).Trim() -ne ([string]$a.serial).Trim()){throw '待恢复磁盘身份已变化，保留原记录'}
                        $d|Set-Disk -IsReadOnly $false
                        $d|Set-Disk -IsOffline ([bool]$a.offline)
                        $d|Set-Disk -IsReadOnly ([bool]$a.readOnly)
                        $v=Get-Disk -Number $a.number
                        if($v.IsOffline -ne $a.offline -or $v.IsReadOnly -ne $a.readOnly){throw '磁盘状态恢复核验失败'}
                        $true|ConvertTo-Json
                        """, e.Value); break;
                case "power":
                    await WindowsTestCommand.RunAsync("powercfg /setactive $a.id|Out-Null; if($LASTEXITCODE){throw '电源计划恢复失败'}; $v=powercfg /getactivescheme; if(($v -join ' ') -notmatch [regex]::Escape($a.id)){throw '电源计划恢复核验失败'}; $true|ConvertTo-Json", new { id = e.Value.GetString() }); break;
                case "automount":
                    bool enabled = e.Value.GetBoolean();
                    await WindowsTestCommand.RunAsync("$verb=if($a.enabled){'enable'}else{'disable'}; $out=('automount '+$verb)|diskpart; if($LASTEXITCODE){throw ($out -join ' ')}; $true|ConvertTo-Json", new { enabled });
                    if (ReadAutoMount() != enabled) throw new IOException("自动挂载恢复核验失败。");
                    break;
                default: throw new InvalidDataException("未知恢复记录：" + e.Kind);
            }
            journal.Entries.RemoveAt(journal.Entries.Count - 1); Write(path, journal);
        }
        System.IO.File.Delete(path);
    }
    private static void RestoreConfigurationElement(Entry entry)
    {
        var file = new XmlConfigurationFile(entry.Key);
        var name = XName.Get(entry.Name);
        var original = entry.Value.Deserialize<string[]>()!.Select(value => XElement.Parse(value)).ToArray();
        bool Matches(XElement root) => root.Elements(name).SequenceEqual(original, XNode.EqualityComparer);
        var current = file.Load().Root ?? throw new InvalidDataException("配置文件缺少根元素。");
        if (Matches(current)) return;
        // Use Core's shared configuration mutex and reload the latest document.
        // Restore only our field; other programs' settings stay as they are now.
        file.Update(root =>
        {
            root.Elements(name).Remove();
            root.Add(original.Select(element => new XElement(element)));
        });
        if (!Matches(file.Load().Root!)) throw new IOException("配置字段恢复核验失败：" + entry.Name);
    }
    private static Journal Read(string path) => JsonSerializer.Deserialize<Journal>(System.IO.File.ReadAllText(path), InspectionJson.Options) ?? throw new IOException("宿主恢复记录损坏。");
    private static void Write(string path, Journal journal)
    { using (var output = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(output, journal, InspectionJson.Options); output.Flush(true); } System.IO.File.Move(path + ".new", path, true); }
    public ValueTask DisposeAsync() => System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "recovery-hold.txt")) ? ValueTask.CompletedTask : new(RecoverAsync(Path.GetDirectoryName(path)!));
}
