namespace ExHyperV.Views.Pages;
using System;
using System.Management.Automation;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using System.Windows.Media.Imaging;
using Microsoft.CodeAnalysis;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using System.Windows.Media;
using System.Collections.Generic;
using System.Security.RightsManagement;
using Wpf.Ui;

public partial class GPUPage
{

    public bool refreshlock = false;
    public ISnackbarService snackbarService { get; set; }
    public GPUPage()
    {
        InitializeComponent();
        GetGpu();
        

    }
    public async void GetGpu()
    {
        await Task.Run(() => {

            List<GPUInfo> gpuList = [];
            PowerShell ps = PowerShell.Create();
            //��ȡĿǰ�������Կ�
            string script0 = "Get-WmiObject -Class Win32_VideoController | select PNPDeviceID,name,AdapterCompatibility,DriverVersion";
            ps.AddScript(script0);
            var result = ps.Invoke();
            if (result.Count > 0)
            {
                foreach (var gpu in result)
                {
                    string name = gpu.Members["name"]?.Value.ToString();
                    string instanceId = gpu.Members["PNPDeviceID"]?.Value.ToString();
                    string Manu = gpu.Members["AdapterCompatibility"]?.Value.ToString();
                    string DriverVersion = gpu.Members["DriverVersion"]?.Value.ToString();

                    gpuList.Add(new GPUInfo(name, "True", Manu, instanceId, null, null, DriverVersion));

                }
            }

            //��ȡHyperV֧��״̬
            bool hyperv = false;
            ps.AddScript("Get-Module -ListAvailable -Name Hyper-V");
            var hypervstatus = ps.Invoke();
            if (hypervstatus.Count != 0) { hyperv = true; }

            //��ȡN����I���Դ�
            string script = $@"Get-ItemProperty -Path ""HKLM:\SYSTEM\ControlSet001\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\0*"" -ErrorAction SilentlyContinue |
    Select-Object DriverDesc,
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
    Where-Object {{ $_.MemorySize -ne $null }}
���ص��ڴ�����Ϊ�ֽ�";
            ps.AddScript(script);
            var result2 = ps.Invoke();
            if (result2.Count > 0)
            {
                foreach (var gpu in result2)
                {
                    string driverDesc = gpu.Members["DriverDesc"]?.Value.ToString();
                    string memorySize = gpu.Members["MemorySize"]?.Value.ToString();

                    var existingGpu = gpuList.FirstOrDefault(g => g.Name == driverDesc); //ѡ�������Կ��е������Կ�
                    if (existingGpu != null) //ֻ�������Կ��Ÿ���
                    {
                        existingGpu.Ram = memorySize;
                    }
                }
            }
            //��ȡ�ɷ���GPU����
            string script1 = "Get-VMHostPartitionableGpu | select name";
            ps.AddScript(script1);
            var result3 = ps.Invoke();
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
            PowerShell ps = PowerShell.Create();
            //��ȡVM��Ϣ
            string script0 = "Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace";
            ps.AddScript(script0);
            var vms = ps.Invoke();
            
            if (vms.Count > 0)
            {
                foreach (var vm in vms)
                {
                    Dictionary<string, string> gpulist = new Dictionary<string, string>(); //�洢��������ص�GPU����
                    string vmname = vm.Members["VMName"]?.Value.ToString();

                    string highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value.ToString();
                    string guest = vm.Members["GuestControlledCacheTypes"]?.Value.ToString();
                    //���ϲ�ѯ�����GPU���⻯��Ϣ

                    string script1 = $@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id";
                    ps.AddScript(script1);
                    var vmgpus = ps.Invoke();

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

    private static StackPanel CreateStackPanel(string text)
    {
        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        stackPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });

        return stackPanel;
    }

    private void GpuUI(List<GPUInfo> gpuList,bool hyperv)
    {
        Dispatcher.Invoke(() =>
        { main.Children.Clear(); });
        foreach (var gpu in gpuList)
        {
            string name = string.IsNullOrEmpty(gpu.Name) ? "�Կ�����ȱʧ" : gpu.Name;
            string valid = string.IsNullOrEmpty(gpu.Valid) ? "��" : gpu.Valid;
            string manu = string.IsNullOrEmpty(gpu.Manu) ? "��" : gpu.Manu;
            string instanceId = string.IsNullOrEmpty(gpu.InstanceId) ? "��" : gpu.InstanceId;
            string pname = string.IsNullOrEmpty(gpu.Pname) ? "��" : gpu.Pname; //GPU����·��
            string ram = (string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram)) / (1024 * 1024) + " MB";
            string driverversion = string.IsNullOrEmpty(gpu.DriverVersion) ? "��" : gpu.DriverVersion;
            string gpup = "��֧��"; //�Ƿ�֧��GPU����

            if (valid != "True") { continue; } //�޳�δ���ӵ��Կ�
            if (hyperv == false) {gpup = "δ��װHyperV";} //����Ҫ������ԱȨ��
           
            if (pname != "��") {gpup = "֧��";}
            
            Dispatcher.Invoke(() =>
            {
                var rowCount = main.RowDefinitions.Count; // ��ȡ��ǰ����
                main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var cardExpander = new CardExpander
                {
                    Margin = new Thickness(30, 5, 10, 0),
                    ContentPadding = new Thickness(6),
                };
                Grid.SetRow(cardExpander, rowCount);

                var grid = new Grid();
                grid.HorizontalAlignment = HorizontalAlignment.Stretch;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                //ͼ��
                var image = new Wpf.Ui.Controls.Image
                {
                    Source = new BitmapImage(new Uri(Utils.GetGpuImagePath(manu)))
                    {
                        CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                        CacheOption = BitmapCacheOption.OnLoad
                    },
                    Height = 64,
                    Width = 64,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top
                };
                // ���ÿ���ݲ���
                image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
                image.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);

                Grid.SetColumn(image, 0);

                // �Կ��ͺ�
                var gpuname = CreateStackPanel(name);
                gpuname.Margin = new Thickness(10,0,0,0);
                gpuname.VerticalAlignment = VerticalAlignment.Center;

                Grid.SetColumn(gpuname, 1);

                grid.Children.Add(gpuname);
                grid.Children.Add(image);

                cardExpander.Header = grid;
                
                //��ϸ����
                var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
                var grid2 = new Grid();
                // ���� Grid ���к���
                grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var textBlocks = new TextBlock[]
                {
                new TextBlock { Text = "������", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = "ר���Դ�", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },      
                new TextBlock { Text = "ʵ��ID", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = "GPU��������", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = "GPU����·��", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = "��������汾", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                };
                var dataTextBlocks = new TextBlock[]
                {
                new TextBlock { Text = manu, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = ram, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = instanceId, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = gpup, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = pname, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = driverversion, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                };
                var row = textBlocks.Length; //�ж�����
                for (int i = 0; i < row; i++)
                {
                    grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                }
                for (int i = 0; i < row; i++)
                {
                    Grid.SetRow(textBlocks[i], i);
                    Grid.SetColumn(textBlocks[i], 0); // ��һ��
                    grid2.Children.Add(textBlocks[i]);

                    Grid.SetRow(dataTextBlocks[i], i);
                    Grid.SetColumn(dataTextBlocks[i], 1); // �ڶ���
                    grid2.Children.Add(dataTextBlocks[i]);
                }
                contentPanel.Children.Add(grid2);
                cardExpander.Content = contentPanel;
                main.Children.Add(cardExpander);
            });

        }
            
    }


    private void VMUI(List<VMInfo> vmList, List<GPUInfo> Hostgpulist)
    {
        Dispatcher.Invoke(() =>
        { vms.Children.Clear(); });
            
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


                var cardExpander = new CardExpander
                {
                    Margin = new Thickness(30, 5, 10, 0),
                    ContentPadding = new Thickness(6),
                };
                Grid.SetRow(cardExpander, rowCount); //�趨VM�ڵ������

                //VM��ͼ��
                var icon = new FontIcon
                {
                    FontSize = 20,
                    FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                    Glyph = "\xE7F4" // ��ȡͼ��Unicode
                };
                cardExpander.Icon = icon;


                //VM�Ҳ����ݣ���Ϊ���ƺͰ�ť��

                var grid1 = new Grid(); //������Ҳಿ�֣��������ֺͰ�ť
                grid1.HorizontalAlignment = HorizontalAlignment.Stretch;
                grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // VM������
                var vmname = CreateStackPanel(name);
                vmname.Margin = new Thickness(5, 0, 0, 0);
                vmname.VerticalAlignment = VerticalAlignment.Center;
                grid1.Children.Add(vmname);

                //��Ӱ�ť
                var button1 = new Wpf.Ui.Controls.Button
                {
                    Content = "���GPU",
                    Margin = new Thickness(0, 0, 5, 0),
                };
                button1.Click += (sender, e) => Gpu_mount(sender, e, name, Hostgpulist); //��ť����¼�

                Grid.SetColumn(button1, 1);
                grid1.Children.Add(button1);

                cardExpander.Header = grid1;
                cardExpander.IsExpanded = true; //Ĭ��չ�����������

                //������VM���������ݲ��֣�Ҳ����GPU�б�

                var GPUcontent = new Grid(); //GPU�б�ڵ�
                foreach (var gpu in GPUs)
                {
                    var gpupath = gpu.Value;
                    var gpuid = gpu.Key;

                    var rowCount2 = GPUcontent.RowDefinitions.Count; // ��ȡGPU�б�ڵ㵱ǰ����
                    GPUcontent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var gpuExpander = new CardExpander
                    {
                        ContentPadding = new Thickness(6),
                    };
                    Grid.SetRow(gpuExpander, rowCount2);

                    var grid0 = new Grid(); //GPU��ͷ������Grid�������Զ���icon
                    grid0.HorizontalAlignment = HorizontalAlignment.Stretch;
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    //�Կ�����
                    var thegpu = Hostgpulist.FirstOrDefault(g => g.Pname == gpupath); //ѡ�������Կ��е������Կ�
                    string name = thegpu.Name;
                    var gpuname = CreateStackPanel(name);
                    gpuname.Margin = new Thickness(10, 0, 0, 0);
                    gpuname.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(gpuname, 1);
                    grid0.Children.Add(gpuname);

                    //�Կ�ͼ��
                    var image0 = new Wpf.Ui.Controls.Image
                    {
                        Source = new BitmapImage(new Uri(Utils.GetGpuImagePath(thegpu.Manu)))
                        {
                            CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                            CacheOption = BitmapCacheOption.OnLoad
                        },
                        Height = 32,
                        Width = 32,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    // ���ÿ���ݲ���
                    image0.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
                    image0.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);

                    Grid.SetColumn(image0, 0);
                    grid0.Children.Add(image0);

                    //ɾ����ť
                    var button = new Wpf.Ui.Controls.Button {
                        Content = "ж��",
                        Margin = new Thickness(0,0,5,0),
                    };
                    button.Click += (sender, e) => Gpu_unmount(sender, e, gpuid, vm.Name, gpuExpander); //��ť����¼�

                    Grid.SetColumn(button, 2);
                    grid0.Children.Add(button);

                    gpuExpander.Header = grid0;
                    

                    //GPU��������ϸ���ݣ�һ��Panel�����������Ϣ
                    var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
                    var grid2 = new Grid();
                    // ���� Grid ���к���
                    grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                    grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var textBlocks = new TextBlock[]
                    {
                        new TextBlock { Text = "GPU����ID", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                        new TextBlock { Text = "GPU����·��", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                    };
                    var dataTextBlocks = new TextBlock[]
                    {
                        new TextBlock { Text = gpuid, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                        new TextBlock { Text = gpupath, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                    };
                    var row = textBlocks.Length; //�ж�����
                    for (int i = 0; i < row; i++)
                    {
                        grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                    }
                    for (int i = 0; i < row; i++)
                    {
                        Grid.SetRow(textBlocks[i], i);
                        Grid.SetColumn(textBlocks[i], 0); // ��һ��
                        grid2.Children.Add(textBlocks[i]);

                        Grid.SetRow(dataTextBlocks[i], i);
                        Grid.SetColumn(dataTextBlocks[i], 1); // �ڶ���
                        grid2.Children.Add(dataTextBlocks[i]);
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

    private void Gpu_unmount(object sender, RoutedEventArgs e,string id ,string vmname, CardExpander gpuExpander)
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
                System.Windows.MessageBox.Show($"ִ�г���: {error.ToString()}");
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

        

        snackbarService = new SnackbarService();
        var ms = Application.Current.MainWindow as MainWindow; //��ȡ������

        snackbarService.SetSnackbarPresenter(ms.SnackbarPresenter);
        snackbarService.Show("�ɹ�",GPUname+"�ѷ��䵽"+ VMname,ControlAppearance.Success,new SymbolIcon(SymbolRegular.CheckboxChecked24,32), TimeSpan.FromSeconds(2));
        //await Task.Delay(2000); // �ȴ�Snackbar��ʾ2���ٳ��Ը���UI����ֹ������·�����ͻ
        vmrefresh(null, null);

    }

}




