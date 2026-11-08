using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.UI.Pages.VirtualMachines;

/// <summary>用于存储设置页的显示状态，介质配置由 Core 模型提供。</summary>
public sealed partial class VmStorageItemViewModel : ObservableObject, IDisposable
{
    public VmStorageItemViewModel(VmStorageAttachment model)
    {
        Model = model;
        Model.PropertyChanged += ModelPropertyChanged;
    }

    public VmStorageAttachment Model { get; }
    public string DriveType => Model.DriveType;
    public string DiskType => Model.DiskType;
    public string PathOrDiskNumber => Model.PathOrDiskNumber;
    public int ControllerLocation => Model.ControllerLocation;
    public string ControllerType => Model.ControllerType;
    public int ControllerNumber => Model.ControllerNumber;
    public int DiskNumber => Model.DiskNumber;
    public string DiskModel => Model.DiskModel;
    public bool IsPassthroughStale => Model.IsPassthroughStale;
    public double DiskSizeGiB { get => Model.DiskSizeGiB; set => Model.DiskSizeGiB = value; }
    public string SerialNumber => Model.SerialNumber;
    public bool HasIsoMedia => Model.HasIsoMedia;

    [ObservableProperty] private bool _isOptimizing;

    public string DisplayName
    {
        get
        {
            if (DiskType == "Physical" && !string.IsNullOrEmpty(DiskModel)) return DiskModel;
            if (DiskType == "Virtual" && !string.IsNullOrEmpty(PathOrDiskNumber))
            {
                try { return System.IO.Path.GetFileName(PathOrDiskNumber); }
                catch (ArgumentException) { return Properties.Resources.Model_Drive_VirtualDisk; }
            }
            return DriveType == "HardDisk" ? Properties.Resources.Model_Drive_VirtualHardDisk : Properties.Resources.Model_Drive_OpticalDrive;
        }
    }

    public string SourceTypeDisplayName => DiskType == "Physical" ? Properties.Resources.Model_Drive_SourcePhysical : Properties.Resources.Model_Drive_SourceVirtual;
    public string Icon => DriveType == "HardDisk" ? "\uEC59" : "\uE958";
    public string SizeDisplay => DiskSizeGiB <= 0 ? Properties.Resources.Common_Unknown
        : DiskSizeGiB < 1.0 / 1024 ? $"{DiskSizeGiB * 1048576:N0} KB"
        : DiskSizeGiB < 1 ? $"{DiskSizeGiB * 1024:N1} MB"
        : $"{DiskSizeGiB:N1} GB";

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SourceTypeDisplayName));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(SizeDisplay));
    }

    public void Dispose() => Model.PropertyChanged -= ModelPropertyChanged;
}
