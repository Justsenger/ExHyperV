using ExHyperV.UI.Shared.Imaging;
using ExHyperV.Services;
    using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.UI.Pages.Pcie
{
    public partial class DeviceViewModel : ObservableObject
    {
        private readonly PcieDeviceInfo _device;
        public List<AssignmentTarget> AssignmentOptions { get; }
        public DeviceViewModel(PcieDeviceInfo device, IReadOnlyList<PcieAssignmentTarget> virtualMachines)
        {
            _device = device;
            IconGlyph = DeviceIcons.GetGlyph(device.ClassType, device.FriendlyName);
            AssignmentOptions = new List<AssignmentTarget>
            {
                new(Properties.Resources.Host_Physical, PcieAssignmentTarget.Host)
            };
            AssignmentOptions.AddRange(virtualMachines.Select(target => new AssignmentTarget(
                FormatVirtualMachine(target, virtualMachines), target)));
        }
        public string FriendlyName => _device.FriendlyName;
        public string ClassType => _device.ClassType;
        public string DisplayClassType => _device.DisplayClassType;
        public string InstanceId => _device.InstanceId;
        public string Path => _device.Path;
        public string Vendor => _device.Vendor;
        public PcieAssignmentTarget Assignment
        {
            get => _device.Assignment;
            set
            {
                if (SetProperty(_device.Assignment, value, _device, (d, v) => d.Assignment = v))
                    OnPropertyChanged(nameof(StatusDisplay));
            }
        }
        public string StatusDisplay => Assignment.Kind switch
        {
            PcieAssignmentKind.Host => Properties.Resources.Host,
            PcieAssignmentKind.Unassigned => Properties.Resources.Status_Dismounted,
            _ => AssignmentOptions.FirstOrDefault(option => option.Target == Assignment)?.Display
                ?? Assignment.VirtualMachineName ?? Assignment.VirtualMachineId?.ToString("D") ?? string.Empty
        };
        public string IconGlyph { get; }

        private static string FormatVirtualMachine(PcieAssignmentTarget target, IReadOnlyList<PcieAssignmentTarget> targets)
        {
            string name = target.VirtualMachineName ?? target.VirtualMachineId!.Value.ToString("D");
            return targets.Count(item => item.VirtualMachineName == target.VirtualMachineName) > 1
                || name == Properties.Resources.Host_Physical
                ? $"{name} ({target.VirtualMachineId:D})" : name;
        }
    }

    /// <summary>显示名称与稳定分配目标分开，重名不会改变选择的虚拟机。</summary>
    public record AssignmentTarget(string Display, PcieAssignmentTarget Target);
}
