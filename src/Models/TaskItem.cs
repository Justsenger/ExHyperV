using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    public enum TaskStatus { Pending, Running, Success, Failed, Warning }

    public partial class TaskItem : ObservableObject
    {
        [ObservableProperty] private string _name;          // 任务名称
        [ObservableProperty] private string _description;   // 描述
        [ObservableProperty] private TaskStatus _status = TaskStatus.Pending;
    }
}