using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Windows;
using Wpf.Ui.Controls;
using System.Windows.Media;
public partial class DDAPage
{
    public bool refreshlock = false;
    public DDAPage()
    {
        InitializeComponent();
        Task.Run(() => Initialinfo()); //��ȡ�豸��Ϣ
    }
    public class DeviceInfo
    {
        public string FriendlyName { get; set; }
        public string Status { get; set; }
        public string ClassType { get; set; }
        public string InstanceId { get; set; }
        public string Path { get; set; }
        public List<string> VmNames { get; set; }  // �洢����������б�

        // ���캯��
        public DeviceInfo(string friendlyName, string status, string classType, string instanceId,List<string> vmNames,string path)
        {
            FriendlyName = friendlyName;
            Status = status;
            ClassType = classType;
            InstanceId = instanceId;
            VmNames = vmNames; 
            Path = path;
        }
    }
    
    private async void Initialinfo(){
        List<DeviceInfo> deviceList = new List<DeviceInfo>();
        await GetInfo(deviceList); //��ȡ����
        //����UI
        Dispatcher.Invoke(() =>
        {
            ParentPanel.Children.Clear();
            progressRing.Visibility = Visibility.Collapsed; //���ؼ�����
            foreach (var device in deviceList) //����UI
            {
                AddCardExpander(device.FriendlyName, device.Status, device.ClassType, device.InstanceId, device.Path, device.VmNames);
            }
            refreshlock = false;
        });
    }
    public void AddCardExpander(string friendlyName, string status, string classType, string instanceId, string path, List<string> vmNames)
    {
        var rowCount = ParentPanel.RowDefinitions.Count; // ��ȡ��ǰ����
        ParentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // �����µ�һ��

        var cardExpander = Utils.CardExpander1();
        cardExpander.Icon = Utils.FontIcon1(classType, friendlyName);

        var headerGrid = new Grid(); // ���� header �� Grid ���֣��������У���һ��ռ��ʣ��ռ䣬�ڶ��и�����������Ӧ���
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var drivername = Utils.TextBlock1(friendlyName); //�豸��
        Grid.SetColumn(drivername, 0); // ��ӵ���һ��
        headerGrid.Children.Add(drivername);

        var Menu = Utils.DropDownButton1(status); //�Ҳఴť
        Grid.SetColumn(Menu, 1); // ��Ӱ�ť���ڶ���

        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(CreateMenuItem("����")); //�������һ������ѡ��ͺ����������б��ں�
        foreach (var vmName in vmNames)
        {
            contextMenu.Items.Add(CreateMenuItem(vmName));
        }
        MenuItem CreateMenuItem(string header)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) =>
            {
                if (Menu.Content != header) // ���û�ѡ�����Ŀǰ��ѡ��ʱ
                {  
                    Switchvm(Menu, (string)Menu.Content, header, instanceId, path);
                }
            };
            return item;
        }
        Menu.Flyout = contextMenu;
        headerGrid.Children.Add(Menu);
        cardExpander.Header = headerGrid;


        // ��ϸ����
        var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
        var grid = new Grid();

        // ���� Grid ���к���
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        }
        var textBlocks = new TextBlock[]
        {
        new TextBlock { Text = "����", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        new TextBlock { Text = "ʵ��ID", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        new TextBlock { Text = "·��", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        };
        var dataTextBlocks = new TextBlock[]
        {
        new TextBlock { Text = classType, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        new TextBlock { Text = instanceId, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        new TextBlock { Text = path, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        };
        for (int i = 0; i < 3; i++)
        {
            Grid.SetRow(textBlocks[i], i);
            Grid.SetColumn(textBlocks[i], 0); // ��һ��
            grid.Children.Add(textBlocks[i]);

            Grid.SetRow(dataTextBlocks[i], i);
            Grid.SetColumn(dataTextBlocks[i], 1); // �ڶ���
            grid.Children.Add(dataTextBlocks[i]);
        }
        contentPanel.Children.Add(grid);
        cardExpander.Content = contentPanel;
        Grid.SetRow(cardExpander, rowCount);
        ParentPanel.Children.Add(cardExpander);
    }

    public string GetIconPath(string deviceType, string friendlyName)
    {
        switch (deviceType)
        {
            case "Display":
                return "\xF211";  // �Կ�ͼ�� 
            case "Net":
                return "\xE839";  // ����ͼ��
            case "USB":
                return friendlyName.Contains("USB4")
                    ? "\xE945"    // �׵�ӿ�ͼ��
                    : "\xECF0";   // ��ͨUSBͼ��
            case "HIDClass":
                return "\xE928";  // HID�豸ͼ��
            case "SCSIAdapter":
            case "HDC":
                return "\xEDA2";  // �洢������ͼ��
            default:
                return friendlyName.Contains("Audio")
                    ? "\xE995"     // ��Ƶ�豸ͼ��
                    : "\xE950";    // Ĭ��ͼ��
        }
    }

    private async Task Switchvm(DropDownButton menu, string Nowname, string Vmname, string instanceId, string path)
    {
        TextBlock contentTextBlock = new TextBlock
        {
            Text = "�޸�������Ĺػ�����Ϊǿ�ƶϵ�...",
            HorizontalAlignment = HorizontalAlignment.Center, // ˮƽ����
            VerticalAlignment = VerticalAlignment.Center,     // ��ֱ����
            TextWrapping = TextWrapping.Wrap                  // �����ı�����
        };

        ContentDialog myDialog = new()
        {
            Title = "�趨�޸���",
            Content = contentTextBlock,
            CloseButtonText = "���������жϣ���ȴ�",
        };
        myDialog.Closing += (sender, args) => { args.Cancel = true; };// ��ֹ�û������ť�����ر��¼�
        var ms = Application.Current.MainWindow as MainWindow; //���ø��ڵ�
        myDialog.DialogHost = ms.ContentPresenterForDialogs;
        var backgroundTask = Task.Run(() => PerformBackgroundOperationAsync(menu, myDialog, contentTextBlock, Vmname, instanceId, path, Nowname));
        await myDialog.ShowAsync(CancellationToken.None);
    }
    private async Task PerformBackgroundOperationAsync(DropDownButton menu,ContentDialog dialog,TextBlock contentTextBlock,string Vmname,string instanceId,string path,string Nowname)
    {
        var (psCommands, messages) = GetCommandsAndMessages(Vmname,instanceId,path,Nowname); //��Ԫ���ȡ��Ӧ���������Ϣ��ʾ
        for (int i = 0; i < messages.Length; i++)
        {
            Application.Current.Dispatcher.Invoke(() =>{contentTextBlock.Text = messages[i];}); //������ʾ
            Thread.Sleep(200); //����һ������ʱ����ʾ����
            var logOutput = await ExecutePowerShellCommand(psCommands[i]); //ִ���������ȡ��־
            if (logOutput.Any(log => log.Contains("����")))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    contentTextBlock.Text += "\n"+ string.Join(Environment.NewLine, logOutput); // ����һ���ж���Ϣ
                    dialog.CloseButtonText = "OK"; // ���°�ť�ı�
                    dialog.Closing += (sender, args) => { args.Cancel = false; }; //�����û�����ر�
                    ddarefresh(null, null);
                });
                return;// �˳�ѭ��

            }
        }
        Application.Current.Dispatcher.Invoke(() =>
        {
            menu.Content = Vmname;
            contentTextBlock.Text = "������ɣ�";
            dialog.CloseButtonText = "OK"; // ���°�ť�ı�
            dialog.Closing += (sender, args) => { args.Cancel = false; };// �����û��ر�
            ddarefresh(null, null);
        });
    }

    private async Task<List<string>> ExecutePowerShellCommand(string psCommand)
    {
        List<string> logOutput = new List<string>();
        try{
            var powerShell = PowerShell.Create(); // ���� PowerShell �Ự��ִ������
            powerShell.AddScript(psCommand); // ��� PowerShell �ű�
            var result = await Task.Run(() => powerShell.Invoke());// �첽ִ������
            foreach (var item in result)// �������ӵ� logOutput �б�
            { logOutput.Add(item.ToString());}// ��ÿ���������ӵ���־�б���
            var errorStream = powerShell.Streams.Error.ReadAll(); // ����׼��������������Ƿ�׽������
            if (errorStream.Count > 0)
            {foreach (var error in errorStream){logOutput.Add($"����: {error.ToString()}");}}// ��������Ϣ��ӵ���־
        }
        catch (Exception ex){logOutput.Add($"����: {ex.Message}");}//����֮��Ĵ���
        return logOutput; // ������־���
    }

    private async Task GetInfo(List<DeviceInfo> deviceList)
    {
        try
        {
            using (PowerShell PowerShellInstance = PowerShell.Create())
            {
                Dictionary<string, string> vmdevice = new Dictionary<string, string>(); //�洢��������ص�PCIP�豸
                List<string> vmNameList = new List<string>() ;// �洢������б�
                PowerShellInstance.AddScript("Set-ExecutionPolicy RemoteSigned -Scope Process -Force"); //���ò���
                PowerShellInstance.AddScript("Import-Module PnpDevice");

                //1.��ȡ����������Ϣ�����Ѿ������˵��豸�����ֵ��Ա������ȡ��

                //����Ƿ�װhyperv
                PowerShellInstance.AddScript("Get-Module -ListAvailable -Name Hyper-V");
                var hypervstatus = PowerShellInstance.Invoke();
                if (hypervstatus.Count != 0) //�Ѱ�װ
                {
                    PowerShellInstance.AddScript(@"
                    $vm = Get-VM | Select-Object Name, HighMemoryMappedIoSpace, LowMemoryMappedIoSpace
                    return @($vm)");//��ȡ�������Ϣ
                    var vmdata = PowerShellInstance.Invoke();
                    foreach (var vm in vmdata)
                    {
                        var Name = vm.Members["Name"]?.Value?.ToString();
                        var Highmap = vm.Members["HighMemoryMappedIoSpace"]?.Value?.ToString();
                        var Lowmap = vm.Members["LowMemoryMappedIoSpace"]?.Value?.ToString();
                        if (!string.IsNullOrEmpty(Name)){vmNameList.Add(Name);}//���ֲ�Ϊ������Ӹ������
                        var script = $@"Get-VMAssignableDevice -VMName '{Name}' | Select-Object InstanceID";

                        PowerShellInstance.AddScript(script); //��ȡ������µ��豸�б�
                        var deviceData = PowerShellInstance.Invoke();//��ȡ�豸�б�
                        if (deviceData != null && deviceData.Count > 0)
                        {
                            foreach (var device in deviceData)
                            {
                                var instanceId = device.Members["InstanceID"]?.Value?.ToString().Substring(4);
                                if (!string.IsNullOrEmpty(instanceId) && !string.IsNullOrEmpty(Name))
                                {
                                    vmdevice[instanceId] = Name; // �� InstanceID �� VMName �����ֵ�
                                }
                            }
                        }
                    }
                }
                else {}//���û�а�װHyperVģ�飬���޷���ȡVM��Ϣ������Ӱ��������Ӳ����ȡ��

                //��ȡ PCIP �豸��Ϣ
                string scripts1 = @"
                $pciDevices = Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCIP\*' }
                $instanceIds = $pciDevices.InstanceId
                $pciDevices | Select-Object Class, InstanceId, FriendlyName, Status |ForEach-Object {$_}";
                PowerShellInstance.AddScript(scripts1);
                var PCIPData = PowerShellInstance.Invoke();
                if (PCIPData != null && PCIPData.Count > 0)
                {
                    foreach (var PCIP in PCIPData)
                    {
                        var instanceId = PCIP.Members["InstanceId"]?.Value?.ToString().Substring(4); //��ȡPCIP����ı��
                        //����������������豸δ��������������PCIP״̬����ok��˵��Ҳδ���������������ж��̬��
                        if (!vmdevice.ContainsKey(instanceId)&& PCIP.Members["Status"]?.Value?.ToString()=="OK"&&!string.IsNullOrEmpty(instanceId)) //״̬ΪOK���ǿ�
                        {vmdevice[instanceId] = "#��ж��"; }
                    }
                }

                // ��ȡ PCI �豸��Ϣ��������ѯ��������յ������豸��������Ϣ�����ԣ����������⣬��ʱ���鶼��ѯ���꣩
                string scripts = @"
                $pciDevices = Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCI\*' }
                $instanceIds = $pciDevices.InstanceId
                $pathMap = @{}
                Get-PnpDeviceProperty -InstanceId $instanceIds -KeyName DEVPKEY_Device_LocationPaths |
                    ForEach-Object { 
                        $pathMap[$_.InstanceId] = $_.Data[0] 
                    }
                Get-PnpDeviceProperty -InstanceId $instanceIds -KeyName DEVPKEY_Device_LocationPaths |
                    ForEach-Object { 
                        $pathMap[$_.InstanceId] = $_.Data[0] 
                    }
                Get-PnpDeviceProperty -InstanceId $instanceIds -KeyName DEVPKEY_Device_LocationPaths |
                    ForEach-Object { 
                        $pathMap[$_.InstanceId] = $_.Data[0] 
                    }
                $pciDevices | Select-Object Class, InstanceId, FriendlyName, Status ,Service |
                    ForEach-Object {
                        $_ | Add-Member -NotePropertyName 'Path' -NotePropertyValue $pathMap[$_.InstanceId]
                        $_
                    }";
                PowerShellInstance.AddScript(scripts);

                var Pcidata = PowerShellInstance.Invoke();
                var sortedResults = Pcidata
                .Where(result => result!= null)  // ���˵�Ϊ�յ�Ԫ��
                .OrderBy(result => result.Members["Service"]?.Value?.ToString()[0])  // �����������򣬼�Class �ֶ�����ĸ
                .ToList();
                foreach (var result in sortedResults)
                {
                    var friendlyName = result.Members["FriendlyName"]?.Value?.ToString();
                    var status = result.Members["Status"]?.Value?.ToString();
                    var classType = result.Members["Class"]?.Value?.ToString();
                    var instanceId = result.Members["InstanceId"]?.Value?.ToString();
                    var path = result.Members["Path"]?.Value?.ToString();
                    var service = result.Members["Service"]?.Value?.ToString();
                    if (service == "pci" || service == null) {
                        continue; //�ų������豸
                    }
                    

                    // ���unknown�豸���м�飬�����������1.�������Ƴ���2.��������������3.ж��̬��

                    // ���Ƿ�����������������Ƴ�
                    if (status == "Unknown" && !string.IsNullOrEmpty(instanceId) && instanceId.Length > 3)
                    {
                        if (vmdevice.ContainsKey(instanceId.Substring(3))) // ���ȥ��ǰ��λ���PCI��InstanceId�Ƿ��ڴ洢��vm�ѷ����豸���б���
                        {
                            status = vmdevice[instanceId.Substring(3)];
                        } 
                        else { continue; } //����������б��ڣ�Ҳ������ж��̬���ų���
                    }
                    else {
                        status = "����"; //�����豸�������
                    }
                    deviceList.Add(new DeviceInfo(friendlyName, status, classType, instanceId,vmNameList,path)); //��ӵ��豸�б�
                }
            }
        }
        catch(Exception ex){
            System.Windows.MessageBox.Show(ex.ToString());
        }
    }

    // ���ݳ��������������Ϣ������
    public (string[] commands, string[] messages) GetCommandsAndMessages(string Vmname, string instanceId, string path, string Nowname)
    {
        // �����������Ϣ��Ĭ������
        string[] commands;
        string[] messages;

        if (Nowname == "#��ж��" && Vmname == "����")
        {  //���豸�۷�����
            commands = new string[]
            {
                $"Mount-VMHostAssignableDevice -LocationPath '{path}'"
            };
            messages = new string[]
            {
            "�����豸��...",
            };
        }
        else if(Nowname == "#��ж��" && Vmname != "����")
        { //��ж���豸��������������
            commands = new string[]
            {   
                $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'",
            };
            messages = new string[]
            {
            "�����豸��...",
            };
        }
        // ���������жϷ��ز�ͬ���������Ϣ
        else if (Nowname == "����")  // �����л��������
        {
            commands = new string[]
            {
            $"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff",  // ��Ϊǿ�ƶϵ�
            $"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'",
            $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
            $"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false",
            $"Dismount-VMHostAssignableDevice -Force -LocationPath '{path}'",
            $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'",
            };
            messages = new string[]
            {
            "�޸�������Ĺػ�����Ϊǿ�ƶϵ�...",
            "��������ɹ���CPU����...",
            "��ȡ�豸��·��...",
            "�����豸��...",
            "ж���豸��...",
            "�����豸��...",
            };
        }
        else if (Vmname != "����"&& Nowname != "����")  // ������л��������
        {
            commands = new string[]
            {
            $"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff",  // ��Ϊǿ�ƶϵ�
            $"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'",
            $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
            $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'",
            $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'",
            };

            messages = new string[]
            {
            "�޸�������Ĺػ�����Ϊǿ�ƶϵ�...",
            "��������ɹ���CPU����...",
            "��ȡ�豸��·��...",
            "ж���豸��...",
            "�����豸��...",
            };
        }
        else if (Vmname == "����" && Nowname != "����")  // ������л�������
        {
            commands = new string[]
            {
                $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'",
                $"Mount-VMHostAssignableDevice -LocationPath '{path}'",
            };

            messages = new string[]
            {
                "��ȡ�豸��·��...",
                "ж���豸��...",
                "�����豸��...",
            };
        }
        else
        {
            commands = new string[0];
            messages = new string[0];
        }
        return (commands, messages);

    }
    
    //�����豸֮ǰ���Զ����ע�����Ϣ��


    private void ddarefresh(object sender, RoutedEventArgs e) {

        if (!refreshlock)
        {
            refreshlock = true;
            progressRing.Visibility = Visibility.Visible; //��ʾ������
            Task.Run(() => Initialinfo()); //��ȡ�豸��Ϣ
        }

        
    }



    //����DDA������ʾ�����ȡ�ߵ�MIMO�ռ���ͨ����Get-VM | Select-Object Name, HighMemoryMappedIoSpace, LowMemoryMappedIoSpace��
    //ֱ�ӵõ��ģ���û�н��ж��������
    //���ݻ�������Ϣ������ֱͨ����������Ҫ�޸ģ�����128-512���ɡ�
    //���Ƕ��ڷ�����GPU-P/GPU-PV)������������Ҫֱ�ӷ����ڴ�������Ҫ�޸ĸ�λ�ڴ棡


}
