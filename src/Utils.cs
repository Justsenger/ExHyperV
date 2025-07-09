using System.Collections.ObjectModel;
using System.Data.Common;
using System.Dynamic;
using System.Management.Automation;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Wpf.Ui.Controls;
using Image = Wpf.Ui.Controls.Image;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using WriteText = Wpf.Ui.Controls.TextBox;

namespace ExHyperV.Views.Pages;


public partial class Utils
{

    public static Collection<PSObject>? Run(string script)
    {
        using (PowerShell ps = PowerShell.Create())
        {
            ps.AddScript(script);
            try
            {
                var results = ps.Invoke();
                if (ps.HadErrors)
                {
                    var errorBuilder = new StringBuilder();
                    errorBuilder.AppendLine("PowerShell 执行时遇到错误：\n");
                    foreach (var error in ps.Streams.Error)
                    {
                        errorBuilder.AppendLine($"- {error.Exception.Message}");
                    }
                    Show(errorBuilder.ToString());
                    return null;
                }
                return results;
            }
            catch (Exception ex)
            {
                Show($"执行 PowerShell 时发生严重系统异常：\n\n{ex.Message}");
                return null;
            }
        }
    }
    public static Collection<PSObject> Run2(string script)
    {
        PowerShell ps = PowerShell.Create();
        ps.AddScript(script);

        Collection<PSObject> output = null;

        try
        {
            // 执行脚本
            output = ps.Invoke();

            // 如果存在错误，将错误信息打印出来
            if (ps.HadErrors)
            {
                System.Windows.MessageBox.Show("错误:");
                foreach (var error in ps.Streams.Error)
                {
                    System.Windows.MessageBox.Show($"错误消息: {error.Exception.Message}");
                    System.Windows.MessageBox.Show($"错误详细信息: {error.Exception.StackTrace}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"执行 PowerShell 脚本时发生异常: {ex.Message}");
            System.Windows.MessageBox.Show($"堆栈信息: {ex.StackTrace}");
        }

        return output;

    }
    public static CardExpander CardExpander1()
    {
        var cardExpander = new CardExpander
        {
            Margin = new Thickness(20, 5, 0, 0),
            ContentPadding = new Thickness(6),
        };

        return cardExpander;
    }

    public static CardExpander CardExpander2()
    {
        var cardExpander = new CardExpander
        {
            Margin = new Thickness(30, 5, 10, 0),
            ContentPadding = new Thickness(6),
        };

        return cardExpander;
    }

    public static string GetIconPath(string deviceType, string friendlyName)
    {
        switch (deviceType)
        {
            case "Switch":
                return "\xE990";  // 交换机图标 
            case "Upstream":     
                return "\uE774";  // 地球/上游网络图标
            case "Display":
                return "\xF211";  // 显卡图标 
            case "Net":
                return "\xE839";  // 网络图标
            case "USB":
                return friendlyName.Contains("USB4")
                    ? "\xE945"    // 雷电接口图标
                    : "\xECF0";   // 普通USB图标
            case "HIDClass":
                return "\xE928";  // HID设备图标
            case "SCSIAdapter":
            case "HDC":
                return "\xEDA2";  // 存储控制器图标
            default:
                return friendlyName.Contains("Audio")
                    ? "\xE995"     // 音频设备图标
                    : "\xE950";    // 默认图标
        }
    }


    public static FontIcon FontIcon1(string classType, string friendlyName) {
        var icon = new FontIcon
        {
            FontSize = 24,
            FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
            Glyph = GetIconPath(classType, friendlyName) // 获取图标Unicode
        };
        return icon;
    }
    public static TextBlock TextBlock1(string friendlyName) {
        var headerText = new TextBlock
        {
            Text = friendlyName,
            FontSize = 16,
            Margin = new System.Windows.Thickness(0,-2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        return headerText;
    }

    public static TextBlock TextBlock12(string friendlyName)
    {
        var headerText = new TextBlock
        {
            Text = friendlyName,
            FontSize = 12,
            Margin = new System.Windows.Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return headerText;
    }

    public static WriteText WriteTextBlock(string friendlyName)
    {
        var headerText = new WriteText
        {
            Text = friendlyName,
            FontSize = 16,
            Margin = new System.Windows.Thickness(0, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        return headerText;
    }



    public static DropDownButton DropDownButton1(string status) {
        var Menu = new DropDownButton
        {
            Content = status,
            Margin = new Thickness(10, 0, 5, 0),
        };
        return Menu;
    }

    public static TextBlock TextBlock3(string friendlyName)
    {
        var headerText = new TextBlock
        {
            Text = friendlyName,
            FontSize = 16,
            Margin = new System.Windows.Thickness(0, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap // 允许文本换行
        };
        return headerText;
    }


    public static TextBlock TextBlock2(string text,int row,int column)
    {
        var textBlock = new TextBlock { Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        Grid.SetRow(textBlock, row);
        Grid.SetColumn(textBlock, column);

        return textBlock;
    }

    public static WriteText WriteTextBlock2(string text, int row, int column)
    {
        var textBlock = new WriteText { Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        Grid.SetRow(textBlock, row);
        Grid.SetColumn(textBlock, column);

        return textBlock;
    }


    public static StackPanel CreateStackPanel(string text)
    {
        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        stackPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });

        return stackPanel;
    }


    public static string GetGpuImagePath(string Manu,string name)
    {
        string imageName;

        // 根据 Manu 设置不同的图片文件名
        if (Manu.Contains("NVIDIA")) // 如果是 NVIDIA 显卡，使用 NVIDIA 的图片
        {
            imageName = "NVIDIA.png";
        }
        else if (Manu.Contains("Advanced")) //"Advanced Micro Devices, Inc."
        {
            imageName = "AMD.png";
        }
        else if (Manu.Contains("Microsoft")) //"Microsoft"
        {
            imageName = "Microsoft.png";
        }
        else if (Manu.Contains("Intel")) // "Intel Corporation"
        {
            imageName = "Intel.png";
            if (name.ToLower().Contains("iris")) {
                imageName = "Intel_Iris_Xe_Graphics.png";
            }
            if (name.ToLower().Contains("arc"))
            {
                imageName = "ARC.png";
            }
            if (name.ToLower().Contains("data"))
            {
                imageName = "data-center-gpu-flex-badge-centered-transparent-rwd_1920-1080.png";
            }


        }
        else if (Manu.Contains("Moore")) // "Moore Threads"
        {
            imageName = "Moore.png";
        }
        else if (Manu.Contains("Qualcomm")) // "Qualcomm Incorporated"
        {
            imageName = "Qualcomm.png";
        }
        else if (Manu.Contains("DisplayLink")) //"DisplayLink"
        {
            imageName = "DisplayLink.png";
        }
        else if (Manu.Contains("Silicon")) //"SiliconMotion"
        {
            imageName = "Silicon.png";
        }
        else
        {
            imageName = "GPU.png";  // 其他情况
        }

        return $"pack://application:,,,/Assets/Gpuicons/{imageName}";
    }

    public static Image CreateGpuImage(string key, string name,int size)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri(GetGpuImagePath(key,name)))
            {
                CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                CacheOption = BitmapCacheOption.OnLoad
            },
            Height = size,
            Width = size,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };

        // 设置抗锯齿参数
        image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        image.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);

        return image;
    }

    public static FontIcon FontIcon(int Size,string Glyph)
    {
        var icon = new FontIcon
        {
            FontSize = Size,
            FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
            Glyph = Glyph // 获取图标Unicode
        };
        return icon;

    }

    public static DateTime GetLinkerTime()
    {
        string filePath = Assembly.GetExecutingAssembly().Location;
        var fileInfo = new System.IO.FileInfo(filePath);
        DateTime linkerTime = fileInfo.LastWriteTime;
        return linkerTime;
    }


    public static async Task UpdateSwitchConfigurationAsync(string switchName, string mode, string? physicalAdapterName, bool allowManagementOS, bool enabledhcp)
    {
        string allowManagementParam = allowManagementOS ? "$true" : "$false";
        string script = string.Empty;

        switch (mode)
        {
            case "Bridge":
                //1.先删除可能存在的宿主适配器。 2.设置交换机为外部交换机。
                script = $"Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue | Remove-VMNetworkAdapter -Confirm:$false";
                script += $"\nSet-VMSwitch -Name '{switchName}' -NetAdapterInterfaceDescription '{physicalAdapterName}' -AllowManagementOS {allowManagementParam}";
                break;

            case "NAT":
                string natName = $"NAT-for-{switchName}";
                string gatewayIP = "192.168.100.1";
                string subnetPrefix = "24";
                string natSubnet = "192.168.100.0/24";

                script = $"Set-VMSwitch -Name '{switchName}' -SwitchType Internal";
                script += $"\nif (-not (Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue)) {{ Add-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' }}";

                script += $"\n$vmAdapter = Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}'";
                script += $"\nif ($vmAdapter) {{";
                script += $"\n    $netAdapter = Get-NetAdapter | Where-Object {{ ($_.MacAddress -replace '-') -eq ($vmAdapter.MacAddress -replace '-') }}";
                script += $"\n    if ($netAdapter) {{";
                script += $"\n        $interfaceAlias = $netAdapter.Name";
                script += $"\n        Get-NetIPAddress -InterfaceAlias $interfaceAlias -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false";
                script += $"\n        New-NetIPAddress -InterfaceAlias $interfaceAlias -IPAddress '{gatewayIP}' -PrefixLength {subnetPrefix} -ErrorAction Stop";
                script += $"\n    }}";
                script += $"\n}}";

                // ==================== 关键修复代码 ====================
                // 移除任何占用目标子网的现有NAT规则
                script += $"\n$conflictingNat = Get-NetNat | Where-Object {{ $_.InternalIPInterfaceAddressPrefix -eq '{natSubnet}' }}";
                script += $"\nif ($conflictingNat) {{ Remove-NetNat -Name $conflictingNat.Name -Confirm:$false }}";
                // ========================================================

                // 现在安全地创建我们自己的规则
                script += $"\nNew-NetNat -Name '{natName}' -InternalIPInterfaceAddressPrefix '{natSubnet}' -ErrorAction Stop";

                break;

            case "Isolated":
                // ==================== 关键修正 ====================
                // 定义可能存在的 NAT 规则的名称，以便我们知道要删除什么
                string natNameToClean = $"NAT-for-{switchName}";

                // 1. [新增] 首先，尝试查找并删除与此交换机关联的 NAT 规则。
                script = $"Get-NetNat -Name '{natNameToClean}' -ErrorAction SilentlyContinue | Remove-NetNat -Confirm:$false";

                // 2. 然后，设置为内部交换机。
                script += $"\nSet-VMSwitch -Name '{switchName}' -SwitchType Internal";

                // 3. 最后，根据需要处理宿主连接。
                if (allowManagementOS)
                {
                    script += $"\nif (-not (Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue)) {{ Add-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' }}";
                }
                else
                {
                    script += $"\nGet-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue | Remove-VMNetworkAdapter -Confirm:$false";
                }

                break;

            default:
                Show($"错误：未知的网络模式 '{mode}'");
                return; 
        }

        Run(script);


        // 这里可以添加对 enabledhcp 参数的处理逻辑，如果需要的话
        if (enabledhcp)
        {
            // ... 添加启用DHCP的脚本和逻辑 ...
            // Show("启用DHCP的逻辑...");
        }
    }
    public static void Show(string message)
    {
        var messageBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "提示",
            Content = message,
            CloseButtonText = "OK"
        };

        messageBox.ShowDialogAsync();
    }

    public static string Version => "V1.0.9";
    public static string Author => "砂菱叶";










}