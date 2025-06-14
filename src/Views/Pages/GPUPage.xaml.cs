using System.Management.Automation;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace ExHyperV.Views.Pages;

public partial class GPUPage
{
    public bool refreshlock;

    public GPUPage()
    {
        InitializeComponent();
        GetGpu();
    }

    public ISnackbarService SnackbarService { get; set; }

    public async void GetGpu()
    {
        await Task.Run(() =>
        {
            List<GPUInfo> gpuList = [];
            //获取目前联机的显卡
            var gpuLinkedResult = Utils.RunWithErrorHandling(
                "Get-WmiObject -Class Win32_VideoController | select PNPDeviceID,name,AdapterCompatibility,DriverVersion");
            if (gpuLinkedResult.HasErrors)
            {
                gpuLinkedResult.ShowErrorsToUser();
                return;
            }

            var gpulinked = gpuLinkedResult.Output;
            if (gpulinked.Count > 0)
                foreach (var gpu in gpulinked)
                {
                    var name = gpu.Members["name"]?.Value.ToString();
                    var instanceId = gpu.Members["PNPDeviceID"]?.Value.ToString();
                    var Manu = gpu.Members["AdapterCompatibility"]?.Value.ToString();
                    var DriverVersion = gpu.Members["DriverVersion"]?.Value.ToString();
                    gpuList.Add(new GPUInfo(name, "True", Manu, instanceId, null, null, DriverVersion));
                }

            //获取HyperV支持状态
            var hypervResult = Utils.RunWithErrorHandling("Get-Module -ListAvailable -Name Hyper-V");
            if (hypervResult.HasErrors)
            {
                hypervResult.ShowErrorsToUser();
                return;
            }

            var hyperv = hypervResult.Output.Count > 0;

            //获取N卡和I卡显存

            var script =
                @"Get-ItemProperty -Path ""HKLM:\SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0*"" -ErrorAction SilentlyContinue |
            Select-Object MatchingDeviceId,
                  @{Name=""MemorySize""; Expression={
                      if ($_.""HardwareInformation.qwMemorySize"") {
                          $_.""HardwareInformation.qwMemorySize""
                      } elseif ($_.""HardwareInformation.MemorySize"" -is [byte[]]) {
                          $hexString = [BitConverter]::ToString($_.""HardwareInformation.MemorySize"").Replace(""-"", """")
                          $memoryValue = [Convert]::ToInt64($hexString, 16)
                          $memoryValue * 16 * 1024 * 1024
                      } else {
                          $_.""HardwareInformation.MemorySize""
                      }
                  }} |
            Where-Object { $_.MemorySize -ne $null }";
            var gpuRamResult = Utils.RunWithErrorHandling(script);
            if (gpuRamResult.HasErrors)
            {
                gpuRamResult.ShowErrorsToUser();
                return;
            }

            var gpuram = gpuRamResult.Output;
            if (gpuram.Count > 0)
                // 遍历所有联机显卡
                foreach (var existingGpu in gpuList)
                {
                    // 在显存信息中寻找匹配项
                    var matchedGpu = gpuram.FirstOrDefault(g =>
                    {
                        var id = g.Members["MatchingDeviceId"]?.Value?.ToString().ToUpper();
                        return !string.IsNullOrEmpty(id) && existingGpu.InstanceId.Contains(id);
                    });

                    // 更新显存信息或设置默认值
                    existingGpu.Ram = matchedGpu?.Members["MemorySize"]?.Value?.ToString() ?? "0";
                }


            //获取可分区GPU属性
            var partitionableGpuResult = Utils.RunWithErrorHandling("Get-VMHostPartitionableGpu | select name");
            if (partitionableGpuResult.HasErrors)
            {
                partitionableGpuResult.ShowErrorsToUser();
                return;
            }

            var result3 = partitionableGpuResult.Output;
            if (result3.Count > 0)
                foreach (var gpu in result3)
                {
                    var pname = gpu.Members["Name"]?.Value.ToString();
                    var existingGpu =
                        gpuList.FirstOrDefault(g =>
                            pname.ToUpper().Contains(g.InstanceId.Replace("\\", "#"))); // 找到 pname 对应的显卡
                    if (existingGpu != null) existingGpu.Pname = pname;
                }

            GpuUI(gpuList, hyperv);
            GetVM(gpuList);
        });
    }

    public async void GetVM(List<GPUInfo> HostgpuList)
    {
        await Task.Run(() =>
        {
            List<VMInfo> vmList = [];
            //获取VM信息
            var vmsResult = Utils.RunWithErrorHandling(
                "Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace");
            if (vmsResult.HasErrors)
            {
                vmsResult.ShowErrorsToUser();
                return;
            }

            var vms = vmsResult.Output;

            if (vms.Count > 0)
                foreach (var vm in vms)
                {
                    Dictionary<string, string> gpulist = new(); //存储虚拟机挂载的GPU分区
                    var vmname = vm.Members["VMName"]?.Value.ToString();
                    var highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value.ToString();
                    var guest = vm.Members["GuestControlledCacheTypes"]?.Value.ToString();

                    //马上查询虚拟机GPU虚拟化信息
                    var vmGpusResult =
                        Utils.RunWithErrorHandling(
                            $@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id");
                    if (vmGpusResult.HasErrors)
                    {
                        vmGpusResult.ShowErrorsToUser();
                        continue;
                    }

                    var vmgpus = vmGpusResult.Output;
                    if (vmgpus.Count > 0)
                        foreach (var gpu in vmgpus)
                        {
                            var gpupath = gpu.Members["InstancePath"]?.Value.ToString();
                            var gpuid = gpu.Members["Id"]?.Value.ToString();
                            gpulist[gpuid] = gpupath; //存入字典，id是不同的，但是path如果来自同一个GPU则可能相同
                        }

                    vmList.Add(new VMInfo(vmname, null, highmmio, guest, gpulist));
                }

            VMUI(vmList, HostgpuList);
        });
    }

    private void GpuUI(List<GPUInfo> gpuList, bool hyperv)
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
            var noneText = LocalizationHelper.GetString("none");
            var name = string.IsNullOrEmpty(gpu.Name) ? noneText : gpu.Name;
            var valid = string.IsNullOrEmpty(gpu.Valid) ? noneText : gpu.Valid;
            var manu = string.IsNullOrEmpty(gpu.Manu) ? noneText : gpu.Manu;
            var instanceId = string.IsNullOrEmpty(gpu.InstanceId) ? noneText : gpu.InstanceId;
            var pname = string.IsNullOrEmpty(gpu.Pname) ? noneText : gpu.Pname; //GPU分区路径
            var driverversion = string.IsNullOrEmpty(gpu.DriverVersion) ? noneText : gpu.DriverVersion;
            var gpup = LocalizationHelper.GetString("notsupport"); //是否支持GPU分区

            var ram = (string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram)) / (1024 * 1024) + " MB";
            if (manu.Contains("Moore"))
                ram = (string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram)) / 1024 +
                      " MB"; //摩尔线程的显存记录在HardwareInformation.MemorySize，但是单位是KB

            if (valid != "True") continue; //剔除未连接的显卡

            if (hyperv == false)
                gpup = LocalizationHelper.GetString("needhyperv");
            if (pname != noneText)
                gpup = LocalizationHelper.GetString("support");

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
                var image = Utils.CreateGpuImage(manu, name, 64);
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
                    (LocalizationHelper.GetString("manu"), 0, 0),
                    (manu, 0, 1),
                    (LocalizationHelper.GetString("ram"), 1, 0),
                    (ram, 1, 1),
                    (LocalizationHelper.GetString("Instanceid"), 2, 0),
                    (instanceId, 2, 1),
                    (LocalizationHelper.GetString("gpupv"), 3, 0),
                    (gpup, 3, 1),
                    (LocalizationHelper.GetString("gpupvpath"), 4, 0),
                    (pname, 4, 1),
                    (LocalizationHelper.GetString("driverversion"), 5, 0),
                    (driverversion, 5, 1)
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
            var name = vm.Name;
            var low = vm.LowMMIO;
            var high = vm.HighMMIO;
            var guest = vm.GuestControlled;
            var GPUs = vm.GPUs;

            //UI更新
            Dispatcher.Invoke(() =>
            {
                //vms作为所有VM的父节点
                //cardExpander作为VM节点

                var rowCount = vms.RowDefinitions.Count; // 获取当前行数
                vms.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var cardExpander = Utils.CardExpander2();
                Grid.SetRow(cardExpander, rowCount); //设定VM节点的行数
                cardExpander.Padding = new Thickness(10, 8, 10, 8); //调整间距

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
                var addbutton = new Button
                {
                    Content = LocalizationHelper.GetString("addgpu"),
                    Margin = new Thickness(0, 0, 5, 0)
                };
                addbutton.Click += (sender, e) => Gpu_mount(sender, e, name, Hostgpulist); //按钮点击事件

                Grid.SetColumn(addbutton, 1);
                grid1.Children.Add(addbutton);

                cardExpander.Header = grid1;
                if (GPUs != null && GPUs.Count != 0) cardExpander.IsExpanded = true; //显卡列表不为空时展开虚拟机详情

                //以下是VM的下拉内容部分，也就是GPU列表
                var GPUcontent = new Grid(); //GPU列表节点
                foreach (var gpu in GPUs)
                {
                    var gpupath = gpu.Value;
                    var gpuid = gpu.Key;

                    GPUcontent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var gpuExpander = new CardExpander
                    {
                        ContentPadding = new Thickness(6)
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
                    var gpuName = thegpu.Name;
                    var gpuname = Utils.CreateStackPanel(gpuName);
                    Grid.SetColumn(gpuname, 1);
                    grid0.Children.Add(gpuname);

                    //显卡图标
                    var gpuimage = Utils.CreateGpuImage(thegpu.Manu, thegpu.Name, 32);
                    Grid.SetColumn(gpuimage, 0);
                    grid0.Children.Add(gpuimage);

                    //删除按钮
                    var button = new Button
                    {
                        Content = LocalizationHelper.GetString("uninstall"),
                        Margin = new Thickness(0, 0, 5, 0)
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
                        (LocalizationHelper.GetString("gpupvid"), 0, 0),
                        (gpuid, 0, 1),
                        (LocalizationHelper.GetString("gpupvpath"), 1, 0),
                        (gpupath, 1, 1)
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
        Dispatcher.Invoke(() => { progressbar.Visibility = Visibility.Collapsed; }); //隐藏加载条
    }

    private void Gpu_dismount(object sender, RoutedEventArgs e, string id, string vmname, CardExpander gpuExpander)
    {
        var ps = PowerShell.Create();
        //删除指定的GPU适配器
        ps.AddScript($@"Remove-VMGpuPartitionAdapter -VMName '{vmname}' -AdapterId '{id}'");
        var result = ps.Invoke();
        // 检查是否有错误
        if (ps.Streams.Error.Count > 0)
        {
            foreach (var error in ps.Streams.Error)
            {
                var errorText = LocalizationHelper.GetString("error");
                MessageBox.Show($"{errorText}: {error}");
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
        var selectItemWindow = new ChooseGPUWindow(vmname, Hostgpulist);
        selectItemWindow.GPUSelected += SelectItemWindow_GPUSelected;
        selectItemWindow.ShowDialog(); //显示GPU适配器选择窗口
    }

    private void vmrefresh(object sender, RoutedEventArgs e)
    {
        if (!refreshlock)
        {
            progressbar.Visibility = Visibility.Visible; //显示加载条
            refreshlock = true;
            GetGpu();
        }
    }

    private async void SelectItemWindow_GPUSelected(object sender, (string, string) args)
    {
        var GPUname = args.Item1;
        var VMname = args.Item2;

        SnackbarService = new SnackbarService();
        var ms = Application.Current.MainWindow as MainWindow; //获取主窗口

        SnackbarService.SetSnackbarPresenter(ms.SnackbarPresenter);
        var successText = LocalizationHelper.GetString("success");
        var alreadyText = LocalizationHelper.GetString("already");

        SnackbarService.Show(
            successText,
            GPUname + alreadyText + VMname, ControlAppearance.Success,
            new SymbolIcon(SymbolRegular.CheckboxChecked24, 32), TimeSpan.FromSeconds(2));
        vmrefresh(null, null);
    }

    public class GPUInfo
    {
        // 构造函数
        public GPUInfo(string name, string valid, string manu, string instanceId, string pname, string ram,
            string driverversion)
        {
            Name = name;
            Valid = valid;
            Manu = manu;
            InstanceId = instanceId;
            Pname = pname;
            Ram = ram;
            DriverVersion = driverversion;
        }

        public string Name { get; set; } //显卡名称
        public string Valid { get; set; } //是否联机
        public string Manu { get; set; } //厂商
        public string InstanceId { get; set; } //显卡实例id
        public string Pname { get; set; } //可分区的显卡路径
        public string Ram { get; set; } //显存大小
        public string DriverVersion { get; set; } //驱动版本
    }

    public class VMInfo
    {
        // 构造函数
        public VMInfo(string name, string low, string high, string guest, Dictionary<string, string> gpus)
        {
            Name = name;
            LowMMIO = low;
            HighMMIO = high;
            GuestControlled = guest;
            GPUs = gpus;
        }

        public string Name { get; set; } //虚拟机名称
        public string LowMMIO { get; set; } //低位内存空间大小
        public string HighMMIO { get; set; } //高位内存空间大小
        public string GuestControlled { get; set; } //控制缓存
        public Dictionary<string, string> GPUs { get; set; } //存储显卡适配器列表
    }
}