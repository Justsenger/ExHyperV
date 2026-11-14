using ExHyperV.Services;

namespace ExHyperV.Models
{
    /// <summary>具有 DDA 位置路径的 PCIe 设备及其当前归属。</summary>
    public partial class PcieDeviceInfo
    {
        /// <summary>PCIe 设备的友好名称。</summary>
        /// <fielddoc>
        /// <meaning>PCIe 设备的友好名称。</meaning>
        /// </fielddoc>
        public string FriendlyName { get; init; } = string.Empty;
        /// <summary>Windows 驱动提供的 PnP 设备类别，用于设备类型判断。</summary>
        /// <fielddoc>
        /// <meaning>Windows 驱动提供的 PnP 设备类别，用于设备类型判断。</meaning>
        /// <values kind="format">设备类别文本，例如 Display、Net、USB。</values>
        /// </fielddoc>
        public string ClassType { get; init; } = string.Empty;
        /// <summary>用于显示的设备类别；系统设备可附带其非 PCI 子设备的类别。</summary>
        /// <fielddoc>
        /// <meaning>用于显示的设备类别；系统设备可附带其非 PCI 子设备的类别。</meaning>
        /// </fielddoc>
        public string DisplayClassType { get; init; } = string.Empty;
        /// <summary>PCIe 设备的 PnP 实例标识，用于核对设备身份。</summary>
        /// <fielddoc>
        /// <meaning>PCIe 设备的 PnP 实例标识，用于核对设备身份。</meaning>
        /// </fielddoc>
        public string InstanceId { get; init; } = string.Empty;
        /// <summary>DDA 分配使用的 PCIe 位置路径。操作时与同次查询的 InstanceId 一起传入，以匹配位置上的设备。</summary>
        /// <fielddoc>
        /// <meaning>DDA 分配使用的 PCIe 位置路径。操作时与同次查询的 InstanceId 一起传入，以匹配位置上的设备。</meaning>
        /// </fielddoc>
        public string Path { get; init; } = string.Empty;
        /// <summary>PCIe 设备制造商名称。</summary>
        /// <fielddoc>
        /// <meaning>PCIe 设备制造商名称。</meaning>
        /// </fielddoc>
        public string Vendor { get; init; } = string.Empty;

        /// <summary>设备当前归属的宿主、虚拟机或未分配状态。实际转移通过 HostPcieService 执行。</summary>
        public PcieAssignmentTarget Assignment { get => _assignment; set => _assignment = value; }
        private PcieAssignmentTarget _assignment = PcieAssignmentTarget.Unassigned;

        internal string Status
        {
            get => Assignment.ToInternalStatus();
            set => Assignment = PcieAssignmentTarget.FromInternalStatus(value);
        }
    }
}
