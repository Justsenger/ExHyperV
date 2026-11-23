namespace ExHyperV.Models;

/// <summary>虚拟机的虚拟串口（COM 1 / COM 2）配置。</summary>
public sealed class VmSerialPort
{
    /// <summary>串口编号（1 为 COM 1，2 为 COM 2）。</summary>
    /// <fielddoc>
    /// <meaning>虚拟串口的固定端口号。</meaning>
    /// <unit>端口编号</unit>
    /// <values kind="options"><c>1</c>：COM 1；<c>2</c>：COM 2。</values>
    /// <source>Msvm_SerialPortSettingData，根据 InstanceID 与 ElementName 解析。</source>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public int PortNumber { get; init; }

    /// <summary>串口显示名称（如 COM 1、COM 2）。</summary>
    /// <fielddoc>
    /// <meaning>虚拟串口的显示名称。</meaning>
    /// <source>Msvm_SerialPortSettingData，读取或派生 ElementName。</source>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public string Name { get; init; } = string.Empty;

    /// <summary>宿主机命名管道路径；为空表示未连接。</summary>
    /// <fielddoc>
    /// <meaning>虚拟串口映射到的宿主机命名管道路径；开启后由 Hyper-V 在宿主机创建命名管道，供 PuTTY、WinDbg 等工具连接，关闭开关即断开连接。</meaning>
    /// <values kind="format">Windows 命名管道路径，例如 <c>\\.\pipe\my-vm-com1</c>；为空字符串表示未连接。</values>
    /// <absence>串口未连接任何命名管道时为空字符串。</absence>
    /// <source>Msvm_SerialPortSettingData，读取 Connection 数组的首个非空条目。</source>
    /// <modification kind="supported" input="pipePath" submission="单串口提交">支持短管道名自动补全为本地管道路径，传空字符串或 null 表示断开连接。<see cref="M:ExHyperV.Services.VmSerialPortService.SetPipePathAsync(System.Guid,System.Int32,System.String)"/></modification>
    /// </fielddoc>
    public string PipePath { get; set; } = string.Empty;

    /// <summary>是否启用 Windows 内核调试模式。</summary>
    /// <fielddoc>
    /// <meaning>是否为 Windows 内核调试器（WinDbg）优化串口传输性能；普通串口控制台通信无需开启。</meaning>
    /// <values kind="options"><c>true</c>：已启用内核调试优化；<c>false</c>：普通串口模式。</values>
    /// <applicability>仅当宿主 WMI 提供 DebuggerMode 属性时可读写。</applicability>
    /// <source>Msvm_SerialPortSettingData，读取或派生 DebuggerMode。</source>
    /// <modification kind="supported" input="enabled" submission="单串口提交"><see cref="M:ExHyperV.Services.VmSerialPortService.SetDebuggerModeAsync(System.Guid,System.Int32,System.Boolean)"/></modification>
    /// </fielddoc>
    public bool DebuggerMode { get; set; }

    /// <summary>宿主 WMI 类是否提供内核调试模式开关。</summary>
    /// <fielddoc>
    /// <meaning>当前宿主的 Msvm_SerialPortSettingData 是否存在 DebuggerMode 属性。</meaning>
    /// <values kind="options"><c>true</c>：支持修改内核调试模式；<c>false</c>：宿主不支持。</values>
    /// <source>Msvm_SerialPortSettingData 的属性探测结果。</source>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public bool DebuggerModeAvailable { get; init; }

    /// <summary>串口是否已配置命名管道连接。</summary>
    public bool IsConnected => !string.IsNullOrWhiteSpace(PipePath);

    internal string WmiInstanceId { get; init; } = string.Empty;
}
