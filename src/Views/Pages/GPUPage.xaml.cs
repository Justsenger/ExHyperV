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
            //获取目前联机的显卡
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

            //获取HyperV支持状态
            bool hyperv = false;
            ps.AddScript("Get-Module -ListAvailable -Name Hyper-V");
            var hypervstatus = ps.Invoke();
            if (hypervstatus.Count != 0) { hyperv = true; }

            //获取N卡和I卡显存
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
返回的内存容量为字节";
            ps.AddScript(script);
            var result2 = ps.Invoke();
            if (result2.Count > 0)
            {
                foreach (var gpu in result2)
                {
                    string driverDesc = gpu.Members["DriverDesc"]?.Value.ToString();
                    string memorySize = gpu.Members["MemorySize"]?.Value.ToString();

                    var existingGpu = gpuList.FirstOrDefault(g => g.Name == driverDesc); //选中联机显卡中的这张显卡
                    if (existingGpu != null) //只有联机显卡才更新
                    {
                        existingGpu.Ram = memorySize;
                    }
                }
            }
            //获取可分区GPU属性
            string script1 = "Get-VMHostPartitionableGpu | select name";
            ps.AddScript(script1);
            var result3 = ps.Invoke();
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
            PowerShell ps = PowerShell.Create();
            //获取VM信息
            string script0 = "Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace";
            ps.AddScript(script0);
            var vms = ps.Invoke();
            
            if (vms.Count > 0)
            {
                foreach (var vm in vms)
                {
                    Dictionary<string, string> gpulist = new Dictionary<string, string>(); //存储虚拟机挂载的GPU分区
                    string vmname = vm.Members["VMName"]?.Value.ToString();

                    string highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value.ToString();
                    string guest = vm.Members["GuestControlledCacheTypes"]?.Value.ToString();
                    //马上查询虚拟机GPU虚拟化信息

                    string script1 = $@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id";
                    ps.AddScript(script1);
                    var vmgpus = ps.Invoke();

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



    public class GPUInfo
    {
        public string Name { get; set; } //显卡名称
        public string Valid { get; set; } //是否联机
        public string Manu { get; set; } //厂商
        public string InstanceId { get; set; } //显卡实例id
        public string Pname { get; set; } //可分区的显卡路径
        public string Ram { get; set; } //显存大小
        public string DriverVersion { get; set; } //驱动版本

        // 构造函数
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
            string name = string.IsNullOrEmpty(gpu.Name) ? "显卡名称缺失" : gpu.Name;
            string valid = string.IsNullOrEmpty(gpu.Valid) ? "无" : gpu.Valid;
            string manu = string.IsNullOrEmpty(gpu.Manu) ? "无" : gpu.Manu;
            string instanceId = string.IsNullOrEmpty(gpu.InstanceId) ? "无" : gpu.InstanceId;
            string pname = string.IsNullOrEmpty(gpu.Pname) ? "无" : gpu.Pname; //GPU分区路径
            string ram = (string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram)) / (1024 * 1024) + " MB";
            string driverversion = string.IsNullOrEmpty(gpu.DriverVersion) ? "无" : gpu.DriverVersion;
            string gpup = "不支持"; //是否支持GPU分区

            if (valid != "True") { continue; } //剔除未连接的显卡
            if (hyperv == false) {gpup = "未安装HyperV";} //还需要检查管理员权限
           
            if (pname != "无") {gpup = "支持";}
            
            Dispatcher.Invoke(() =>
            {
                var rowCount = main.RowDefinitions.Count; // 获取当前行数
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

                //图标
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
                // 设置抗锯齿参数
                image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
                image.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);

                Grid.SetColumn(image, 0);

                // 显卡型号
                var gpuname = CreateStackPanel(name);
                gpuname.Margin = new Thickness(10,0,0,0);
                gpuname.VerticalAlignment = VerticalAlignment.Center;

                Grid.SetColumn(gpuname, 1);

                grid.Children.Add(gpuname);
                grid.Children.Add(image);

                cardExpander.Header = grid;
                
                //详细数据
                var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
                var grid2 = new Grid();
                // 定义 Grid 的列和行
                grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var textBlocks = new TextBlock[]
                {
                new TextBlock { Text = "制造商", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = "专用显存", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },      
                new TextBlock { Text = "实例ID", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = "GPU分区功能", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = "GPU分区路径", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                new TextBlock { Text = "驱动程序版本", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
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
                var row = textBlocks.Length; //有多少行
                for (int i = 0; i < row; i++)
                {
                    grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                }
                for (int i = 0; i < row; i++)
                {
                    Grid.SetRow(textBlocks[i], i);
                    Grid.SetColumn(textBlocks[i], 0); // 第一列
                    grid2.Children.Add(textBlocks[i]);

                    Grid.SetRow(dataTextBlocks[i], i);
                    Grid.SetColumn(dataTextBlocks[i], 1); // 第二列
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


            //UI更新
            Dispatcher.Invoke(() =>
            {
                //vms作为所有VM的父节点
                //cardExpander作为VM节点

                var rowCount = vms.RowDefinitions.Count; // 获取当前行数
                vms.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });


                var cardExpander = new CardExpander
                {
                    Margin = new Thickness(30, 5, 10, 0),
                    ContentPadding = new Thickness(6),
                };
                Grid.SetRow(cardExpander, rowCount); //设定VM节点的行数

                //VM的图标
                var icon = new FontIcon
                {
                    FontSize = 20,
                    FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                    Glyph = "\xE7F4" // 获取图标Unicode
                };
                cardExpander.Icon = icon;


                //VM右侧内容，分为名称和按钮。

                var grid1 = new Grid(); //虚拟机右侧部分，容纳文字和按钮
                grid1.HorizontalAlignment = HorizontalAlignment.Stretch;
                grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // VM的名称
                var vmname = CreateStackPanel(name);
                vmname.Margin = new Thickness(5, 0, 0, 0);
                vmname.VerticalAlignment = VerticalAlignment.Center;
                grid1.Children.Add(vmname);

                //添加按钮
                var button1 = new Wpf.Ui.Controls.Button
                {
                    Content = "添加GPU",
                    Margin = new Thickness(0, 0, 5, 0),
                };
                button1.Click += (sender, e) => Gpu_mount(sender, e, name, Hostgpulist); //按钮点击事件

                Grid.SetColumn(button1, 1);
                grid1.Children.Add(button1);

                cardExpander.Header = grid1;
                cardExpander.IsExpanded = true; //默认展开虚拟机详情

                //以下是VM的下拉内容部分，也就是GPU列表

                var GPUcontent = new Grid(); //GPU列表节点
                foreach (var gpu in GPUs)
                {
                    var gpupath = gpu.Value;
                    var gpuid = gpu.Key;

                    var rowCount2 = GPUcontent.RowDefinitions.Count; // 获取GPU列表节点当前行数
                    GPUcontent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var gpuExpander = new CardExpander
                    {
                        ContentPadding = new Thickness(6),
                    };
                    Grid.SetRow(gpuExpander, rowCount2);

                    var grid0 = new Grid(); //GPU的头部，用Grid来容纳自定义icon
                    grid0.HorizontalAlignment = HorizontalAlignment.Stretch;
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    //显卡名称
                    var thegpu = Hostgpulist.FirstOrDefault(g => g.Pname == gpupath); //选中联机显卡中的这张显卡
                    string name = thegpu.Name;
                    var gpuname = CreateStackPanel(name);
                    gpuname.Margin = new Thickness(10, 0, 0, 0);
                    gpuname.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(gpuname, 1);
                    grid0.Children.Add(gpuname);

                    //显卡图标
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
                    // 设置抗锯齿参数
                    image0.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
                    image0.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);

                    Grid.SetColumn(image0, 0);
                    grid0.Children.Add(image0);

                    //删除按钮
                    var button = new Wpf.Ui.Controls.Button {
                        Content = "卸载",
                        Margin = new Thickness(0,0,5,0),
                    };
                    button.Click += (sender, e) => Gpu_unmount(sender, e, gpuid, vm.Name, gpuExpander); //按钮点击事件

                    Grid.SetColumn(button, 2);
                    grid0.Children.Add(button);

                    gpuExpander.Header = grid0;
                    

                    //GPU适配器详细数据，一个Panel来存放文字信息
                    var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };
                    var grid2 = new Grid();
                    // 定义 Grid 的列和行
                    grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                    grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var textBlocks = new TextBlock[]
                    {
                        new TextBlock { Text = "GPU分区ID", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                        new TextBlock { Text = "GPU分区路径", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                    };
                    var dataTextBlocks = new TextBlock[]
                    {
                        new TextBlock { Text = gpuid, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                        new TextBlock { Text = gpupath, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
                    };
                    var row = textBlocks.Length; //有多少行
                    for (int i = 0; i < row; i++)
                    {
                        grid2.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                    }
                    for (int i = 0; i < row; i++)
                    {
                        Grid.SetRow(textBlocks[i], i);
                        Grid.SetColumn(textBlocks[i], 0); // 第一列
                        grid2.Children.Add(textBlocks[i]);

                        Grid.SetRow(dataTextBlocks[i], i);
                        Grid.SetColumn(dataTextBlocks[i], 1); // 第二列
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

        Dispatcher.Invoke(() => {progressbar.Visibility = Visibility.Collapsed; });//隐藏加载条

    }

    private void Gpu_unmount(object sender, RoutedEventArgs e,string id ,string vmname, CardExpander gpuExpander)
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
                System.Windows.MessageBox.Show($"执行出错: {error.ToString()}");
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


    private void vmrefresh(object sender, RoutedEventArgs e)
    {
        if (!refreshlock) {
            progressbar.Visibility = Visibility.Visible; //显示加载条
            refreshlock = true;
            GetGpu();
        }
    }


    private async void SelectItemWindow_GPUSelected(object sender, (string, string) args)
    {
        string GPUname = args.Item1;
        string VMname = args.Item2;

        

        snackbarService = new SnackbarService();
        var ms = Application.Current.MainWindow as MainWindow; //获取主窗口

        snackbarService.SetSnackbarPresenter(ms.SnackbarPresenter);
        snackbarService.Show("成功",GPUname+"已分配到"+ VMname,ControlAppearance.Success,new SymbolIcon(SymbolRegular.CheckboxChecked24,32), TimeSpan.FromSeconds(2));
        //await Task.Delay(2000); // 等待Snackbar显示2秒再尝试更新UI，防止界面更新发生冲突
        vmrefresh(null, null);

    }

}




