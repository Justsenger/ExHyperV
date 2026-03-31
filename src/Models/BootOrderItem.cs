using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace ExHyperV.Models
{
    public partial class BootOrderItem : ObservableObject
    {
        [ObservableProperty] private string _name;          // 显示名称
        [ObservableProperty] private string _description;   // 详细描述（路径/地址）
        [ObservableProperty] private string _icon;          // 图标代码
        [ObservableProperty] private bool _isLast;          // 辅助 UI 箭头显示

        public object Reference { get; set; }
        public bool IsGen2 { get; set; }

        // 映射表
        private static readonly Dictionary<int, (string Name, string Icon)> Gen1DeviceMapping = new()
        {
            { 0, ("软盘", "\uE74E") },
            { 1, ("光驱", "\uE958") },
            { 2, ("IDE 硬盘", "\uEDA2") },
            { 3, ("PXE 网络引导", "\uE774") },
            { 4, ("SCSI 硬盘", "\uEDA2") }
        };

        /// <summary>
        /// 快速创建一个第一代虚拟机的引导项
        /// </summary>
        public static BootOrderItem CreateGen1(ushort code)
        {
            var exists = Gen1DeviceMapping.TryGetValue(code, out var info);
            var (name, icon) = exists ? info : ("未知设备", "\uE9CE");

            return new BootOrderItem
            {
                Name = name,
                Icon = icon,
                IsGen2 = false,
                Reference = (int)code
            };
        }
    }
}