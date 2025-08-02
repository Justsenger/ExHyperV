// /ViewModels/VirtualMachineViewModel.cs
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using Wpf.Ui.Controls;
using ExHyperV.Tools;      // <<<--- 确保有这个 using


namespace ExHyperV.ViewModels
{
    public partial class VirtualMachineViewModel : ObservableObject
    {
        public VMInfo Model { get; }
        public FontIcon VmIcon { get; }

        public ObservableCollection<AssignedGpuViewModel> AssignedGpus { get; } = new();

        public VirtualMachineViewModel(VMInfo model, System.Collections.Generic.IEnumerable<HostGpuViewModel> allHostGpus)
        {
            Model = model;

            VmIcon = Utils.FontIcon(24, "\xE7F4");


            // 这是关键：在这里处理数据，将字典转换为包含丰富信息的对象列表
            foreach (var assignedGpu in model.GPUs)
            {
                // 从所有主机GPU中，找到这个分区的“父亲”
                var parentGpu = allHostGpus.FirstOrDefault(h => h.Pname == assignedGpu.Value);

                AssignedGpus.Add(new AssignedGpuViewModel
                {
                    AdapterId = assignedGpu.Key,
                    InstancePath = assignedGpu.Value,
                    ParentGpuName = parentGpu?.Name ?? "Unknown GPU",
                    ParentGpuVendor = parentGpu?.Vendor ?? "Unknown",
                    ParentGpuManu = parentGpu?.Manu ?? "Unknown"
                });
            }
        }

        public string Name => Model.Name;

        // 计算属性，用于在XAML中判断是否应该默认展开
        public bool HasAssignedGpus => AssignedGpus.Any();
        public string VmIconGlyph => "\xE7F4";
    }
}