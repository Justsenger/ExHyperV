using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels
{
    /// <summary>
    /// USB 设备的 item-level ViewModel：包装一份 <see cref="UsbDevice"/> Model，
    /// 并维护当前分配目标（Host 或某个运行中 VM 名）+ 下拉框选项。
    /// </summary>
    public partial class UsbDeviceViewModel : ObservableObject
    {
        // BusId 是硬件标识，通常不会变，保持只读
        public string BusId { get; }

        // 以下属性在手机切换模式（如从 MTP 变 ADB）时会变，需设为可观察
        [ObservableProperty] private string _vidPid;
        [ObservableProperty] private string _description;
        [ObservableProperty] private string _status;

        // 当前分配目标 (如: Properties.Resources.UsbDevice_Host 或 虚拟机名称)
        [ObservableProperty] private string _currentAssignment;

        // 分配选项列表 - 改为 ObservableCollection 以支持动态更新下拉框内容
        public ObservableCollection<string> AssignmentOptions { get; } = new();

        public UsbDeviceViewModel(UsbDevice model, List<string> runningVmNames)
        {
            BusId = model.BusId;
            VidPid = model.VidPid;
            Description = model.Description;
            Status = model.Status;
            _currentAssignment = Properties.Resources.UsbDevice_Host;

            UpdateOptions(runningVmNames);
        }

        // 提供一个方法来安全更新下拉列表
        public void UpdateOptions(List<string> runningVmNames)
        {
            // 记录当前选择，防止刷新时丢失
            var current = CurrentAssignment;

            // 更新列表内容
            AssignmentOptions.Clear();
            AssignmentOptions.Add(Properties.Resources.UsbDevice_Host);
            foreach (var name in runningVmNames)
            {
                AssignmentOptions.Add(name);
            }

            // 恢复选择
            if (AssignmentOptions.Contains(current))
                CurrentAssignment = current;
            else
                CurrentAssignment = Properties.Resources.UsbDevice_Host;
        }
    }
}
