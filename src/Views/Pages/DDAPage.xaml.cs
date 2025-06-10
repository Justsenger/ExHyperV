using System.Windows;
using System.Windows.Controls;
using ExHyperV.Models;
using Wpf.Ui.Controls;
using MenuItem = Wpf.Ui.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.Views.Pages;

public partial class DdaPage
{
    private bool _refreshlock;

    public DdaPage()
    {
        InitializeComponent();
        Task.Run(IsServerAsync);
        Task.Run(InitialInfoAsync); // Get device information
    }

    private void DdArefresh(object? sender, RoutedEventArgs? e)
    {
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        if (_refreshlock) return;
        _refreshlock = true;
        ProgressRing.Visibility = Visibility.Visible; //��ʾ������
        Task.Run(InitialInfoAsync); //��ȡ�豸��Ϣ
    }

    private async Task IsServerAsync()
    {
        var result = Utils.Run("(Get-WmiObject -Class Win32_OperatingSystem).ProductType");
        await Dispatcher.InvokeAsync(() =>
        {
            if (result.Count > 0 && result[0].ToString() == "3") Isserver.IsOpen = false; //�������汾���ر���ʾ
        });
    }

    private async Task InitialInfoAsync()
    {
        var deviceList = new List<DeviceInfo>();
        await DeviceInfoAsync(deviceList); //��ȡ����

        await Dispatcher.InvokeAsync(() => //����UI
        {
            ParentPanel.Children.Clear();
            ProgressRing.Visibility = Visibility.Collapsed; //���ؼ�����
            foreach (var device in deviceList) //����UI
            {
                var cardExpander = Utils.CardExpander1();
                cardExpander.Icon = Utils.FontIcon1(device.ClassType, device.FriendlyName);
                Grid.SetRow(cardExpander, ParentPanel.RowDefinitions.Count);
                ParentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Add a new row

                var headerGrid =
                    new Grid(); // Create header Grid layout with two columns: first column takes remaining space, second column for buttons
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var drivername = Utils.CreateHeaderTextBlock(device.FriendlyName);
                Grid.SetColumn(drivername, 0);
                headerGrid.Children.Add(drivername);

                var menu = Utils.DropDownButton1(device.Status); //�Ҳఴť
                Grid.SetColumn(menu, 1); // ���Ӱ�ť���ڶ���

                var contextMenu = new ContextMenu();
                contextMenu.Items.Add(CreateMenuItem(Properties.Resources.Host, menu, device));
                foreach (var vmName in device.VmNames)
                    contextMenu.Items.Add(CreateMenuItem(vmName, menu, device));

                menu.Flyout = contextMenu;
                headerGrid.Children.Add(menu);
                cardExpander.Header = headerGrid;


                var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };

                var grid = new Grid();

                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

                var textData = new TextData[]
                {
                    new(Properties.Resources.kind, 0, 0),
                    new(Properties.Resources.Instanceid, 1, 0),
                    new(Properties.Resources.path, 2, 0),
                    new(device.ClassType, 0, 1),
                    new(device.InstanceId, 1, 1),
                    new(device.Path, 2, 1)
                };

                foreach (var textItem in textData)
                {
                    var textBlock = Utils.CreateGridTextBlock(textItem.Text, textItem.Row, textItem.Column);
                    grid.Children.Add(textBlock);
                }

                contentPanel.Children.Add(grid);
                cardExpander.Content = contentPanel;
                ParentPanel.Children.Add(cardExpander);
            }

            _refreshlock = false;
        });
    }

    private async Task DdAps(DropDownButton menu, ContentDialog dialog, TextBlock contentTextBlock, string vmname,
        string instanceId, string path, string nowname)
    {
        var commandData = DdaCommands(vmname, instanceId, path, nowname); //ͨ����Ԫ���ȡ��Ӧ���������Ϣ��ʾ
        for (var i = 0; i < commandData.Messages.Length; i++)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                contentTextBlock.Text = commandData.Messages[i]); //������ʾ
            await Task.Delay(200); //����һ������ʱ����ʾ����

            var logOutput = await Utils.RunAsyncAsStrings(commandData.Commands[i]); //ִ���������ȡ��־

            if (await ProcessCommandResult(logOutput, contentTextBlock, dialog))
                return; // �˳�ѭ��������������
        }

        await ShowSuccessResult(menu, contentTextBlock, dialog, vmname);
    }


    private static Task DeviceInfoAsync(List<DeviceInfo> deviceList)
    {
        return Task.Run(() =>
        {
            try
            {
                var vmdevice = new Dictionary<string, string>(); // Store VM-related PCIP device list
                var vmNameList = new List<string>(); // Store VM list
                Utils.Run("Set-ExecutionPolicy RemoteSigned -Scope Process -Force"); // Set policy
                Utils.Run("Import-Module PnpDevice");

                // 1. Get VM information and devices that have been assigned, store their names for later retrieval

                //����Ƿ�װhyperv
                var hypervstatus = Utils.Run("Get-Module -ListAvailable -Name Hyper-V");

                if (hypervstatus.Count != 0) //�Ѱ�װ
                {
                    //��ȡ�������Ϣ
                    var vmdata = Utils.Run("Get-VM | Select-Object Name");
                    foreach (var vm in vmdata)
                    {
                        var name = vm.Members["Name"]?.Value?.ToString();

                        if (!string.IsNullOrEmpty(name))
                            vmNameList.Add(name); // Add to list if name is not empty

                        var deviceData =
                            Utils.Run(
                                $"Get-VMAssignableDevice -VMName '{name}' | Select-Object InstanceID"); // Get VM device list

                        if (deviceData.Count == 0) continue;

                        foreach (var device in deviceData)
                        {
                            var instanceIdValue = device.Members["InstanceID"]?.Value?.ToString();
                            if (string.IsNullOrEmpty(instanceIdValue) || instanceIdValue.Length <= 4) continue;
                            var instanceId = instanceIdValue[4..];
                            if (!string.IsNullOrEmpty(instanceId) && !string.IsNullOrEmpty(name))
                                vmdevice[instanceId] = name; // Store InstanceID and VMName as key-value pair
                        }
                    }
                }
                // If HyperV module is not installed, VM information cannot be retrieved, but this doesn't affect hardware retrieval

                // Get PCIP device information
                var pcipData =
                    Utils.Run(
                        "Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCIP\\*' } | Select-Object Class, InstanceId, FriendlyName, Status");
                if (pcipData is { Count: > 0 })
                {
                    var pcipInstances = pcipData
                        .Where(x => x.Members is not null)
                        .Select(pcip => new
                        {
                            Pcip = pcip,
                            InstanceIdValue = pcip.Members["InstanceId"]?.Value?.ToString()
                        })
                        .Where(x => !string.IsNullOrEmpty(x.InstanceIdValue) && x.InstanceIdValue.Length > 4)
                        .Select(x => new
                        {
                            x.Pcip,
                            InstanceId = x.InstanceIdValue![4..]
                        })
                        .Where(x => !string.IsNullOrEmpty(x.InstanceId)
                                    && !vmdevice.ContainsKey(x.InstanceId)
                                    && x.Pcip.Members["Status"]?.Value?.ToString() == "OK");

                    foreach (var instanceId in pcipInstances.Select(item => item.InstanceId)
                                 .Where(id => !string.IsNullOrEmpty(id)))
                        vmdevice[instanceId] = Properties.Resources.removed;
                }

                // ��ȡ PCI �豸��Ϣ����Ҫ���ѯ������ܻ�ȡ�����豸��������Ϣ��
                const string scripts = """

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
                                                                       }
                                       """;

                var pcidata = Utils.Run(scripts);
                var deviceResults = pcidata
                    .Where(x => x.Members is not null) // ���˵�Ϊ�յ�Ԫ��
                    .OrderBy(x => x.Members["Service"]?.Value?.ToString()?[0]) // �����������򣬼�Class �ֶ�����ĸ
                    .Select(result => new
                    {
                        FriendlyName = result.Members["FriendlyName"]?.Value?.ToString(),
                        Status = result.Members["Status"]?.Value?.ToString(),
                        ClassType = result.Members["Class"]?.Value?.ToString(),
                        InstanceId = result.Members["InstanceId"]?.Value?.ToString(),
                        Path = result.Members["Path"]?.Value?.ToString(),
                        Service = result.Members["Service"]?.Value?.ToString()
                    })
                    .Where(x => x.Service is not ("pci" or null)); // �ų������豸

                foreach (var result in deviceResults)
                {
                    var status = result.Status;
                    var instanceId = result.InstanceId;

                    // Devices with Unknown status may be in several states: 1. Removed 2. Assigned to VM 3. Dismounted
                    // Check if assigned to VM
                    if (status == "Unknown" && !string.IsNullOrEmpty(instanceId) && instanceId.Length > 3)
                    {
                        if (!vmdevice.ContainsKey(instanceId[3..]))
                            continue; // If not in VM list, and also dismounted, it means it has been removed

                        status = vmdevice[
                            instanceId[
                                3..]]; // Remove first 3 characters, check if PCI InstanceId exists in stored VM assigned device list
                    }
                    else
                    {
                        status = Properties.Resources.Host; // Otherwise it's on the host system
                    }

                    deviceList.Add(new DeviceInfo(
                        result.FriendlyName ?? string.Empty,
                        status,
                        result.ClassType ?? string.Empty,
                        result.InstanceId ?? string.Empty,
                        vmNameList,
                        result.Path ?? string.Empty)); // Add to device list
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        });
    }

    private static CommandData DdaCommands(string vmname, string instanceId, string path,
        string nowname)
    {
        // �����������Ϣ��Ĭ������
        string[] commands;
        string[] messages;

        if (nowname == Properties.Resources.removed && vmname == Properties.Resources.Host)
        {
            //���豸�۷�����
            commands =
            [
                $"Mount-VMHostAssignableDevice -LocationPath '{path}'"
            ];
            messages =
            [
                Properties.Resources.mounting
            ];
        }
        else if (nowname == Properties.Resources.removed && vmname != Properties.Resources.Host)
        {
            // Dismount device and assign to VM
            commands =
            [
                $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{vmname}'"
            ];
            messages =
            [
                Properties.Resources.mounting
            ];
        }
        // Based on different conditions, return different command information
        else if (nowname == Properties.Resources.Host) // Switch from host to VM
        {
            commands =
            [
                $"Set-VM -Name '{vmname}' -AutomaticStopAction TurnOff", // Set to force shutdown
                $"Set-VM -GuestControlledCacheTypes $true -VMName '{vmname}'",
                $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                $"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false",
                $"Dismount-VMHostAssignableDevice -Force -LocationPath '{path}'",
                $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{vmname}'"
            ];
            messages =
            [
                Properties.Resources.string5,
                Properties.Resources.cpucache,
                Properties.Resources.getpath,
                Properties.Resources.Disabledevice,
                Properties.Resources.Dismountdevice,
                Properties.Resources.mounting
            ];
        }
        else if (vmname != Properties.Resources.Host && nowname != Properties.Resources.Host) // Switch between VMs
        {
            commands =
            [
                $"Set-VM -Name '{vmname}' -AutomaticStopAction TurnOff", // Set to force shutdown
                $"Set-VM -GuestControlledCacheTypes $true -VMName '{vmname}'",
                $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{nowname}'",
                $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{vmname}'"
            ];

            messages =
            [
                Properties.Resources.string5,
                Properties.Resources.cpucache,
                Properties.Resources.getpath,
                Properties.Resources.Dismountdevice,
                Properties.Resources.mounting
            ];
        }
        else if (vmname == Properties.Resources.Host && nowname != Properties.Resources.Host) // Switch from VM to host
        {
            commands =
            [
                $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{nowname}'",
                $"Mount-VMHostAssignableDevice -LocationPath '{path}'",
                $"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false"
            ];

            messages =
            [
                Properties.Resources.getpath,
                Properties.Resources.Dismountdevice,
                Properties.Resources.mounting,
                Properties.Resources.enabling
            ];
        }
        else
        {
            commands = [];
            messages = [];
        }

        return new CommandData(commands, messages);
    }

    private async Task<bool> ProcessCommandResult(List<string> logOutput, TextBlock textBlock, ContentDialog dlg)
    {
        if (!logOutput.Any(x => x.Contains("Error")))
            return false; // Continue execution

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            textBlock.Text += "\n" + string.Join(Environment.NewLine, logOutput);
            dlg.CloseButtonText = "OK";
            dlg.Closing += (_, args) => args.Cancel = false;
            RefreshDevices();
        });
        return true; // Stop execution
    }

    private async Task ShowSuccessResult(DropDownButton menuButton, TextBlock textBlock, ContentDialog dlg,
        string vmName)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            menuButton.Content = vmName;
            textBlock.Text = Properties.Resources.operated;
            dlg.CloseButtonText = Properties.Resources.OK;
            dlg.Closing += (_, args) => args.Cancel = false;
            RefreshDevices();
        });
    }

    private MenuItem CreateMenuItem(string header, DropDownButton menu, DeviceInfo device)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            if ((string)menu.Content == header) return;

            var contentTextBlock = new TextBlock
            {
                Text = Properties.Resources.string5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            var dialog = new ContentDialog
            {
                Title = Properties.Resources.setting,
                Content = contentTextBlock,
                CloseButtonText = Properties.Resources.wait
            };

            dialog.Closing += (_, args) => args.Cancel = true;
            dialog.DialogHost = Application.Current.MainWindow is MainWindow mainWindow
                ? mainWindow.ContentPresenterForDialogs
                : null;

            _ = dialog.ShowAsync(CancellationToken.None);

            await DdAps(
                menu,
                dialog,
                contentTextBlock,
                header,
                device.InstanceId,
                device.Path,
                (string)menu.Content);
        };
        return item;
    }


    private readonly struct TextData(string text, int row, int column)
    {
        public string Text { get; } = text;
        public int Row { get; } = row;
        public int Column { get; } = column;
    }

    private readonly struct CommandData(string[] commands, string[] messages)
    {
        public string[] Commands { get; } = commands;
        public string[] Messages { get; } = messages;
    }
}