using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels
{
    public partial class HostMemoryViewModel : ObservableObject
    {
        private readonly MemoryInfo _model;

        public HostMemoryViewModel(MemoryInfo model)
        {
            _model = model;
        }

        public string BankLabel => _model.BankLabel;
        public string DeviceLocator => _model.DeviceLocator;
        public string Manufacturer => _model.Manufacturer;
        public string PartNumber => _model.PartNumber;
        public string Capacity => _model.Capacity;
        public string DeclaredSpeed => _model.DeclaredSpeed;
        public string ConfiguredSpeed => _model.ConfiguredSpeed;
        public string IsEcc => _model.IsEcc;
        public string MemoryType => _model.MemoryType;
        public string SerialNumber => _model.SerialNumber;

        // 恢复为显示单个内存条信息的 DisplayName
        public string DisplayName => $"{Manufacturer} {PartNumber} ({Capacity})";
    }
}