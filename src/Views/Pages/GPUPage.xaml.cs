namespace ExHyperV.Views.Pages;
using System;
using System.Management.Automation;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using Wpf.Ui;


public partial class GPUPage
{

    public bool refreshlock = false;
    public ISnackbarService SnackbarService { get; set; }
    public GPUPage()
    {
        InitializeComponent();
        GetGpu();
    }
    public class GPUInfo
    {
        public string Name { get; set; } //�Կ�����
        public string Valid { get; set; } //�Ƿ�����
        public string Manu { get; set; } //����
        public string InstanceId { get; set; } //�Կ�ʵ��id
        public string Pname { get; set; } //�ɷ������Կ�·��
        public string Ram { get; set; } //�Դ��С
        public string DriverVersion { get; set; } //�����汾

        // ���캯��
        public GPUInfo(string name, string valid, string manu, string instanceId, string pname, string ram, string driverversion)
        {
            Name = name;
            Valid = valid;
            Manu = manu;
            InstanceId = instanceId;
            Pname = pname;
            Ram = ram;
            DriverVersion = driverversion;
        }
    }
    public class VMInfo
    {
        public string Name { get; set; } //���������
        public string LowMMIO { get; set; } //��λ�ڴ�ռ��С
        public string HighMMIO { get; set; } //��λ�ڴ�ռ��С
        public string GuestControlled { get; set; } //���ƻ���
        public Dictionary<string, string> GPUs { get; set; } //�洢�Կ��������б�



        // ���캯��
        public VMInfo(string name, string low, string high, string guest, Dictionary<string, string> gpus)
        {
            Name = name;
            LowMMIO = low;
            HighMMIO = high;
            GuestControlled = guest;
            GPUs = gpus;
        }

    }
    public async void GetGpu()
    {
        await Task.Run(() => {
            List<GPUInfo> gpuList = [];
            //��ȡĿǰ�������Կ�
            var gpulinked = Utils.Run("Get-WmiObject -Class Win32_VideoController | select PNPDeviceID,name,AdapterCompatibility,DriverVersion");
            if (gpulinked.Count > 0)
            {
                foreach (var gpu in gpulinked)
                {
                    string name = gpu.Members["name"]?.Value.ToString();
                    string instanceId = gpu.Members["PNPDeviceID"]?.Value.ToString();
                    string Manu = gpu.Members["AdapterCompatibility"]?.Value.ToString();
                    string DriverVersion = gpu.Members["DriverVersion"]?.Value.ToString();
                    gpuList.Add(new GPUInfo(name, "True", Manu, instanceId, null, null, DriverVersion));
                }
            }
            //��ȡHyperV֧��״̬
            bool hyperv = Utils.Run("Get-Module -ListAvailable -Name Hyper-V").Count > 0;

            //��ȡN����I���Դ�

            string script = $@"Get-ItemProperty -Path ""HKLM:\SYSTEM\ControlSet001\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\0*"" -ErrorAction SilentlyContinue |
            Select-Object MatchingDeviceId,
                  @{{Name=""MemorySize""; Expression={{
                      if ($_.""HardwareInformation.qwMemorySize"") {{
                          $_.""HardwareInformation.qwMemorySize""
                      }} elseif ($_.""HardwareInformation.MemorySize"" -is [byte[]]) {{
                          $hexString = [BitConverter]::ToString($_.""HardwareInformation.MemorySize"").Replace(""-"", """")
                          $memoryValue = [Convert]::ToInt64($hexString, 16)
                          $memoryValue * 16 * 1024 * 1024
                      }} else {{
                          $_.""HardwareInformation.MemorySize""
                      }}
                  }}}} |
            Where-Object {{ $_.MemorySize -ne $null }}";
            var gpuram = Utils.Run(script);
            if (gpuram.Count > 0)
            {
                // �������������Կ�
                foreach (var existingGpu in gpuList)
                {
                    // ���Դ���Ϣ��Ѱ��ƥ����
                    var matchedGpu = gpuram.FirstOrDefault(g =>
                    {
                        string id = g.Members["MatchingDeviceId"]?.Value?.ToString().ToUpper();
                        return !string.IsNullOrEmpty(id) && existingGpu.InstanceId.Contains(id);
                    });

                    // �����Դ���Ϣ������Ĭ��ֵ
                    existingGpu.Ram = matchedGpu?.Members["MemorySize"]?.Value?.ToString() ?? "0";
                }
            }



            //��ȡ�ɷ���GPU����
            var result3 = Utils.Run("Get-VMHostPartitionableGpu | select name");
            if (result3.Count > 0)
            {
                foreach (var gpu in result3)
                {
                    string pname = gpu.Members["Name"]?.Value.ToString();
                    var existingGpu = gpuList.FirstOrDefault(g => pname.ToUpper().Contains(g.InstanceId.Replace("\\", "#")));  // �ҵ� pname ��Ӧ���Կ�
                    if (existingGpu != null)
                    {
                        existingGpu.Pname = pname;
                    }
                }
            }
            GpuUI(gpuList, hyperv);
            GetVM(gpuList);
        });
    
    
    
    }
    public async void GetVM(List<GPUInfo> HostgpuList)
    {
        await Task.Run(() => {
            List<VMInfo> vmList = [];
            //��ȡVM��Ϣ
            var vms = Utils.Run("Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace");
            
            if (vms.Count > 0)
            {
                foreach (var vm in vms)
                {
                    Dictionary<string, string> gpulist = new(); //�洢��������ص�GPU����
                    string vmname = vm.Members["VMName"]?.Value.ToString();
                    string highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value.ToString();
                    string guest = vm.Members["GuestControlledCacheTypes"]?.Value.ToString();

                    //���ϲ�ѯ�����GPU���⻯��Ϣ
                    var vmgpus = Utils.Run($@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id");
                    if (vmgpus.Count > 0)
                    {
                        foreach (var gpu in vmgpus)
                        {
                            string gpupath = gpu.Members["InstancePath"]?.Value.ToString();
                            string gpuid = gpu.Members["Id"]?.Value.ToString();
                            gpulist[gpuid] = gpupath; //�����ֵ䣬id�ǲ�ͬ�ģ�����path�������ͬһ��GPU�������ͬ
                        }
                    }
                    vmList.Add(new VMInfo(vmname,null,highmmio,guest,gpulist));
                }
            }
            VMUI(vmList,HostgpuList);
        });
    }

    private void GpuUI(List<GPUInfo> gpuList,bool hyperv)
    {
        Dispatcher.Invoke(() => { main.Children.Clear(); });

        foreach (var gpu in gpuList)
        {
            string name = string.IsNullOrEmpty(gpu.Name) ? Properties.Resources.none : gpu.Name;
            string valid = string.IsNullOrEmpty(gpu.Valid) ? Properties.Resources.none : gpu.Valid;
            string manu = string.IsNullOrEmpty(gpu.Manu) ? Properties.Resources.none : gpu.Manu;
            string instanceId = string.IsNullOrEmpty(gpu.InstanceId) ? Properties.Resources.none : gpu.InstanceId;
            string pname = string.IsNullOrEmpty(gpu.Pname) ? Properties.Resources.none : gpu.Pname; //GPU����·��
            string driverversion = string.IsNullOrEmpty(gpu.DriverVersion) ? Properties.Resources.none : gpu.DriverVersion;
            string gpup = ExHyperV.Properties.Resources.notsupport; //�Ƿ�֧��GPU����

            string ram = (string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram)) / (1024 * 1024) + " MB";
            if (manu.Contains("Moore")) {
                ram = (string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram)) / 1024 + " MB";
            }//Ħ���̵߳��Դ��¼��HardwareInformation.MemorySize�����ǵ�λ��KB

            if (valid != "True") { continue; } //�޳�δ���ӵ��Կ�
            if (hyperv == false) {gpup = ExHyperV.Properties.Resources.needhyperv;} 
            if (pname != Properties.Resources.none) {gpup = ExHyperV.Properties.Resources.support;}
            
            Dispatcher.Invoke(() =>
            {
                var rowCount = main.RowDefinitions.Count; // ��ȡ��ǰ����
                main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var cardExpander = Utils.CardExpander2();
                Grid.SetRow(cardExpander, rowCount);
                cardExpander.Padding = new Thickness(8); //�������

                var grid = new Grid();
                grid.HorizontalAlignment = HorizontalAlignment.Stretch;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                //����ͼ��
                var image = Utils.CreateGpuImage(manu,64);
                Grid.SetColumn(image, 0);
                grid.Children.Add(image);

                // �Կ��ͺ�
                var gpuname = Utils.CreateStackPanel(name);
                Grid.SetColumn(gpuname, 1);
                grid.Children.Add(gpuname);
                cardExpander.Header = grid;
                
                //��ϸ����
                var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
                var grid2 = new Grid();
                // ���� Grid ���к���
                grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

                var textData = new (string text, int row, int column)[]
                {
                    (ExHyperV.Properties.Resources.manu, 0, 0),(manu, 0, 1),
                    (ExHyperV.Properties.Resources.ram, 1, 0),(ram, 1, 1),
                    (ExHyperV.Properties.Resources.Instanceid, 2, 0),(instanceId, 2, 1),
                    (ExHyperV.Properties.Resources.gpupv, 3, 0),(gpup, 3, 1),
                    (ExHyperV.Properties.Resources.gpupvpath, 4, 0),(pname, 4, 1),
                    (ExHyperV.Properties.Resources.driverversion, 5, 0),(driverversion, 5, 1),
                };

                foreach (var (text, row1, column) in textData)
                {
                    var textBlock = Utils.TextBlock2(text, row1, column);   
                    grid2.Children.Add(textBlock);
                }
                contentPanel.Children.Add(grid2);
                cardExpander.Content = contentPanel;
                main.Children.Add(cardExpander);
            });

        }
            
    }
    private void VMUI(List<VMInfo> vmList, List<GPUInfo> Hostgpulist)
    {
        Dispatcher.Invoke(() => { vms.Children.Clear(); });

        foreach (var vm in vmList)
        {
            string name = vm.Name;
            string low = vm.LowMMIO;
            string high = vm.HighMMIO;
            string guest = vm.GuestControlled;
            Dictionary<string, string> GPUs = vm.GPUs;

            //UI����
            Dispatcher.Invoke(() =>
            {
                //vms��Ϊ����VM�ĸ��ڵ�
                //cardExpander��ΪVM�ڵ�

                var rowCount = vms.RowDefinitions.Count; // ��ȡ��ǰ����
                vms.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var cardExpander = Utils.CardExpander2();
                Grid.SetRow(cardExpander, rowCount); //�趨VM�ڵ������
                cardExpander.Padding = new Thickness(10,8,10,8); //�������

                cardExpander.Icon = Utils.FontIcon(24, "\xE7F4"); //�����ͼ��

                //VM�Ҳ����ݣ���Ϊ���ƺͰ�ť��
                var grid1 = new Grid(); //������Ҳಿ�֣��������ֺͰ�ť
                grid1.HorizontalAlignment = HorizontalAlignment.Stretch;
                grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // VM������
                var vmname = Utils.TextBlock1(name);
                grid1.Children.Add(vmname);

                //��Ӱ�ť
                var addbutton = new Wpf.Ui.Controls.Button
                {
                    Content = ExHyperV.Properties.Resources.addgpu,
                    Margin = new Thickness(0, 0, 5, 0),
                };
                addbutton.Click += (sender, e) => Gpu_mount(sender, e, name, Hostgpulist); //��ť����¼�

                Grid.SetColumn(addbutton, 1);
                grid1.Children.Add(addbutton);

                cardExpander.Header = grid1;
                cardExpander.IsExpanded = true; //Ĭ��չ�����������

                //������VM���������ݲ��֣�Ҳ����GPU�б�

                var GPUcontent = new Grid(); //GPU�б�ڵ�
                foreach (var gpu in GPUs)
                {
                    var gpupath = gpu.Value;
                    var gpuid = gpu.Key;

                    GPUcontent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var gpuExpander = new CardExpander
                    {
                        ContentPadding = new Thickness(6),
                    };
                    Grid.SetRow(gpuExpander, GPUcontent.RowDefinitions.Count); //��ȡ������GPU�б�ڵ㵱ǰ����
                    gpuExpander.Padding = new Thickness(10, 8, 10, 8); //�������

                    var grid0 = new Grid(); //GPU��ͷ������Grid�������Զ���icon
                    grid0.HorizontalAlignment = HorizontalAlignment.Stretch;
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    //�Կ�����
                    var thegpu = Hostgpulist.FirstOrDefault(g => g.Pname == gpupath); //ѡ�������Կ��е������Կ�
                    string name = thegpu.Name;
                    var gpuname = Utils.CreateStackPanel(name);
                    Grid.SetColumn(gpuname, 1);
                    grid0.Children.Add(gpuname);

                    //�Կ�ͼ��
                    var gpuimage = Utils.CreateGpuImage(thegpu.Manu,32);
                    Grid.SetColumn(gpuimage, 0);
                    grid0.Children.Add(gpuimage);

                    //ɾ����ť
                    var button = new Wpf.Ui.Controls.Button {
                        Content = ExHyperV.Properties.Resources.uninstall,
                        Margin = new Thickness(0,0,5,0),
                    };
                    button.Click += (sender, e) => Gpu_dismount(sender, e, gpuid, vm.Name, gpuExpander); //��ť����¼�

                    Grid.SetColumn(button, 2);
                    grid0.Children.Add(button);
                    gpuExpander.Header = grid0;
                    
                    //GPU��������ϸ���ݣ�һ��Panel�����������Ϣ
                    var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
                    var grid2 = new Grid();
                    // ���� Grid ���к���
                    grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                    grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                    grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

                    var textData = new (string text, int row, int column)[]
                    {
                        (ExHyperV.Properties.Resources.gpupvid, 0, 0),(gpuid, 0, 1),
                        (ExHyperV.Properties.Resources.gpupvpath, 1, 0),(gpupath, 1, 1),
                     };

                    foreach (var (text, row1, column) in textData)
                    {
                        var textBlock = Utils.TextBlock2(text, row1, column);
                        grid2.Children.Add(textBlock);
                    }
                    contentPanel.Children.Add(grid2);
                    gpuExpander.Content = contentPanel;
                    GPUcontent.Children.Add(gpuExpander);
                }
                cardExpander.Content = GPUcontent;
                vms.Children.Add(cardExpander);
            });

        }
        refreshlock = false;
        Dispatcher.Invoke(() => {progressbar.Visibility = Visibility.Collapsed; });//���ؼ�����
    }

    private void Gpu_dismount(object sender, RoutedEventArgs e,string id ,string vmname, CardExpander gpuExpander)
    {
        PowerShell ps = PowerShell.Create();
        //ɾ��ָ����GPU������
        ps.AddScript($@"Remove-VMGpuPartitionAdapter -VMName '{vmname}' -AdapterId '{id}'");
        var result = ps.Invoke();
        // ����Ƿ��д���
        if (ps.Streams.Error.Count > 0)
        {
            foreach (var error in ps.Streams.Error)
            {
                System.Windows.MessageBox.Show($"Error: {error.ToString()}");
            }
        }
        else
        {
            var parentGrid = (Grid)gpuExpander.Parent;
            parentGrid.Children.Remove(gpuExpander); //��Ҫ�ڳɹ�ִ�к���С�
        }
    }

    private void Gpu_mount(object sender, RoutedEventArgs e, string vmname, List<GPUInfo> Hostgpulist)
    {
        ChooseGPUWindow selectItemWindow = new ChooseGPUWindow(vmname,Hostgpulist);
        selectItemWindow.GPUSelected += SelectItemWindow_GPUSelected;
        selectItemWindow.ShowDialog(); //��ʾGPU������ѡ�񴰿�
        
    }

    private void vmrefresh(object sender, RoutedEventArgs e)
    {
        if (!refreshlock) {
            progressbar.Visibility = Visibility.Visible; //��ʾ������
            refreshlock = true;
            GetGpu();
        }
    }

    private async void SelectItemWindow_GPUSelected(object sender, (string, string) args)
    {
        string GPUname = args.Item1;
        string VMname = args.Item2;

        SnackbarService = new SnackbarService();
        var ms = Application.Current.MainWindow as MainWindow; //��ȡ������

        SnackbarService.SetSnackbarPresenter(ms.SnackbarPresenter);
        SnackbarService.Show(ExHyperV.Properties.Resources.success,GPUname+ExHyperV.Properties.Resources.already+ VMname,ControlAppearance.Success,new SymbolIcon(SymbolRegular.CheckboxChecked24,32), TimeSpan.FromSeconds(2));
        vmrefresh(null, null);

    }

}




