using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    // 虚拟机基础信息
    public class VmInfo
    {
        public string Name { get; set; }
        public Guid Id { get; set; }
    }

    // USB 设备原始数据模型
    public class UsbDeviceModel
    {
        public string BusId { get; set; }
        public string VidPid { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
    }

    // USB 设备视图模型 - 修复后的版本
    public partial class UsbDeviceViewModel : ObservableObject
    {
        // BusId 是硬件标识，通常不会变，保持只读
        public string BusId { get; }

        // 以下属性在手机切换模式（如从 MTP 变 ADB）时会变，需设为可观察
        [ObservableProperty] private string _vidPid;
        [ObservableProperty] private string _description;
        [ObservableProperty] private string _status;

        // 当前分配目标 (如: Properties.Resources.UsbDeviceModel_1 或 虚拟机名称)
        [ObservableProperty] private string _currentAssignment;

        // 分配选项列表 - 改为 ObservableCollection 以支持动态更新下拉框内容
        public ObservableCollection<string> AssignmentOptions { get; } = new();

        public UsbDeviceViewModel(UsbDeviceModel model, List<string> runningVmNames)
        {
            BusId = model.BusId;
            VidPid = model.VidPid;
            Description = model.Description;
            Status = model.Status;
            _currentAssignment = Properties.Resources.UsbDeviceModel_1;

            UpdateOptions(runningVmNames);
        }

        // 提供一个方法来安全更新下拉列表
        public void UpdateOptions(List<string> runningVmNames)
        {
            // 记录当前选择，防止刷新时丢失
            var current = CurrentAssignment;

            // 更新列表内容
            AssignmentOptions.Clear();
            AssignmentOptions.Add(Properties.Resources.UsbDeviceModel_1);
            foreach (var name in runningVmNames)
            {
                AssignmentOptions.Add(name);
            }

            // 恢复选择
            if (AssignmentOptions.Contains(current))
                CurrentAssignment = current;
            else
                CurrentAssignment = Properties.Resources.UsbDeviceModel_1;
        }
    }
}