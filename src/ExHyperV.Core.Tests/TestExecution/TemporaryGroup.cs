using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestExecution;

internal sealed class TemporaryGroup : IAsyncDisposable
{
    private sealed record Journal(string Name, Guid? Id);
    private readonly string directory;
    internal string Name { get; }
    internal Guid Id { get; private set; }
    private TemporaryGroup(string directory) { this.directory = directory; Name = "ExHyperV-ApiGroup-" + Guid.NewGuid().ToString("N"); }

    internal static async Task<TemporaryGroup> CreateAsync(ApiCaseContext c, VmGroupKind kind, Guid? parent = null, bool targetCall = false)
    {
        var group = new TemporaryGroup(Path.Combine(c.DirectoryPath, "groups", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(group.directory); group.Save();
        group.Id = targetCall ? await c.CallAsync(() => VmGroupService.CreateAsync(group.Name, kind, parent))
            : await VmGroupService.CreateAsync(group.Name, kind, parent);
        group.Save(); return group;
    }

    private void Save()
    {
        var path = Path.Combine(directory, "group.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(new Journal(Name, Id == Guid.Empty ? null : Id), InspectionJson.Options));
        File.Move(path + ".new", path, true);
    }

    internal static async Task CleanupAllAsync(string directory)
    {
        var root = Path.Combine(directory, "groups"); if (!Directory.Exists(root)) return;
        foreach (string child in Directory.GetDirectories(root))
            if (Guid.TryParseExact(Path.GetFileName(child), "N", out _)) await CleanupAsync(child);
    }

    private static async Task CleanupAsync(string directory)
    {
        var path = Path.Combine(directory, "group.json"); if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), InspectionJson.Options) ?? throw new IOException("分组归属记录缺失。");
        const string prefix = "ExHyperV-ApiGroup-";
        if (!journal.Name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(journal.Name[prefix.Length..], "N", out _)) throw new IOException("分组归属记录校验失败。");
        var all = await VmGroupService.ReadAsync();
        var owned = all.Groups.Where(g => journal.Id.HasValue ? g.Id == journal.Id : g.Name == journal.Name).ToArray();
        foreach (var group in owned) await VmGroupService.DeleteAsync(group.Id);
        var after = await VmGroupService.ReadAsync();
        if (after.Groups.Any(g => owned.Any(previous => g.Id == previous.Id))) throw new IOException("专属分组清理后仍存在。");
        File.Delete(path);
    }

    public async ValueTask DisposeAsync() => await CleanupAsync(directory);
}
