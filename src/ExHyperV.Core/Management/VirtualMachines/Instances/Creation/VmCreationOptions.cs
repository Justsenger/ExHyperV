namespace ExHyperV.Services;

/// <summary>创建前可查询的宿主目录、配置版本、隔离能力和交换机清单；具体选项由调用方决定。</summary>
/// <param name="DefaultVmPath">宿主默认虚拟机配置根目录。</param>
/// <param name="DefaultVhdPath">宿主默认虚拟硬盘根目录；宿主未设置时可为空。</param>
/// <param name="Versions">宿主支持的配置版本。</param>
/// <param name="IsolationSupported">宿主是否支持硬件隔离。</param>
/// <param name="IsolationTypes">宿主支持的隔离类型。</param>
/// <param name="Switches">可用虚拟交换机 GUID 到显示名称的字典；重名交换机保留各自身份，null 选择由上层表达。</param>
public sealed record VmCreationOptions(string DefaultVmPath, string DefaultVhdPath,
    IReadOnlyList<string> Versions, bool IsolationSupported,
    IReadOnlyList<string> IsolationTypes, IReadOnlyDictionary<Guid, string> Switches);
