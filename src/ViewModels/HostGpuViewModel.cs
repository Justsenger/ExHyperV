using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Properties;

namespace ExHyperV.ViewModels
{
    public partial class HostGpuViewModel : ObservableObject
    {
        public GPUInfo Model { get; }

        public HostGpuViewModel(GPUInfo model, bool isHyperVInstalled)
        {
            Model = model;
            if (!isHyperVInstalled)
            {
                GpuPartitionStatusText = Resources.needhyperv;
            }
            else if (!string.IsNullOrEmpty(Model.Pname) && Model.Pname != Resources.none)
            {
                GpuPartitionStatusText = Resources.support;
            }
            else
            {
                GpuPartitionStatusText = Resources.notsupport;
            }
        }

        public string Name => Model.Name;
        public string Vendor => Model.Vendor;
        public string Manu => Model.Manu;
        public string Ram => Model.Ram;
        public string InstanceId => Model.InstanceId;
        public string DriverVersion => Model.DriverVersion;
        public string Pname => Model.Pname;
        public string GpuPartitionStatusText { get; }

        public string RamDisplay
        {
            get
            {
                if (long.TryParse(Model.Ram, out long ramBytes))
                {
                    // 摩尔线程的特殊处理逻辑
                    if (Model.Manu.Contains("Moore"))
                    {
                        return $"{ramBytes / 1024} MB";
                    }
                    // 标准处理逻辑
                    return $"{ramBytes / (1024 * 1024)} MB";
                }
                return "N/A"; // 如果无法解析，则显示 N/A
            }
        }
        public bool IsPartitionable => !string.IsNullOrEmpty(Model.Pname);
    }
}