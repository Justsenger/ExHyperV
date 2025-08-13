using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.ViewModels
{
    public partial class AssignedGpuViewModel : ObservableObject
    {
        public string AdapterId { get; set; }
        public string InstancePath { get; set; }
        public string ParentGpuName { get; set; }
        public string ParentGpuVendor { get; set; }
        public string ParentGpuManu { get; set; }
    }
}