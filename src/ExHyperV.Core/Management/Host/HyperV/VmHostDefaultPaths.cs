using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>宿主创建虚拟机和虚拟硬盘时使用的默认目录。</summary>
/// <param name="DefaultVmPath">新虚拟机配置文件的宿主默认存放目录。</param>
/// <param name="DefaultVhdPath">默认虚拟硬盘目录，宿主未提供时为空字符串。</param>
public sealed record VmHostDefaultPaths(string DefaultVmPath, string DefaultVhdPath);
