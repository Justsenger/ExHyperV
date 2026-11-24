using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>读取和保存虚拟机 VMCX 配置文件（.vmcx）中的自定义键值数据。</summary>
/// <remarks>数据由 VMMS 保存到 VMCX 文件，通过 HostOnlyItems 提供宿主侧读写，随虚拟机导出携带。每项由区分大小写的键名和字符串值组成，Source 为 4，不交换给来宾。写入仅更新指定键，结构化内容由使用该键的程序约定。</remarks>
/// <example><code>using System;
/// using System.Threading.Tasks;
/// using ExHyperV.Services;
///
/// public static class MetadataExample
/// {
///     public static async Task SaveOsTypeAsync(Guid vmId)
///     {
///         var result = await VmMetadataService.SetAsync(vmId, VmMetadataService.OsTypeKey, "Linux");
///         if (!result.Success)
///             throw new InvalidOperationException(result.Error);
///     }
/// }</code></example>
public static partial class VmMetadataService
{
    /// <summary>操作系统分类的键名；值与 OsImages 使用的分类名称一致。</summary>
    /// <metadatakey purpose="操作系统分类">操作系统类型名称，例如 Linux，用于界面识别和图标显示。</metadatakey>
    public const string OsTypeKey = "ExHyperV.OSType";

    /// <summary>处理器亲和性的键名。新保存使用 Version=2 的 JSON，包含 Backend、LogicalProcessors、HostFingerprint 和 TopologyFingerprint；后两项为宿主 UUID 和编号拓扑的摘要。旧数组与版本 1 记录保留可读，但非空旧选择需要重新保存。空集合表示不限制。</summary>
    /// <metadatakey purpose="CPU 亲和性配置">JSON 文本，记录所选处理器、使用的后端、宿主及处理器拓扑标识。</metadatakey>
    public const string CpuAffinityKey = "ExHyperV.CpuAffinity";

    /// <summary>已部署显卡驱动版本的键名；保存以规范化宿主显卡实例 ID 为键、已写入来宾的驱动版本为值的 JSON 字典。</summary>
    /// <metadatakey purpose="GPU 驱动版本记录">JSON 字典，键为规范化宿主显卡实例 ID，值为最近一次由工具成功部署到来宾系统的驱动版本。</metadatakey>
    public const string GpuDriversKey = "ExHyperV.GpuDrivers";

    internal const string ComponentId = "2A34B1C2-FD73-4043-8A5B-DD2159BC743F";
    private const string ComponentClass = "Msvm_KvpExchangeComponentSettingData";
    private const string ItemClass = "Msvm_KvpExchangeDataItem";

    /// <summary>读取虚拟机 VMCX 配置文件中通过 HostOnlyItems 保存的全部自定义数据。</summary>
    /// <param name="vmId">目标虚拟机的标识。</param>
    /// <returns>成功时返回独立只读字典，未存任何键时字典为空；虚拟机不存在、查询失败或数据损坏时返回失败。</returns>
    /// <queryentry label="读取全部自定义数据" category="自定义数据"/>
    public static async Task<ApiResponse<IReadOnlyDictionary<string, string>>> ReadAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<IReadOnlyDictionary<string, string>>.Fail("虚拟机 GUID 不能为空。");

        var settings = await WmiApi.QueryFirstAsync(
            $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            row => row.Path.Path).ConfigureAwait(false);
        if (!settings.HasData || string.IsNullOrWhiteSpace(settings.Data))
            return ApiResponse<IReadOnlyDictionary<string, string>>.Fail(
                settings.Success ? "找不到虚拟机的当前配置。" : settings.Error, settings.Code, settings.ErrorSource, settings.Exception, settings.ErrorKind);

        var components = await WmiApi.QueryAsync(
            $"ASSOCIATORS OF {{{settings.Data}}} WHERE ResultClass = {ComponentClass}",
            row => row["HostOnlyItems"] as string[] ?? []).ConfigureAwait(false);
        if (!components.HasData || components.Data.Count != 1)
            return ApiResponse<IReadOnlyDictionary<string, string>>.Fail(
                components.Success ? "未找到唯一的虚拟机 KVP 配置。" : components.Error, components.Code, components.ErrorSource, components.Exception, components.ErrorKind);
        try
        {
            return ApiResponse<IReadOnlyDictionary<string, string>>.Ok(ParseItems(components.Data[0]));
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or ArgumentException)
        {
            return ApiResponse<IReadOnlyDictionary<string, string>>.Fail(ex.Message, exception: ex);
        }
    }

    /// <summary>新增或修改一个 HostOnlyItems 键，等待 VMMS 作业结束并回读确认。</summary>
    /// <param name="vmId">目标虚拟机的标识。</param>
    /// <param name="key">非空键名，区分大小写；使用自己的名称前缀避免与其他程序冲突。</param>
    /// <param name="value">要保存的非 null 字符串，可以为空；JSON 等内容由调用方序列化。</param>
    /// <returns>指定键已保存且回读一致时成功。权限、容量、数据格式和并发修改等错误返回失败，不回退到其他存储。</returns>
    /// <remarks>按键新增或覆盖。同一键的并发修改以系统接受的写入为准；失败后重新读取。容量上限由 VMMS 校验。</remarks>
    /// <editentry label="写入自定义数据" category="自定义数据"/>
    public static Task<ApiResponse> SetAsync(Guid vmId, string key, string value)
        => key == GpuDriversKey ? VmSettingsWriteLock.RunAsync(vmId, () => WriteAsync(vmId, key, value, false))
            : WriteAsync(vmId, key, value, false);

    /// <summary>删除指定 HostOnlyItems 键；键已不存在时直接成功。</summary>
    /// <param name="vmId">目标虚拟机的标识。</param>
    /// <param name="key">要删除的非空键名，区分大小写。</param>
    /// <returns>指定键不存在时成功；查询或删除失败返回失败，不清理其他键。</returns>
    /// <editentry label="删除自定义数据" category="自定义数据"/>
    public static Task<ApiResponse> RemoveAsync(Guid vmId, string key)
        => key == GpuDriversKey ? VmSettingsWriteLock.RunAsync(vmId, () => WriteAsync(vmId, key, string.Empty, true))
            : WriteAsync(vmId, key, string.Empty, true);

    internal static Dictionary<string, string> ParseGpuDriversMap(IReadOnlyDictionary<string, string>? metadata)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadata == null || !metadata.TryGetValue(GpuDriversKey, out string? raw)) return map;
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException(GpuDriversKey);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            string key = NormalizeGpuDriverKey(entry.Name);
            if (key.Length == 0 || entry.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(entry.Value.GetString()) || !map.TryAdd(key, entry.Value.GetString()!.Trim()))
                throw new InvalidDataException("无效或重复的 GPU 驱动版本记录。");
        }
        return map;
    }

    internal static string NormalizeGpuDriverKey(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return string.Empty;
        string normalized = instanceId.Trim().ToUpperInvariant();
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized[4..];
        int suffix = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (suffix >= 0) normalized = normalized[..suffix];
        return normalized.Replace('\\', '#').Replace("#", string.Empty, StringComparison.Ordinal);
    }

    internal static Task<ApiResponse> UpsertGpuDriverVersionsAsync(Guid vmId, IEnumerable<(string InstanceId, string DriverVersion)> updates)
    {
        var snapshot = updates.ToArray();
        return VmSettingsWriteLock.RunAsync(vmId, () => UpsertUnderLockAsync(vmId, snapshot));
    }

    private static async Task<ApiResponse> UpsertUnderLockAsync(Guid vmId, (string InstanceId, string DriverVersion)[] updates)
    {
        var current = await ReadAsync(vmId).ConfigureAwait(false);
        if (!current.HasData) return ApiResponse.Fail(current.Error, current.Code, current.ErrorSource, current.Exception, current.ErrorKind);
        var map = ParseGpuDriversMap(current.Data);
        bool changed = false;
        foreach (var (instanceId, driverVersion) in updates)
        {
            string key = NormalizeGpuDriverKey(instanceId);
            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(driverVersion)) continue;
            string cleanVer = driverVersion.Trim();
            if (!map.TryGetValue(key, out string? prev) || !string.Equals(prev, cleanVer, StringComparison.OrdinalIgnoreCase))
            {
                map[key] = cleanVer;
                changed = true;
            }
        }
        if (!changed) return ApiResponse.Ok();
        return await WriteAsync(vmId, GpuDriversKey, JsonSerializer.Serialize(map, CoreJson.Default.DictionaryStringString), false).ConfigureAwait(false);
    }

    internal static async Task<ApiResponse> RemoveGpuDriverVersionUnderLockAsync(Guid vmId, string instanceId)
    {
        HostResourceLock.AssertHeld(VmSettingsWriteLock.Resource(vmId));
        string key = NormalizeGpuDriverKey(instanceId);
        if (string.IsNullOrEmpty(key)) return ApiResponse.Ok();
        var current = await ReadAsync(vmId).ConfigureAwait(false);
        if (!current.HasData) return ApiResponse.Fail(current.Error, current.Code, current.ErrorSource, current.Exception, current.ErrorKind);
        if (!current.Data.ContainsKey(GpuDriversKey)) return ApiResponse.Ok();
        var map = ParseGpuDriversMap(current.Data);
        if (!map.Remove(key)) return ApiResponse.Ok();
        return map.Count == 0
            ? await WriteAsync(vmId, GpuDriversKey, string.Empty, true).ConfigureAwait(false)
            : await WriteAsync(vmId, GpuDriversKey, JsonSerializer.Serialize(map, CoreJson.Default.DictionaryStringString), false).ConfigureAwait(false);
    }

    private static async Task<ApiResponse> WriteAsync(Guid vmId, string key, string value, bool remove)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。");
        string item;
        try { item = SerializeItem(key, value); }
        catch (Exception ex) when (ex is ArgumentException or XmlException)
        { return ApiResponse.Fail(ex.Message, exception: ex); }

        var current = await ReadAsync(vmId).ConfigureAwait(false);
        if (!current.HasData) return ApiResponse.Fail(current.Error, current.Code, current.ErrorSource, current.Exception, current.ErrorKind);
        bool exists = current.Data.TryGetValue(key, out string? previous);
        if (remove ? !exists : exists && string.Equals(previous, value, StringComparison.Ordinal))
            return ApiResponse.Ok();

        var target = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'",
            row => row.Path.Path).ConfigureAwait(false);
        if (!target.HasData || string.IsNullOrWhiteSpace(target.Data))
            return ApiResponse.Fail(target.Success ? "找不到虚拟机。" : target.Error, target.Code, target.ErrorSource, target.Exception, target.ErrorKind);

        string method = remove ? "RemoveKvpItems" : exists ? "ModifyKvpItems" : "AddKvpItems";
        var result = await WmiApi.InvokeAsync(
            "SELECT * FROM Msvm_VirtualSystemManagementService", method,
            parameters =>
            {
                parameters["TargetSystem"] = target.Data;
                parameters["DataItems"] = new[] { item };
            }).ConfigureAwait(false);
        if (!result.Success) return result;

        var saved = await ReadAsync(vmId).ConfigureAwait(false);
        if (!saved.HasData) return ApiResponse.Fail(saved.Error, saved.Code, saved.ErrorSource, saved.Exception, ApiErrorKind.OutcomeUnknown);
        bool remains = saved.Data.TryGetValue(key, out string? actual);
        return (remove ? !remains : remains && string.Equals(actual, value, StringComparison.Ordinal))
            ? ApiResponse.Ok()
            : ApiResponse.Fail("VMMS 提交后回读的元数据与请求不一致，请重新读取虚拟机配置。", kind: ApiErrorKind.Conflict);
    }

    // 列表查询一次读取全部组件，由当前已实现配置的 InstanceID 选择对应组件，排除快照配置。
    internal static Task<ApiResponse<List<(string Id, string[] Items)>>> ReadAllAsync()
        => WmiApi.QueryAsync(
            $"SELECT InstanceID, HostOnlyItems FROM {ComponentClass}",
            row => (Id: row["InstanceID"]?.ToString() ?? string.Empty,
                    Items: row["HostOnlyItems"] as string[] ?? []));

    internal static IReadOnlyDictionary<string, string> ParseItems(IEnumerable<string> items)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string item in items)
        {
            using var text = new StringReader(item);
            // WMI 的 CIM XML 会在 VALUE 中返回原始 CR/CRLF；按普通 XML 归一化会改变键和值。
            using var reader = new XmlTextReader(text)
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                Normalization = false
            };
            XElement instance = XElement.Load(reader);
            if (instance.Name != "INSTANCE" || (string?)instance.Attribute("CLASSNAME") != ItemClass)
                throw new InvalidDataException("HostOnlyItems 中存在无法识别的数据实例。");
            var properties = instance.Elements("PROPERTY").ToDictionary(
                property => (string?)property.Attribute("NAME") ?? string.Empty,
                property => property.Element("VALUE")?.Value, StringComparer.OrdinalIgnoreCase);
            if (!properties.TryGetValue("Source", out string? source)
                || !ushort.TryParse(source, NumberStyles.None, CultureInfo.InvariantCulture, out ushort sourceValue)
                || sourceValue != 4
                || !properties.TryGetValue("Name", out string? key) || string.IsNullOrEmpty(key)
                || !properties.TryGetValue("Data", out string? value) || value is null)
                throw new InvalidDataException("HostOnlyItems 的键、值或 Source 无效。");
            XmlConvert.VerifyXmlChars(key);
            XmlConvert.VerifyXmlChars(value);
            if (!result.TryAdd(key, value))
                throw new InvalidDataException("HostOnlyItems 中存在重复键。");
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    internal static string SerializeItem(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        XmlConvert.VerifyXmlChars(key);
        XmlConvert.VerifyXmlChars(value);
        var instance = new XElement("INSTANCE", new XAttribute("CLASSNAME", ItemClass),
            Property("Name", "string", key), Property("Data", "string", value),
            Property("Source", "uint16", "4"));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.Entitize
        }))
            instance.WriteTo(writer);
        return output.ToString();

        static XElement Property(string name, string type, string contents) => new("PROPERTY",
            new XAttribute("NAME", name), new XAttribute("TYPE", type), new XElement("VALUE", contents));
    }
}
