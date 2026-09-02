using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels
{
    /// <summary>USB 设备及其当前分配目标。</summary>
    public partial class UsbDeviceViewModel : ObservableObject
    {
        public string BusId { get; }

        // 手机切换 USB 模式时，描述和 VID/PID 可能变化。
        [ObservableProperty] private string _vidPid;
        [ObservableProperty] private string _description;

        [ObservableProperty] private string _currentAssignment;

        public ObservableCollection<string> AssignmentOptions { get; } = new();

        public UsbDeviceViewModel(UsbDevice model, List<string> runningVmNames)
        {
            BusId = model.BusId;
            VidPid = model.VidPid;
            Description = model.Description;
            _currentAssignment = Properties.Resources.UsbDevice_Host;

            UpdateOptions(runningVmNames);
        }

        public void UpdateOptions(List<string> runningVmNames)
        {
            var current = CurrentAssignment;

            AssignmentOptions.Clear();
            AssignmentOptions.Add(Properties.Resources.UsbDevice_Host);
            foreach (var name in runningVmNames)
            {
                AssignmentOptions.Add(name);
            }

            if (AssignmentOptions.Contains(current))
                CurrentAssignment = current;
            else
                CurrentAssignment = Properties.Resources.UsbDevice_Host;
        }
    }
}
