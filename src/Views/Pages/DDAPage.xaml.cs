using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Management.Automation;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = Wpf.Ui.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.Views.Pages;

public partial class DDAPage
{
    public bool refreshlock;

    public DDAPage()
    {
        ViewModel = new DDAPageViewModel();
        DataContext = ViewModel;
        InitializeComponent();
        Task.Run(() => IsServer());
        Task.Run(() => ViewModel.LoadDevicesAsync());
    }

    public DDAPageViewModel ViewModel { get; }


    private async void IsServer()
    {
        var result = Utils.Run("(Get-WmiObject -Class Win32_OperatingSystem).ProductType");
        Dispatcher.Invoke(() =>
        {
            if (result[0].ToString() == "3") Isserver.IsOpen = false;
        });
    }


    private void AssignButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DeviceInfo device)
        {
            var contextMenu = new ContextMenu();

            var hostMenuItem = new MenuItem
            {
                Header = LocalizationHelper.GetString("Host")
            };
            hostMenuItem.Click += (s, args) =>
            {
                var parameter = new DeviceAssignmentParameter { Device = device, Target = "Host" };
                ViewModel.AssignDeviceCommand.Execute(parameter);
            };
            contextMenu.Items.Add(hostMenuItem);

            foreach (var vmName in device.VmNames)
            {
                var vmMenuItem = new MenuItem { Header = vmName };
                var vmNameCopy = vmName;
                vmMenuItem.Click += (s, args) =>
                {
                    var parameter = new DeviceAssignmentParameter { Device = device, Target = vmNameCopy };
                    ViewModel.AssignDeviceCommand.Execute(parameter);
                };
                contextMenu.Items.Add(vmMenuItem);
            }

            button.ContextMenu = contextMenu;
            contextMenu.IsOpen = true;
        }
    }

    public class DDAPageViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<DeviceInfo> _devices;
        private bool _isLoading;

        public DDAPageViewModel()
        {
            Devices = new ObservableCollection<DeviceInfo>();
            RefreshCommand = new RelayCommand(async () => await LoadDevicesAsync(), () => !IsLoading);
            AssignDeviceCommand =
                new RelayCommand<DeviceAssignmentParameter>(async param => await AssignDeviceAsync(param));
        }

        public ObservableCollection<DeviceInfo> Devices
        {
            get => _devices;
            set => SetProperty(ref _devices, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (SetProperty(ref _isLoading, value)) RefreshCommand.RaiseCanExecuteChanged();
            }
        }

        public RelayCommand RefreshCommand { get; }
        public RelayCommand<DeviceAssignmentParameter> AssignDeviceCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public async Task LoadDevicesAsync()
        {
            IsLoading = true;
            try
            {
                var deviceList = new List<DeviceInfo>();
                await GetInfo(deviceList);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    Devices.Clear();
                    foreach (var device in deviceList) Devices.Add(device);
                });
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task GetInfo(List<DeviceInfo> deviceList)
        {
            try
            {
                using (var PowerShellInstance = PowerShell.Create())
                {
                    var vmdevice = new Dictionary<string, string>();
                    var vmNameList = new List<string>();
                    PowerShellInstance.AddScript("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");
                    PowerShellInstance.AddScript("Import-Module PnpDevice");

                    var hypervstatus = Utils.Run("Get-Module -ListAvailable -Name Hyper-V");

                    if (hypervstatus.Count != 0)
                    {
                        var vmdata = Utils.Run(@"Get-VM | Select-Object Name");
                        foreach (var vm in vmdata)
                        {
                            var Name = vm.Members["Name"]?.Value?.ToString();

                            if (!string.IsNullOrEmpty(Name)) vmNameList.Add(Name);

                            var deviceData =
                                Utils.Run(
                                    $@"Get-VMAssignableDevice -VMName '{Name}' | Select-Object InstanceID");

                            if (deviceData != null && deviceData.Count > 0)
                                foreach (var device in deviceData)
                                {
                                    var instanceId = device.Members["InstanceID"]?.Value?.ToString().Substring(4);
                                    if (!string.IsNullOrEmpty(instanceId) && !string.IsNullOrEmpty(Name))
                                        vmdevice[instanceId] = Name;
                                }
                        }
                    }

                    var PCIPData =
                        Utils.Run(
                            "Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCIP\\*' } | Select-Object Class, InstanceId, FriendlyName, Status");
                    if (PCIPData != null && PCIPData.Count > 0)
                        foreach (var PCIP in PCIPData)
                        {
                            var instanceId = PCIP.Members["InstanceId"]?.Value?.ToString().Substring(4);

                            if (!vmdevice.ContainsKey(instanceId) &&
                                PCIP.Members["Status"]?.Value?.ToString() == "OK" &&
                                !string.IsNullOrEmpty(instanceId))

                                vmdevice[instanceId] = LocalizationHelper.GetString("removed");
                        }


                    var scripts = @"
                                $maxRetries = 10
                                $retryIntervalSeconds = 1

                                $pciDevices = Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCI\*' }
                                $allInstanceIds = $pciDevices.InstanceId
                                $pathMap = @{}

                                Get-PnpDeviceProperty -InstanceId $allInstanceIds -KeyName DEVPKEY_Device_LocationPaths -ErrorAction SilentlyContinue |
                                    ForEach-Object {
                                        if ($_.Data -and $_.Data.Count -gt 0) {
                                            $pathMap[$_.InstanceId] = $_.Data[0]
                                        }
                                    }

                                $idsNeedingPath = $allInstanceIds | Where-Object { -not $pathMap.ContainsKey($_) }
                                $attemptCount = 0

                                while (($idsNeedingPath.Count -gt 0) -and ($attemptCount -lt $maxRetries)) {
                                    $attemptCount++

                                    Get-PnpDeviceProperty -InstanceId $idsNeedingPath -KeyName DEVPKEY_Device_LocationPaths -ErrorAction SilentlyContinue |
                                        ForEach-Object {
                                            if ($_.Data -and $_.Data.Count -gt 0) {
                                                $pathMap[$_.InstanceId] = $_.Data[0]
                                            }
                                        }

                                    $idsNeedingPath = $allInstanceIds | Where-Object { -not $pathMap.ContainsKey($_) }

                                    if (($idsNeedingPath.Count -gt 0) -and ($attemptCount -lt $maxRetries)) {
                                        Start-Sleep -Seconds $retryIntervalSeconds
                                    }
                                }

                                $pciDevices |
                                    Select-Object Class, InstanceId, FriendlyName, Status, Service |
                                    ForEach-Object {
                                        $_ | Add-Member -NotePropertyName 'Path' -NotePropertyValue $pathMap[$_.InstanceId] -Force
                                        $_
                                    }";

                    var Pcidata = Utils.Run(scripts);
                    var sortedResults = Pcidata
                        .Where(result => result != null)
                        .OrderBy(result => result.Members["Service"]?.Value?.ToString()[0])
                        .ToList();
                    foreach (var result in sortedResults)
                    {
                        var friendlyName = result.Members["FriendlyName"]?.Value?.ToString();
                        var status = result.Members["Status"]?.Value?.ToString();
                        var classType = result.Members["Class"]?.Value?.ToString();
                        var instanceId = result.Members["InstanceId"]?.Value?.ToString();
                        var path = result.Members["Path"]?.Value?.ToString();
                        var service = result.Members["Service"]?.Value?.ToString();
                        if (service == "pci" || service == null) continue;

                        if (status == "Unknown" && !string.IsNullOrEmpty(instanceId) && instanceId.Length > 3)
                        {
                            if (vmdevice.ContainsKey(instanceId
                                    .Substring(3)))
                                status = vmdevice[instanceId.Substring(3)];
                            else
                                continue;
                        }
                        else
                        {
                            status = LocalizationHelper.GetString("Host");
                        }

                        deviceList.Add(new DeviceInfo(friendlyName, status, classType, instanceId, vmNameList,
                            path));
                    }
                }
            }
            catch (Exception ex)
            {
                var errorText = LocalizationHelper.GetString("error");
                Application.Current.Dispatcher.Invoke(() => { MessageBox.Show($"{errorText}: {ex.Message}"); });
            }
        }

        private async Task AssignDeviceAsync(DeviceAssignmentParameter parameter)
        {
            if (parameter?.Device == null) return;

            var device = parameter.Device;
            var targetVm = parameter.Target;

            var progressDialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("setting"),
                CloseButtonText = LocalizationHelper.GetString("wait")
            };

            var progressTextBlock = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            progressDialog.Content = progressTextBlock;
            progressDialog.Closing += (sender, args) => { args.Cancel = true; };

            Application.Current.Dispatcher.Invoke(() =>
            {
                progressDialog.DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs;
                _ = progressDialog.ShowAsync();
            });

            try
            {
                var (commands, messages) = GetDDACommands(targetVm, device.InstanceId, device.Path, device.Status);

                for (var i = 0; i < messages.Length; i++)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        progressTextBlock.Text = LocalizationHelper.GetString(messages[i]);
                    });
                    await Task.Delay(200);

                    if (i < commands.Length)
                    {
                        var result = Utils.Run(commands[i]);
                    }
                }

                device.Status = targetVm;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    progressTextBlock.Text = LocalizationHelper.GetString("operated");
                    progressDialog.CloseButtonText = LocalizationHelper.GetString("OK");
                    progressDialog.Closing += (sender, args) => { args.Cancel = false; };
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    progressTextBlock.Text = $"{LocalizationHelper.GetString("error")}: {ex.Message}";
                    progressDialog.CloseButtonText = LocalizationHelper.GetString("OK");
                    progressDialog.Closing += (sender, args) => { args.Cancel = false; };
                });
            }
        }

        private static (string[] commands, string[] messages) GetDDACommands(string vmName, string instanceId,
            string path, string currentStatus)
        {
            string[] commands;
            string[] messages;

            var removedText = LocalizationHelper.GetString("removed");
            var hostText = LocalizationHelper.GetString("Host");

            if (currentStatus == removedText && vmName == hostText)
            {
                commands = new[] { $"Mount-VMHostAssignableDevice -LocationPath '{path}'" };
                messages = new[] { "mounting" };
            }
            else if (currentStatus == removedText && vmName != hostText)
            {
                commands = new[] { $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{vmName}'" };
                messages = new[] { "mounting" };
            }
            else if (currentStatus == hostText)
            {
                commands = new[]
                {
                    $"Set-VM -Name '{vmName}' -AutomaticStopAction TurnOff",
                    $"Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}'",
                    $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                    $"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false",
                    $"Dismount-VMHostAssignableDevice -Force -LocationPath '{path}'",
                    $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{vmName}'"
                };
                messages = new[] { "string5", "cpucache", "getpath", "Disabledevice", "Dismountdevice", "mounting" };
            }
            else if (vmName != hostText && currentStatus != hostText)
            {
                commands = new[]
                {
                    $"Set-VM -Name '{vmName}' -AutomaticStopAction TurnOff",
                    $"Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}'",
                    $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                    $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{currentStatus}'",
                    $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{vmName}'"
                };
                messages = new[] { "string5", "cpucache", "getpath", "Dismountdevice", "mounting" };
            }
            else if (vmName == hostText && currentStatus != hostText)
            {
                commands = new[]
                {
                    $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                    $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{currentStatus}'",
                    $"Mount-VMHostAssignableDevice -LocationPath '{path}'",
                    $"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false"
                };
                messages = new[] { "getpath", "Dismountdevice", "mounting", "enabling" };
            }
            else
            {
                commands = Array.Empty<string>();
                messages = Array.Empty<string>();
            }

            return (commands, messages);
        }

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

    public class RelayCommand : ICommand
    {
        private readonly Func<bool>? _canExecute;
        private readonly Func<Task> _executeAsync;

        public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke() ?? true;
        }

        public async void Execute(object? parameter)
        {
            if (CanExecute(parameter)) await _executeAsync();
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Func<T, bool>? _canExecute;
        private readonly Func<T, Task> _executeAsync;

        public RelayCommand(Func<T, Task> executeAsync, Func<T, bool>? canExecute = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            if (parameter is T typedParameter)
                return _canExecute?.Invoke(typedParameter) ?? true;
            return parameter == null && typeof(T).IsClass;
        }

        public async void Execute(object? parameter)
        {
            if (CanExecute(parameter) && parameter is T typedParameter) await _executeAsync(typedParameter);
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}