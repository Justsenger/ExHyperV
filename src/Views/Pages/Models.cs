using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ExHyperV.Views.Pages;

public class DeviceInfo : INotifyPropertyChanged
{
    private string _classType;
    private string _friendlyName;
    private string _instanceId;
    private string _path;
    private string _status;
    private List<string> _vmNames;

    // 构造函数
    public DeviceInfo(string friendlyName, string status, string classType, string instanceId, List<string> vmNames,
        string path)
    {
        FriendlyName = friendlyName;
        Status = status;
        ClassType = classType;
        InstanceId = instanceId;
        VmNames = vmNames;
        Path = path;
    }

    public string FriendlyName
    {
        get => _friendlyName;
        set => SetProperty(ref _friendlyName, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string ClassType
    {
        get => _classType;
        set => SetProperty(ref _classType, value);
    }

    public string InstanceId
    {
        get => _instanceId;
        set => SetProperty(ref _instanceId, value);
    }

    public List<string> VmNames
    {
        get => _vmNames;
        set => SetProperty(ref _vmNames, value);
    }

    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class DeviceAssignmentParameter
{
    public DeviceInfo? Device { get; set; }
    public string? Target { get; set; }
}