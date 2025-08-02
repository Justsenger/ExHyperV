// /ViewModels/AssignedGpuViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.ViewModels
{
    public partial class AssignedGpuViewModel : ObservableObject
    {
        public string AdapterId { get; set; }
        public string InstancePath { get; set; }

        // 这些信息是从它的“父”GPU那里获取的
        public string ParentGpuName { get; set; }
        public string ParentGpuVendor { get; set; }
        public string ParentGpuManu { get; set; }
    }
}