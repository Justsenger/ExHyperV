using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>串口的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmSerialPortFields
{
    /// <summary>串口编号：虚拟串口的固定端口号（1 表示 COM 1，2 表示 COM 2）。</summary>
    /// <fielddoc>
    /// <meaning>虚拟串口的固定端口号。</meaning>
    /// <unit>端口编号</unit>
    /// <values kind="options"><c>1</c>：COM 1；<c>2</c>：COM 2。</values>
    /// <source>Msvm_SerialPortSettingData，由 InstanceID 与 ElementName 解析 PortNumber。</source>
    /// <references><see cref="VmSerialPortService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<int> PortNumber = new("Serial.PortNumber", "串口编号", "串口", VmQueryCollection.SerialPorts, "SerialPorts", "PortNumber");

    /// <summary>串口名称：虚拟串口的显示名称（如 COM 1、COM 2）。</summary>
    /// <fielddoc>
    /// <meaning>虚拟串口的显示名称。</meaning>
    /// <source>Msvm_SerialPortSettingData，读取或派生 ElementName。</source>
    /// <references><see cref="VmSerialPortService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("Serial.Name", "串口名称", "串口", VmQueryCollection.SerialPorts, "SerialPorts", "Name");

    /// <summary>命名管道路径：虚拟串口连接的宿主机命名管道路径；为空表示未连接。</summary>
    /// <fielddoc>
    /// <meaning>虚拟串口连接到的宿主机命名管道路径；为空字符串表示未连接。</meaning>
    /// <values kind="format">Windows 命名管道路径，例如 <c>\\.\pipe\my-vm-com1</c>；留空表示未连接。</values>
    /// <absence>串口未连接任何命名管道时为空字符串。</absence>
    /// <source>Msvm_SerialPortSettingData，读取 Connection 数组首项。</source>
    /// <references><see cref="VmSerialPortService"/>。</references>
    /// <modification kind="supported" input="pipePath" submission="单串口提交">传入空字符串或 <c>null</c> 可断开连接。<see cref="M:ExHyperV.Services.VmSerialPortService.SetPipePathAsync(System.Guid,System.Int32,System.String)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> PipePath = new("Serial.PipePath", "命名管道路径", "串口", VmQueryCollection.SerialPorts, "SerialPorts", "PipePath");

    /// <summary>内核调试模式：是否为 Windows 内核调试器（WinDbg）优化串口传输。</summary>
    /// <fielddoc>
    /// <meaning>是否为 Windows 内核调试器（WinDbg）优化虚拟串口数据传输。</meaning>
    /// <values kind="options"><c>true</c>：开启内核调试优化；<c>false</c>：普通串口模式。</values>
    /// <applicability>仅当宿主 Msvm_SerialPortSettingData 提供 DebuggerMode 属性时可用。</applicability>
    /// <source>Msvm_SerialPortSettingData，读取 DebuggerMode。</source>
    /// <references><see cref="VmSerialPortService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单串口提交">需要虚拟机完全关机。<see cref="M:ExHyperV.Services.VmSerialPortService.SetDebuggerModeAsync(System.Guid,System.Int32,System.Boolean)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> DebuggerMode = new("Serial.DebuggerMode", "内核调试模式", "串口", VmQueryCollection.SerialPorts, "SerialPorts", "DebuggerMode");

    internal static readonly VmQueryField[] Items = [PortNumber, Name, PipePath, DebuggerMode];
}
