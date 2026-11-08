using System.Xml;
using System.Xml.Linq;
using ExHyperV.Services;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmMetadataApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api.StartsWith("VmMetadataService.", StringComparison.Ordinal);
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        var original = ReadNative(vm.Id);
        string[] notes = (string[]?)SystemBaseline.Settings(vm.Id).GetValueOrDefault("Notes") ?? [];
        const string key = "ApiTests.One", other = "ApiTests.Other";
        if (p.Api == "VmMetadataService.ReadAsync()")
        {
            if (p.Id.EndsWith("-2", StringComparison.Ordinal))
            {
                await vm.DisposeAsync(); var gone = await c.CallAsync(() => VmMetadataService.ReadAsync(vm.Id));
                c.Require(!gone.Success && gone.Data is null, "目标缺席返回查询失败，保留空清单与失败的区别"); return;
            }
            var empty = await c.CallAsync(() => VmMetadataService.ReadAsync(vm.Id)); c.Require(empty.HasData, "读取初始元数据：" + empty.Error);
            Compare(c, original, empty.Data!);
            var prepared = await VmMetadataService.SetAsync(vm.Id, key, "中文 & <xml>\r\n"); c.Require(prepared.Success, prepared.Error);
            var result = await c.CallAsync(() => VmMetadataService.ReadAsync(vm.Id)); c.Require(result.HasData, "读取有内容的元数据：" + result.Error); Compare(c, ReadNative(vm.Id), result.Data!);
        }
        else if (p.Api == "VmMetadataService.SetOperatingSystemTypeAsync()")
        {
            if (p.Id.EndsWith("-3", StringComparison.Ordinal))
            { await vm.DisposeAsync(); var gone = await c.CallAsync(() => VmMetadataService.SetOperatingSystemTypeAsync(vm.Id, "Windows")); c.Require(!gone.Success, "消失目标拒绝写入"); return; }
            foreach (string value in p.Id.EndsWith("-2", StringComparison.Ordinal) ? new[] { "Windows", "Windows" } : new[] { "Linux", "Windows" })
            { var result = await c.CallAsync(() => VmMetadataService.SetOperatingSystemTypeAsync(vm.Id, value)); c.Require(result.Success, "保存系统分类：" + result.Error); c.Equal(value, ReadNative(vm.Id)[VmMetadataService.OsTypeKey], "系统 HostOnlyItems 分类值"); }
        }
        else if (p.Api == "VmMetadataService.RemoveAsync()")
        {
            var first = await VmMetadataService.SetAsync(vm.Id, key, "remove"); var second = await VmMetadataService.SetAsync(vm.Id, other, "keep"); c.Require(first.Success && second.Success, "两个测试键已写入");
            for (int i = 0; i < 2; i++) { var result = await c.CallAsync(() => VmMetadataService.RemoveAsync(vm.Id, key)); c.Require(result.Success, "删除指定键：" + result.Error); c.Require(!ReadNative(vm.Id).ContainsKey(key), "目标键消失"); c.Equal("keep", ReadNative(vm.Id)[other], "其他键保持"); }
        }
        else if (p.Id.EndsWith("-2", StringComparison.Ordinal))
        {
            foreach (var input in new[] { (Guid.Empty, key, "value"), (vm.Id, "", "value"), (vm.Id, key, "illegal\u0001") })
            { var result = await c.CallAsync(() => VmMetadataService.SetAsync(input.Item1, input.Item2, input.Item3)); c.Require(!result.Success, "非法元数据输入被拒绝"); Compare(c, original, ReadNative(vm.Id)); }
        }
        else if (p.Id.EndsWith("-3", StringComparison.Ordinal))
        {
            foreach (var pair in new[] { (key, "一"), (other, "二"), (key, "三") })
            { var result = await c.CallAsync(() => VmMetadataService.SetAsync(vm.Id, pair.Item1, pair.Item2)); c.Require(result.Success, "单键提交：" + result.Error); }
            c.Equal("三", ReadNative(vm.Id)[key], "第一个键新值"); c.Equal("二", ReadNative(vm.Id)[other], "第二个键保留");
        }
        else
        {
            foreach (string value in new[] { "中文 <xml> & \"quoted\" 'text'\r\nnext\nline", "覆盖值", "" })
            { var result = await c.CallAsync(() => VmMetadataService.SetAsync(vm.Id, key, value)); c.Require(result.Success, "写入元数据：" + result.Error); c.Equal(value, ReadNative(vm.Id)[key], "特殊字符逐字保留"); }
        }
        c.Sequence(notes, (string[]?)SystemBaseline.Settings(vm.Id).GetValueOrDefault("Notes") ?? [], "虚拟机备注保持原值");
        foreach (var pair in original) c.Equal(pair.Value, ReadNative(vm.Id)[pair.Key], "原有元数据保留 " + pair.Key);
    }
    internal static Dictionary<string, string> ReadNative(Guid id)
    {
        var settings = SystemBaseline.Settings(id);
        var component = SystemBaseline.Query($"ASSOCIATORS OF {{{settings["__PATH"]}}} WHERE ResultClass=Msvm_KvpExchangeComponentSettingData").Single();
        var items = (string[]?)component.GetValueOrDefault("HostOnlyItems") ?? [];
        return ParseNativeItems(items);
    }
    internal static Dictionary<string, string> ParseNativeItems(IEnumerable<string> items)
    {
        return items.Select(text =>
        {
            // CIM XML carries literal CR/CRLF. Ordinary XML parsing normalizes the baseline itself.
            using var input = new StringReader(text);
            using var reader = new XmlTextReader(input) { Normalization = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            return XElement.Load(reader).Elements("PROPERTY").ToDictionary(e => (string)e.Attribute("NAME")!, e => e.Element("VALUE")?.Value ?? "");
        })
            .ToDictionary(item => item["Name"], item => item["Data"], StringComparer.Ordinal);
    }
    private static void Compare(ApiCaseContext c, IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    { c.Sequence(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal), "元数据键集合"); foreach (var pair in expected) c.Equal(pair.Value, actual[pair.Key], "元数据值 " + pair.Key); }
}
