using ExHyperV.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    // 定义任务的逻辑类型（用于 switch/case 逻辑判断）
    public partial class TaskItem : ObservableObject
    {
        // 逻辑标识符，稳定、不随语言变化
        public GpuDeploymentStepKind TaskType { get; set; }

        [ObservableProperty] private string _name = string.Empty;          // 显示名称（用于 UI 展示）
        [ObservableProperty] private string _description = string.Empty;   // 详细描述
        [ObservableProperty] private GpuDeploymentStepStatus _status = GpuDeploymentStepStatus.Pending;
    }
}