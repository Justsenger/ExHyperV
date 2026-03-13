using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    // 定义任务的逻辑类型（用于 switch/case 逻辑判断）
    public enum GpuTaskType
    {
        Prepare,        // 环境准备
        ConfigCheck,    // 检查虚拟机设置
        PowerCheck,     // 电源检查
        Optimization,   // 系统优化
        Assign,         // 分配显卡
        Driver          // 驱动安装
    }

    public enum TaskStatus { Pending, Running, Success, Failed, Warning }

    public partial class TaskItem : ObservableObject
    {
        // 关键：逻辑标识符，这个永远不会变，也不受语言影响
        public GpuTaskType TaskType { get; set; }

        [ObservableProperty] private string _name;          // 显示名称（用于 UI 展示）
        [ObservableProperty] private string _description;   // 详细描述
        [ObservableProperty] private TaskStatus _status = TaskStatus.Pending;
    }
}