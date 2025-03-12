using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Windows;
using Wpf.Ui.Controls;
using System.Windows.Media;
using System.Security.AccessControl;
using System.Xml.Linq;

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

        Dispatcher.Invoke(() => //����UI
        {
            ParentPanel.Children.Clear();
            progressRing.Visibility = Visibility.Collapsed; //���ؼ�����
            foreach (var device in deviceList) //����UI
            {
                var cardExpander = Utils.CardExpander1();
                cardExpander.Icon = Utils.FontIcon1(device.ClassType, device.FriendlyName);
                Grid.SetRow(cardExpander, ParentPanel.RowDefinitions.Count);
                ParentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // �����µ�һ��

                var headerGrid = new Grid(); // ���� header �� Grid ���֣��������У���һ��ռ��ʣ��ռ䣬�ڶ��и�����������Ӧ���
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var drivername = Utils.TextBlock1(device.FriendlyName); //�豸��
                Grid.SetColumn(drivername, 0); // ��ӵ���һ��
                headerGrid.Children.Add(drivername);

                var Menu = Utils.DropDownButton1(device.Status); //�Ҳఴť
                Grid.SetColumn(Menu, 1); // ��Ӱ�ť���ڶ���

                var contextMenu = new ContextMenu();
                contextMenu.Items.Add(CreateMenuItem("����")); //�������һ������ѡ��ͺ����������б��ں�
                foreach (var vmName in device.VmNames)
                {
                    contextMenu.Items.Add(CreateMenuItem(vmName));
                }
                MenuItem CreateMenuItem(string header)
                {
                    var item = new MenuItem { Header = header };
                    item.Click += async (s, e) =>
                    {
                        if ((String)Menu.Content != header) // ���û�ѡ�����Ŀǰ��ѡ��ʱ
                        {
                            TextBlock contentTextBlock = new TextBlock
                            {
                                Text = "�޸�������Ĺػ�����Ϊǿ�ƶϵ�...",
                                HorizontalAlignment = HorizontalAlignment.Center, // ˮƽ����
                                VerticalAlignment = VerticalAlignment.Center,     // ��ֱ����
                                TextWrapping = TextWrapping.Wrap                  // �����ı�����
                            };

                            ContentDialog Dialog = new()
                            {
                                Title = "�趨�޸���",
                                Content = contentTextBlock,
                                CloseButtonText = "���������жϣ���ȴ�",
                            };

                            Dialog.Closing += (sender, args) => { args.Cancel = true; };// ��ֹ�û������ť�����ر��¼�
                            Dialog.DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs;

                            await Dialog.ShowAsync(CancellationToken.None); //��ʾ��ʾ��

                            await DDAps(Menu, Dialog, contentTextBlock, header, device.InstanceId, device.Path, (String)Menu.Content); //ִ��������
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
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

                var textData = new (string text, int row, int column)[]
                {
                    ("����", 0, 0),
                    ("ʵ��ID", 1, 0),
                    ("·��", 2, 0),
                    (device.ClassType, 0, 1),
                    (device.InstanceId, 1, 1),
                    (device.Path, 2, 1),
                };

                foreach (var (text, row, column) in textData)
                {
                    var textBlock = Utils.TextBlock2(text, row, column);
                    grid.Children.Add(textBlock);
                }

                contentPanel.Children.Add(grid);
                cardExpander.Content = contentPanel;
                ParentPanel.Children.Add(cardExpander);
            }
            refreshlock = false;
        });
    }

    private async Task DDAps(DropDownButton menu,ContentDialog dialog,TextBlock contentTextBlock,string Vmname,string instanceId,string path,string Nowname)
    {
        var (psCommands, messages) = DDACommands(Vmname,instanceId,path,Nowname); //ͨ����Ԫ���ȡ��Ӧ���������Ϣ��ʾ
        for (int i = 0; i < messages.Length; i++)
        {
            Application.Current.Dispatcher.Invoke(() =>{contentTextBlock.Text = messages[i];}); //������ʾ
            Thread.Sleep(200); //����һ������ʱ����ʾ����
            var logOutput = await DDAps(psCommands[i]); //ִ���������ȡ��־
            if (logOutput.Any(log => log.Contains("����")))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    contentTextBlock.Text += "\n"+ string.Join(Environment.NewLine, logOutput); // ����һ���ж���Ϣ
                    dialog.CloseButtonText = "OK"; // ���°�ť�ı�
                    dialog.Closing += (sender, args) => { args.Cancel = false; }; //�����û�����ر�
                    DDArefresh(null, null);
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
            DDArefresh(null, null);
        });
    }

    private async Task<List<string>> DDAps(string psCommand)
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
                var hypervstatus =Utils.Run("Get-Module -ListAvailable -Name Hyper-V");

                if (hypervstatus.Count != 0) //�Ѱ�װ
                {
                    //��ȡ�������Ϣ
                    var vmdata = Utils.Run(@"Get-VM | Select-Object Name");
                    foreach (var vm in vmdata)
                    {
                        var Name = vm.Members["Name"]?.Value?.ToString();

                        if (!string.IsNullOrEmpty(Name)){vmNameList.Add(Name);}//���ֲ�Ϊ������Ӹ������

                        var deviceData = Utils.Run($@"Get-VMAssignableDevice -VMName '{Name}' | Select-Object InstanceID");//��ȡ��������豸�б�

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
                //���û�а�װHyperVģ�飬���޷���ȡVM��Ϣ������Ӱ��������Ӳ����ȡ��

                //��ȡ PCIP �豸��Ϣ
                var PCIPData = Utils.Run("Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCIP\\*' } | Select-Object Class, InstanceId, FriendlyName, Status");
                if (PCIPData != null && PCIPData.Count > 0)
                {
                    foreach (var PCIP in PCIPData)
                    {
                        var instanceId = PCIP.Members["InstanceId"]?.Value?.ToString().Substring(4); //��ȡPCIP����ı��
                        //����������������豸δ����������+PCIP״̬����OK����˵����δ���������������ж��̬��
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
    private static (string[] commands, string[] messages) DDACommands(string Vmname, string instanceId, string path, string Nowname)
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
    

    public void DDArefresh(object sender, RoutedEventArgs e) {

        if (!refreshlock)
        {
            refreshlock = true;
            progressRing.Visibility = Visibility.Visible; //��ʾ������
            Task.Run(() => Initialinfo()); //��ȡ�豸��Ϣ
        }

        
    }


}
