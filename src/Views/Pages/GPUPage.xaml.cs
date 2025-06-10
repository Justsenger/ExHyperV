using System.Windows;
using System.Windows.Controls;
using ExHyperV.Models;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ExHyperV.Views.Pages;

public partial class GpuPage
{
    private bool _refreshlock;

    private SnackbarService? _snackbarService;

    public GpuPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadGpu();
    }

    private async Task LoadGpu()
    {
        try
        {
            var (gpuList, hyperv) = await Task.Run(LoadGpuInfoAndHypervStatus);
            GpuUi(gpuList, hyperv);
            await LoadVm(gpuList);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show($"Error loading GPU information: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Progressbar.Visibility = Visibility.Collapsed;
            });
        }
    }

    /// <summary>
    ///     Loads GPU information and checks Hyper-V status.
    /// </summary>
    /// <returns>Tuple containing the list of GpuInfo and a bool indicating Hyper-V status.</returns>
    private static (List<GpuInfo> gpuList, bool hyperv) LoadGpuInfoAndHypervStatus()
    {
        // Use factory method to create objects with complete data
        var gpuList = GpuInfo.CreateGpuInfoList();

        //获取HyperV支持状态
        var hypervResult = Utils.Run("Get-Module -ListAvailable -Name Hyper-V");
        var hyperv = hypervResult.Count > 0;

        return (gpuList, hyperv);
    }

    private async Task LoadVm(List<GpuInfo> hostgpuList)
    {
        await Task.Run(() =>
        {
            List<VmInfo> vmList = [];
            //获取VM信息
            var vms = Utils.Run(
                "Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace");

            if (vms.Count > 0)
            {
                var vmData = vms.Select(vm => vm.Members).ToList();
                foreach (var vmMembers in vmData)
                {
                    var gpulist = new Dictionary<string, string>(); //存储虚拟机挂载的GPU分区
                    var vmname = vmMembers["VMName"]?.Value?.ToString();

                    //马上查询虚拟机GPU虚拟化信息
                    if (vmname is not null)
                    {
                        var vmgpus =
                            Utils.Run($"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id");
                        if (vmgpus.Count > 0)
                        {
                            var gpuData = vmgpus.Select(gpu => new
                            {
                                GpuPath = gpu.Members["InstancePath"]?.Value?.ToString(),
                                GpuId = gpu.Members["Id"]?.Value?.ToString()
                            }).Where(x => x.GpuId is not null && x.GpuPath is not null);

                            foreach (var gpu in gpuData)
                                gpulist[gpu.GpuId!] = gpu.GpuPath!;
                        }
                    }

                    vmList.Add(new VmInfo(vmname ?? string.Empty, gpulist));
                }
            }

            Vmui(vmList, hostgpuList);
        });
    }

    private void GpuUi(List<GpuInfo> gpuList, bool hyperv)
    {
        var cardExpanders = new List<UIElement>();
        var rowDefinitions = new List<RowDefinition>();

        foreach (var gpu in gpuList)
        {
            var name = string.IsNullOrEmpty(gpu.Name) ? Properties.Resources.none : gpu.Name;
            var valid = string.IsNullOrEmpty(gpu.Valid) ? Properties.Resources.none : gpu.Valid;
            var manu = string.IsNullOrEmpty(gpu.Manu) ? Properties.Resources.none : gpu.Manu;
            var instanceId = string.IsNullOrEmpty(gpu.InstanceId) ? Properties.Resources.none : gpu.InstanceId;
            var pname = string.IsNullOrEmpty(gpu.Pname) ? Properties.Resources.none : gpu.Pname; //GPU分区路径
            var driverversion = string.IsNullOrEmpty(gpu.DriverVersion) ? Properties.Resources.none : gpu.DriverVersion;
            var gpup = Properties.Resources.notsupport; //是否支持GPU分区

            string ram;
            try
            {
                var ramValue = string.IsNullOrEmpty(gpu.Ram) ? 0 : long.Parse(gpu.Ram);
                if (manu.Contains("Moore"))
                    ram = ramValue / 1024 + " MB"; //摩尔线程的显存记录在HardwareInformation.MemorySize，但是单位是KB
                else
                    ram = ramValue / (1024 * 1024) + " MB";
            }
            catch (FormatException)
            {
                ram = Properties.Resources.none; // Use localized value for "unknown"
            }
            catch (OverflowException)
            {
                ram = Properties.Resources.none;
            }

            if (!bool.TryParse(valid, out var isValid) || !isValid) continue; //剔除未连接的显卡

            if (!hyperv) gpup = Properties.Resources.needhyperv;
            if (pname != Properties.Resources.none) gpup = Properties.Resources.support;

            var cardExpander = Utils.CardExpander2();
            cardExpander.Padding = new Thickness(8); //调整间距

            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
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

            var textData = new[]
            {
                new { Text = Properties.Resources.manu, Row = 0, Column = 0 },
                new { Text = manu, Row = 0, Column = 1 },
                new { Text = Properties.Resources.ram, Row = 1, Column = 0 },
                new { Text = ram, Row = 1, Column = 1 },
                new { Text = Properties.Resources.Instanceid, Row = 2, Column = 0 },
                new { Text = instanceId, Row = 2, Column = 1 },
                new { Text = Properties.Resources.gpupv, Row = 3, Column = 0 },
                new { Text = gpup, Row = 3, Column = 1 },
                new { Text = Properties.Resources.gpupvpath, Row = 4, Column = 0 },
                new { Text = pname, Row = 4, Column = 1 },
                new { Text = Properties.Resources.driverversion, Row = 5, Column = 0 },
                new { Text = driverversion, Row = 5, Column = 1 }
            };

            foreach (var item in textData)
            {
                var textBlock = Utils.CreateGridTextBlock(item.Text, item.Row, item.Column);
                grid2.Children.Add(textBlock);
            }

            contentPanel.Children.Add(grid2);
            cardExpander.Content = contentPanel;

            cardExpanders.Add(cardExpander);
            rowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Dispatcher.Invoke(() =>
        {
            Main.Children.Clear();
            Main.RowDefinitions.Clear();
            for (var i = 0; i < cardExpanders.Count; i++)
            {
                Main.RowDefinitions.Add(rowDefinitions[i]);
                Grid.SetRow(cardExpanders[i], i);
                Main.Children.Add(cardExpanders[i]);
            }
        });
    }

    private void Vmui(List<VmInfo> vmList, List<GpuInfo> hostgpulist)
    {
        var vmCardExpanders = new List<UIElement>();
        var vmRowDefinitions = new List<RowDefinition>();

        foreach (var (name, gpUs) in vmList)
        {
            // Creating UI elements in background thread
            var cardExpander = Utils.CardExpander2();
            cardExpander.Padding = new Thickness(10, 8, 10, 8); //调整间距

            cardExpander.Icon = Utils.FontIcon(24, "\xE7F4"); //虚拟机图标

            //VM右侧内容，分为名称和按钮。
            var grid1 = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            }; //虚拟机右侧部分，容纳文字和按钮
            grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid1.Height = 30;

            // VM的名称
            var vmname = Utils.CreateHeaderTextBlock(name);
            grid1.Children.Add(vmname);

            //添加按钮
            var addbutton = new Button
            {
                Content = Properties.Resources.addgpu,
                Margin = new Thickness(0, 0, 5, 0)
            };
            addbutton.Click += (_, _) => Gpu_mount(name, hostgpulist); //按钮点击事件

            Grid.SetColumn(addbutton, 1);
            grid1.Children.Add(addbutton);

            cardExpander.Header = grid1;
            if (gpUs.Count > 0) cardExpander.IsExpanded = true; //显卡列表不为空时展开虚拟机详情

            //以下是VM的下拉内容部分，也就是GPU列表
            var gpUcontent = new Grid(); //GPU列表节点
            foreach (var (gpuid, gpupath) in gpUs)
            {
                gpUcontent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var gpuExpander = new CardExpander
                {
                    ContentPadding = new Thickness(6)
                };
                Grid.SetRow(gpuExpander, gpUcontent.RowDefinitions.Count); //获取并设置GPU列表节点当前行数
                gpuExpander.Padding = new Thickness(10, 8, 10, 8); //调整间距

                var grid0 = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch
                }; //GPU的头部，用Grid来容纳自定义icon
                grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                //显卡名称
                var thegpu = hostgpulist.FirstOrDefault(x => x.Pname == gpupath); //选中联机显卡中的这张显卡
                var gpuName = thegpu?.Name ?? "Unknown GPU";
                var gpuNamePanel = Utils.CreateStackPanel(gpuName);
                Grid.SetColumn(gpuNamePanel, 1);
                grid0.Children.Add(gpuNamePanel);

                //显卡图标
                var gpuimage = Utils.CreateGpuImage(thegpu?.Manu ?? "Unknown", thegpu?.Name ?? "Unknown", 32);
                Grid.SetColumn(gpuimage, 0);
                grid0.Children.Add(gpuimage);

                //删除按钮
                var button = new Button
                {
                    Content = Properties.Resources.uninstall,
                    Margin = new Thickness(0, 0, 5, 0)
                };
                button.Click += (_, _) => Gpu_dismount(gpuid, name, gpuExpander); //按钮点击事件

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

                var textData = new[]
                {
                    new { Text = Properties.Resources.gpupvid, Row = 0, Column = 0 },
                    new { Text = gpuid, Row = 0, Column = 1 },
                    new { Text = Properties.Resources.gpupvpath, Row = 1, Column = 0 },
                    new { Text = gpupath, Row = 1, Column = 1 }
                };

                foreach (var item in textData)
                {
                    var textBlock = Utils.CreateGridTextBlock(item.Text, item.Row, item.Column);
                    grid2.Children.Add(textBlock);
                }

                contentPanel.Children.Add(grid2);
                gpuExpander.Content = contentPanel;
                gpUcontent.Children.Add(gpuExpander);
            }

            cardExpander.Content = gpUcontent;
            vmCardExpanders.Add(cardExpander);
            vmRowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // Single UI thread access
        Dispatcher.Invoke(() =>
        {
            Vms.Children.Clear();
            Vms.RowDefinitions.Clear();
            for (var i = 0; i < vmCardExpanders.Count; i++)
            {
                Vms.RowDefinitions.Add(vmRowDefinitions[i]);
                Grid.SetRow(vmCardExpanders[i], i);
                Vms.Children.Add(vmCardExpanders[i]);
            }

            _refreshlock = false;
            Progressbar.Visibility = Visibility.Collapsed; //隐藏加载条
        });
    }

    private static void Gpu_dismount(string id, string vmname, CardExpander gpuExpander)
    {
        //删除指定的GPU适配器
        var result = Utils.RunWithErrors($"Remove-VMGpuPartitionAdapter -VMName '{vmname}' -AdapterId '{id}'");

        // 检查是否有错误
        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors) MessageBox.Show($"Error: {error}");
        }
        else
        {
            var parentGrid = (Grid)gpuExpander.Parent;
            parentGrid.Children.Remove(gpuExpander); //需要在成功执行后进行。
        }
    }

    private void Gpu_mount(string vmname, List<GpuInfo> hostgpulist)
    {
        var selectItemWindow = new ChooseGpuWindow(vmname, hostgpulist);
        selectItemWindow.GpuSelected += SelectItemWindow_GpuSelected;
        selectItemWindow.ShowDialog(); //显示GPU适配器选择窗口
    }

    private async void VmRefresh(object _, RoutedEventArgs __)
    {
        try
        {
            if (_refreshlock) return;
            Progressbar.Visibility = Visibility.Visible; //显示加载条
            _refreshlock = true;

            await LoadGpu();
        }
        catch (Exception ex)
        {
            try
            {
                MessageBox.Show($"Error refreshing: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Progressbar.Visibility = Visibility.Collapsed;
                _refreshlock = false;
            }
            catch
            {
                // If even showing error dialog failed, just reset state
                // to avoid application crash
                _refreshlock = false;
                try
                {
                    Progressbar.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    // Ignore UI errors in critical situation
                }
            }
        }
    }

    private void SelectItemWindow_GpuSelected(object? sender, GpuSelectedEventArgs args)
    {
        var gpuName = args.Name;
        var vmName = args.MachineName;

        _snackbarService = new SnackbarService();
        var ms = Application.Current.MainWindow as MainWindow; //获取主窗口

        if (ms?.SnackbarPresenter is not null)
        {
            _snackbarService.SetSnackbarPresenter(ms.SnackbarPresenter);
            _snackbarService.Show(
                Properties.Resources.success,
                gpuName + Properties.Resources.already + vmName,
                ControlAppearance.Success,
                new SymbolIcon(SymbolRegular.CheckboxChecked24, 32),
                TimeSpan.FromSeconds(2));
        }

        VmRefresh(this, new RoutedEventArgs());
    }
}