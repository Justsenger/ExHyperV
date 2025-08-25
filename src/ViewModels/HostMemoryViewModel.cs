using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using System;

namespace ExHyperV.ViewModels
{
    public partial class HostMemoryViewModel : ObservableObject
    {
        private readonly MemoryInfo _model;
        private readonly bool _isVirtualEnvironment;

        public HostMemoryViewModel(MemoryInfo model)
        {
            _model = model;
            // 通过检查制造商是否为"Microsoft"来判断是否在虚拟环境中
            _isVirtualEnvironment = _model.Manufacturer.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);
        }

        // --- 属性已根据新规则更新 ---

        public string Manufacturer => _model.Manufacturer;

        public string PartNumber
        {
            get
            {
                // 如果是虚拟环境，强制显示 "Hyper-V Memory"
                if (_isVirtualEnvironment)
                {
                    return "Hyper-V Memory";
                }

                if (string.IsNullOrEmpty(_model.PartNumber) || _model.PartNumber.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    return "N/A";
                }
                return _model.PartNumber;
            }
        }

        public string MemoryType
        {
            get
            {
                // 如果是虚拟环境，强制显示 "N/A"
                if (_isVirtualEnvironment)
                {
                    return "N/A";
                }

                // PowerShell脚本在无法识别类型时会返回 "Unknown (...)"
                if (string.IsNullOrEmpty(_model.MemoryType) || _model.MemoryType.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    return "N/A";
                }
                return _model.MemoryType;
            }
        }

        public string DeclaredSpeed
        {
            get
            {
                // 如果是虚拟环境或速度值为 "0 MT/s" 或只有单位，则显示 N/A
                if (_isVirtualEnvironment || string.IsNullOrEmpty(_model.DeclaredSpeed) || _model.DeclaredSpeed.StartsWith("0 ") || _model.DeclaredSpeed.Trim().Equals("MT/s", StringComparison.OrdinalIgnoreCase))
                {
                    return "N/A";
                }
                return _model.DeclaredSpeed;
            }
        }

        public string ConfiguredSpeed
        {
            get
            {
                if (_isVirtualEnvironment || string.IsNullOrEmpty(_model.ConfiguredSpeed) || _model.ConfiguredSpeed.StartsWith("0 ") || _model.ConfiguredSpeed.Trim().Equals("MT/s", StringComparison.OrdinalIgnoreCase))
                {
                    return "N/A";
                }
                return _model.ConfiguredSpeed;
            }
        }

        public string BankLabel
        {
            get
            {
                if (string.IsNullOrEmpty(_model.BankLabel) || _model.BankLabel.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    return "N/A";
                }
                return _model.BankLabel;
            }
        }

        // --- 以下属性保持不变 ---

        public string Capacity => _model.Capacity;
        public string DeviceLocator => _model.DeviceLocator;
        public string IsEcc => _model.IsEcc;
        public string SerialNumber => _model.SerialNumber;

        public string DisplayName => $"{Manufacturer} {PartNumber} ({Capacity})";
    }
}