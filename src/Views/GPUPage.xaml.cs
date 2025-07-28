namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Windows;
using System.Windows.Controls;
using ExHyperV;
using ExHyperV.Tools;
using Microsoft.CodeAnalysis;
using Wpf.Ui;
using Wpf.Ui.Controls;


public partial class GPUPage
{

    public bool refreshlock = false;
    public ISnackbarService SnackbarService { get; set; }
    public GPUPage()
    {
        InitializeComponent();
        LoadData();
    }
    private async void LoadData()
    {
        await GetGpu();
    }
    public class GPUInfo
    {
        public string Name { get; set; } //显卡名称
        public string Valid { get; set; } //是否联机
        public string Manu { get; set; } //厂商
        public string InstanceId { get; set; } //显卡实例id
        public string Pname { get; set; } //可分区的显卡路径
        public string Ram { get; set; } //显存大小
        public string DriverVersion { get; set; } //驱动版本
        public string Vendor { get; set; }

        // 构造函数
        public GPUInfo(string name, string valid, string manu, string instanceId, string pname, string ram, string driverversion, string vendor)
        {
            Name = name;
            Valid = valid;
            Manu = manu;
            InstanceId = instanceId;
            Pname = pname;
            Ram = ram;
            DriverVersion = driverversion;
            Vendor = vendor;
        }
    }
    public class VMInfo
    {
        public string Name { get; set; } //虚拟机名称
        public string LowMMIO { get; set; } //低位内存空间大小
        public string HighMMIO { get; set; } //高位内存空间大小
        public string GuestControlled { get; set; } //控制缓存
        public Dictionary<string, string> GPUs { get; set; } //存储显卡适配器列表




        // 构造函数
        public VMInfo(string name, string low, string high, string guest, Dictionary<string, string> gpus)
        {
            Name = name;
            LowMMIO = low;
            HighMMIO = high;
            GuestControlled = guest;
            GPUs = gpus;

        }

    }
    public async Task GetGpu()
    {

        var pciInfoProvider = new PciInfoProvider();
        await pciInfoProvider.EnsureInitializedAsync();
        await Task.Run(() => {
            List<GPUInfo> gpuList = []; //获取目前联机的显卡
            
            var gpulinked = Utils.Run("Get-WmiObject -Class Win32_VideoController | select PNPDeviceID,name,AdapterCompatibility,DriverVersion");
            if (gpulinked.Count > 0)
            {
                foreach (var gpu in gpulinked)
                {
                    string name = gpu.Members["name"]?.Value.ToString();
                    string instanceId = gpu.Members["PNPDeviceID"]?.Value.ToString();
                    string Manu = gpu.Members["AdapterCompatibility"]?.Value.ToString();
                    string DriverVersion = gpu.Members["DriverVersion"]?.Value.ToString();
                    string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId);
                    if (vendor == "Unknown") { continue; } //查不到制造商，就没有必要显示了
                    gpuList.Add(new GPUInfo(name, "True", Manu, instanceId, null, null, DriverVersion,vendor));
                }
            }
            //获取HyperV支持状态
            bool hyperv = Utils.Run("Get-Module -ListAvailable -Name Hyper-V").Count > 0;

            //获取N卡和A卡显存

            string script = $@"
Get-ItemProperty -Path ""HKLM:\SYSTEM\ControlSet001\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\0*"" -ErrorAction SilentlyContinue |
    Select-Object MatchingDeviceId,
          @{{Name='MemorySize'; Expression={{
              if ($_. ""HardwareInformation.qwMemorySize"") {{
                  $_.""HardwareInformation.qwMemorySize""
              }} 
              elseif ($_. ""HardwareInformation.MemorySize"" -and $_.""HardwareInformation.MemorySize"" -isnot [byte[]]) {{
                  $_.""HardwareInformation.MemorySize""
              }}
              else {{
                  $null
              }}
          }}}} |
    Where-Object {{ $_.MemorySize -ne $null -and $_.MemorySize -gt 0 }}
";
            var gpuram = Utils.Run(script);
            if (gpuram.Count > 0)
            {
                // 遍历所有联机显卡
                foreach (var existingGpu in gpuList)
                {
                    // 在显存信息中寻找匹配项
                    var matchedGpu = gpuram.FirstOrDefault(g =>
                    {
                        string id = g.Members["MatchingDeviceId"]?.Value?.ToString().ToUpper().Substring(0, 21);
                        return !string.IsNullOrEmpty(id) && existingGpu.InstanceId.Contains(id);
                    });

                    // 更新显存信息或设置默认值
                    string preram = matchedGpu?.Members["MemorySize"]?.Value?.ToString() ?? "0";
                    existingGpu.Ram = long.TryParse(preram, out long _) ? preram : "0";
                }
            }



            //获取可分区GPU属性
            var result3 = Utils.Run("Get-VMHostPartitionableGpu | select name");
            if (result3.Count > 0)
            {
                foreach (var gpu in result3)
                {
                    string pname = gpu.Members["Name"]?.Value.ToString();
                    var existingGpu = gpuList.FirstOrDefault(g => pname.ToUpper().Contains(g.InstanceId.Replace("\\", "#")));  // 找到 pname 对应的显卡
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
            //获取VM信息
            var vms = Utils.Run("Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace");
            
            if (vms.Count > 0)
            {
                foreach (var vm in vms)
                {
                    Dictionary<string, string> gpulist = new(); //存储虚拟机挂载的GPU分区
                    string vmname = vm.Members["VMName"]?.Value.ToString();
                    string highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value.ToString();
                    string guest = vm.Members["GuestControlledCacheTypes"]?.Value.ToString();

                    //马上查询虚拟机GPU虚拟化信息
                    var vmgpus = Utils.Run($@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id");
                    if (vmgpus.Count > 0)
                    {
                        foreach (var gpu in vmgpus)
                        {
                            string gpupath = gpu.Members["InstancePath"]?.Value.ToString();
                            string gpuid = gpu.Members["Id"]?.Value.ToString();
                            gpulist[gpuid] = gpupath; //存入字典，id是不同的，但是path如果来自同一个GPU则可能相同
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

        //gpuList.Add(new GPUInfo("Microsoft Hyper-V Video", "True", "Microsoft", "test", null, null, "V114514"));
        //gpuList.Add(new GPUInfo("Moore Threads MTT S80", "True", "Moore", "test", null, null, "V114514"));
        //gpuList.Add(new GPUInfo("NVIDIA RTX 4090", "True", "NVIDIA", "test", null, null, "V114514"));
        //gpuList.Add(new GPUInfo("Radeon RX 9070 XT", "True", "Advanced", "test", null, null, "V114514"));
        //gpuList.Add(new GPUInfo("Qualcomm® Adreno™ GPU X1E-80-100", "True", "Qualcomm", "test", null, null, "V114514"));
        //gpuList.Add(new GPUInfo("DisplayLink DL-6950", "True", "DisplayLink", "test", null, null, "V114514"));
        //gpuList.Add(new GPUInfo("Silicon SM768", "True", "Silicon", "test", null, null, "V114514"));

        foreach (var gpu in gpuList)
        {
            string name = string.IsNullOrEmpty(gpu.Name) ? Properties.Resources.none : gpu.Name;
            string valid = string.IsNullOrEmpty(gpu.Valid) ? Properties.Resources.none : gpu.Valid;
            string manu = string.IsNullOrEmpty(gpu.Manu) ? Properties.Resources.none : gpu.Manu;
            string instanceId = string.IsNullOrEmpty(gpu.InstanceId) ? Properties.Resources.none : gpu.InstanceId;
            string pname = string.IsNullOrEmpty(gpu.Pname) ? Properties.Resources.none : gpu.Pname; //GPU分区路径
            string driverversion = string.IsNullOrEmpty(gpu.DriverVersion) ? Properties.Resources.none : gpu.DriverVersion;
            string gpup = ExHyperV.Properties.Resources.notsupport; //是否支持GPU分区
            string vendor = string.IsNullOrEmpty(gpu.Vendor) ? Properties.Resources.none : gpu.Vendor;

            string ram = (string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram)) / (1024 * 1024) + " MB";
            if (manu.Contains("Moore")) {
                ram = (string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram)) / 1024 + " MB";
            }//摩尔线程的显存记录在HardwareInformation.MemorySize，但是单位是KB
            

            if (valid != "True") { continue; } //剔除未连接的显卡
            if (hyperv == false) {gpup = ExHyperV.Properties.Resources.needhyperv;} 
            if (pname != Properties.Resources.none) {gpup = ExHyperV.Properties.Resources.support;}
            
            Dispatcher.Invoke(() =>
            {
                var rowCount = main.RowDefinitions.Count; // 获取当前行数
                main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var cardExpander = Utils.CardExpander2();
                Grid.SetRow(cardExpander, rowCount);
                cardExpander.Padding = new Thickness(8); //调整间距

                var grid = new Grid();
                grid.HorizontalAlignment = HorizontalAlignment.Stretch;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                //创建图标
                var image = Utils.CreateGpuImage(manu,name,64);
                Grid.SetColumn(image, 0);
                grid.Children.Add(image);

                // 显卡型号
                var gpuname = Utils.CreateStackPanel(name);
                Grid.SetColumn(gpuname, 1);
                grid.Children.Add(gpuname);
                cardExpander.Header = grid;
                
                //详细数据
                var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
                var grid2 = new Grid();
                // 定义 Grid 的列和行
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
                    (ExHyperV.Properties.Resources.manu, 0, 0),(vendor, 0, 1),
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

            //UI更新
            Dispatcher.Invoke(() =>
            {
                //vms作为所有VM的父节点
                //cardExpander作为VM节点

                var rowCount = vms.RowDefinitions.Count; // 获取当前行数
                vms.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var cardExpander = Utils.CardExpander2();
                Grid.SetRow(cardExpander, rowCount); //设定VM节点的行数
                cardExpander.Padding = new Thickness(10,8,10,8); //调整间距

                cardExpander.Icon = Utils.FontIcon(24, "\xE7F4"); //虚拟机图标

                //VM右侧内容，分为名称和按钮。
                var grid1 = new Grid(); //虚拟机右侧部分，容纳文字和按钮
                grid1.HorizontalAlignment = HorizontalAlignment.Stretch;
                grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid1.Height = 30;

                // VM的名称
                var vmname = Utils.TextBlock1(name);
                grid1.Children.Add(vmname);

                //添加按钮
                var addbutton = new Wpf.Ui.Controls.Button
                {
                    Content = ExHyperV.Properties.Resources.addgpu,
                    Margin = new Thickness(0, 0, 5, 0),
                };
                addbutton.Click += (sender, e) => Gpu_mount(sender, e, name, Hostgpulist); //按钮点击事件

                Grid.SetColumn(addbutton, 1);
                grid1.Children.Add(addbutton);

                cardExpander.Header = grid1;
                if (GPUs!= null && GPUs.Count != 0) { 
                    cardExpander.IsExpanded = true; //显卡列表不为空时展开虚拟机详情
                }

                //以下是VM的下拉内容部分，也就是GPU列表
                var GPUcontent = new Grid(); //GPU列表节点
                foreach (var gpu in GPUs)
                {
                    var gpupath = gpu.Value;
                    var gpuid = gpu.Key;

                    GPUcontent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var gpuExpander = new CardExpander
                    {
                        ContentPadding = new Thickness(6),
                    };
                    Grid.SetRow(gpuExpander, GPUcontent.RowDefinitions.Count); //获取并设置GPU列表节点当前行数
                    gpuExpander.Padding = new Thickness(10, 8, 10, 8); //调整间距

                    var grid0 = new Grid(); //GPU的头部，用Grid来容纳自定义icon
                    grid0.HorizontalAlignment = HorizontalAlignment.Stretch;
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    //显卡名称
                    var thegpu = Hostgpulist.FirstOrDefault(g => g.Pname == gpupath); //选中联机显卡中的这张显卡
                    string name = thegpu.Name;
                    var gpuname = Utils.CreateStackPanel(name);
                    Grid.SetColumn(gpuname, 1);
                    grid0.Children.Add(gpuname);

                    //显卡图标
                    var gpuimage = Utils.CreateGpuImage(thegpu.Manu,thegpu.Name,32);
                    Grid.SetColumn(gpuimage, 0);
                    grid0.Children.Add(gpuimage);

                    //删除按钮
                    var button = new Wpf.Ui.Controls.Button {
                        Content = ExHyperV.Properties.Resources.uninstall,
                        Margin = new Thickness(0,0,5,0),
                    };
                    button.Click += (sender, e) => Gpu_dismount(sender, e, gpuid, vm.Name, gpuExpander); //按钮点击事件

                    Grid.SetColumn(button, 2);
                    grid0.Children.Add(button);
                    gpuExpander.Header = grid0;
                    
                    //GPU适配器详细数据，一个Panel来存放文字信息
                    var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
                    var grid2 = new Grid();
                    // 定义 Grid 的列和行
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
        Dispatcher.Invoke(() => {progressbar.Visibility = Visibility.Collapsed; });//隐藏加载条
    }

    private void Gpu_dismount(object sender, RoutedEventArgs e,string id ,string vmname, CardExpander gpuExpander)
    {
        PowerShell ps = PowerShell.Create();
        //删除指定的GPU适配器
        ps.AddScript($@"Remove-VMGpuPartitionAdapter -VMName '{vmname}' -AdapterId '{id}'");
        var result = ps.Invoke();
        // 检查是否有错误
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
            parentGrid.Children.Remove(gpuExpander); //需要在成功执行后进行。
        }
    }

    private void Gpu_mount(object sender, RoutedEventArgs e, string vmname, List<GPUInfo> Hostgpulist)
    {
        ChooseGPUWindow selectItemWindow = new ChooseGPUWindow(vmname,Hostgpulist);
        selectItemWindow.GPUSelected += SelectItemWindow_GPUSelected;
        selectItemWindow.ShowDialog(); //显示GPU适配器选择窗口
        
    }

    private async void Vmrefresh(object sender, RoutedEventArgs e)
    {
        if (!refreshlock) {
            progressbar.Visibility = Visibility.Visible; //显示加载条
            refreshlock = true;
            await GetGpu();
        }
    }

    private async void SelectItemWindow_GPUSelected(object? sender, (string, string) args)
    {
        string GPUname = args.Item1;
        string VMname = args.Item2;

        SnackbarService = new SnackbarService();
        var ms = Application.Current.MainWindow as MainWindow; //获取主窗口

        SnackbarService.SetSnackbarPresenter(ms.SnackbarPresenter);
        SnackbarService.Show(ExHyperV.Properties.Resources.success,GPUname+ExHyperV.Properties.Resources.already+ VMname,ControlAppearance.Success,new SymbolIcon(SymbolRegular.CheckboxChecked24,32), TimeSpan.FromSeconds(2));
        Vmrefresh(null, null);

    }

}




