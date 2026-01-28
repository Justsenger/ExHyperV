using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachineViewModel : ObservableObject
    {
        public VmInstanceInfo Model { get; }

        public string Name => Model.Name;

        public ObservableCollection<AssignedGpuViewModel> AssignedGpus { get; } = new();

        public VirtualMachineViewModel(VmInstanceInfo model, List<HostGpuViewModel> hostGpus)
        {
            Model = model;

            // 根据 Model.GPUs 字典和 hostGpus 列表创建 AssignedGpuViewModel
            if (Model.GPUs != null && hostGpus != null)
            {
                foreach (var kvp in Model.GPUs)
                {
                    string adapterId = kvp.Key;
                    string instancePath = kvp.Value;

                    // 尝试从 hostGpus 中找到匹配的 GPU
                    var matchingGpu = hostGpus.FirstOrDefault(gpu =>
                        !string.IsNullOrEmpty(gpu.Pname) &&
                        (gpu.Pname == instancePath || 
                         NormalizePath(gpu.Pname) == NormalizePath(instancePath)));

                    if (matchingGpu != null)
                    {
                        AssignedGpus.Add(new AssignedGpuViewModel
                        {
                            AdapterId = adapterId,
                            InstancePath = instancePath,
                            ParentGpuName = matchingGpu.Name,
                            ParentGpuVendor = matchingGpu.Vendor,
                            ParentGpuManu = matchingGpu.Vendor  // 统一使用 Vendor 以保持一致性
                        });
                    }
                    else
                    {
                        // 如果找不到匹配的 GPU，仍然添加，但使用默认值
                        AssignedGpus.Add(new AssignedGpuViewModel
                        {
                            AdapterId = adapterId,
                            InstancePath = instancePath,
                            ParentGpuName = "未知 GPU",
                            ParentGpuVendor = "未知",
                            ParentGpuManu = "未知"
                        });
                    }
                }
            }
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.Replace('\\', '#').ToUpper();
        }
    }
}
