using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using Image = Wpf.Ui.Controls.Image;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.Tools;


public class Utils
{
    public static Collection<PSObject> Run(string script)
    {
        PowerShell ps = PowerShell.Create();
        ps.AddScript(script);
        return ps.Invoke();
    }
    public static Collection<PSObject>? Run2(string script)
    {
        using PowerShell ps = PowerShell.Create();
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
                Show2(errorBuilder.ToString()); 
                return null;
            }
            return results;
        }
        catch (Exception ex)
        {
            Show2($"执行 PowerShell 时发生严重系统异常：\n\n{ex.Message}");
            return null;
        }
    }
    public static CardExpander CardExpander1()
    {
        return new CardExpander
        {
            Margin = new Thickness(20, 5, 0, 0),
            ContentPadding = new Thickness(6),
        };
    }

    public static CardExpander CardExpander2()
    {
        return new CardExpander
        {
            Margin = new Thickness(30, 5, 10, 0),
            ContentPadding = new Thickness(6),
        };
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
        return new FontIcon
        {
            FontSize = 24,
            FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
            Glyph = GetIconPath(classType, friendlyName) // 获取图标Unicode
        };
    }
    public static TextBlock TextBlock1(string friendlyName) {
        return new TextBlock
        {
            Text = friendlyName,
            FontSize = 16,
            Margin = new Thickness(0, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }
    public static TextBlock TextBlock12(string friendlyName)
    {
        var headerText = new TextBlock
        {
            Text = friendlyName,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return headerText;
    }
    public static DropDownButton DropDownButton1(string status) {
        return new DropDownButton
        {
            Content = status,
            Margin = new Thickness(10, 0, 5, 0),
        };
    }
    public static TextBlock TextBlock3(string friendlyName)
    {
        return new TextBlock
        {
            Text = friendlyName,
            FontSize = 16,
            Margin = new Thickness(0, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap // 允许文本换行
        };
    }
    public static TextBlock TextBlock2(string text,int row,int column)
    {
        var textBlock = new TextBlock { Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
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

        return $"pack://application:,,,/Assets/{imageName}";
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
        //获取编译时间
        string filePath = Assembly.GetExecutingAssembly().Location;
        var fileInfo = new System.IO.FileInfo(filePath);
        DateTime linkerTime = fileInfo.LastWriteTime;
        return linkerTime;
    }


    public static async Task UpdateSwitchConfigurationAsync(string switchName, string mode, string? physicalAdapterName, bool allowManagementOS, bool enabledhcp)
    {
        string script;
        switch (mode)
        {
            case "Bridge":


                //1.清除ICS设置。2.清除多余的宿主适配器。3.设置交换机为外部交换机，指定上游网卡。

                script = $@"$netShareManager = New-Object -ComObject HNetCfg.HNetShare;
                foreach ($connection in $netShareManager.EnumEveryConnection) {{
                    $props = $netShareManager.NetConnectionProps.Invoke($connection);
                    $config = $netShareManager.INetSharingConfigurationForINetConnection.Invoke($connection);
                    if ($config.SharingEnabled) {{
                        $config.DisableSharing();
                    }}
                }}";
                script += $"Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue | Remove-VMNetworkAdapter -Confirm:$false;";
                script += $"\nSet-VMSwitch -Name '{switchName}' -NetAdapterInterfaceDescription '{physicalAdapterName}'";

                break;

            case "NAT":
                //1.设置为内部交换机。2.开启ICS.

                script = $"Set-VMSwitch -Name '{switchName}' -SwitchType Internal;";
                script += $@"$PublicAdapterDescription = '{physicalAdapterName}';
                $SwitchName = '{switchName}';
                $publicNic = Get-NetAdapter -InterfaceDescription $PublicAdapterDescription -ErrorAction SilentlyContinue;
                $PublicAdapterActualName = $publicNic.Name;
                $vmAdapter = Get-VMNetworkAdapter -ManagementOS -SwitchName $SwitchName -ErrorAction SilentlyContinue;
                $privateAdapter = Get-NetAdapter | Where-Object {{ ($_.MacAddress -replace '[-:]') -eq ($vmAdapter.MacAddress -replace '[-:]') }};
                $PrivateAdapterName = $privateAdapter.Name;

                $netShareManager = New-Object -ComObject HNetCfg.HNetShare;
                $publicConfig = $null;
                $privateConfig = $null;

                foreach ($connection in $netShareManager.EnumEveryConnection) {{
                    $props = $netShareManager.NetConnectionProps.Invoke($connection);
                    $config = $netShareManager.INetSharingConfigurationForINetConnection.Invoke($connection);
                    if ($config.SharingEnabled) {{
                        $config.DisableSharing();
                    }}
                    if ($props.Name -eq $PublicAdapterActualName) {{
                        $publicConfig = $config;
                    }}
                    elseif ($props.Name -eq $PrivateAdapterName) {{
                        $privateConfig = $config;
                    }}
                }}

                if ($publicConfig -and $privateConfig) {{
                    $publicConfig.EnableSharing(0);
                    $privateConfig.EnableSharing(1);

                }}
                ";
                break;

            case "Isolated":
                script = $"\nSet-VMSwitch -Name '{switchName}' -SwitchType Internal;";
                script += $@"$netShareManager = New-Object -ComObject HNetCfg.HNetShare;
                foreach ($connection in $netShareManager.EnumEveryConnection) {{
                    $props = $netShareManager.NetConnectionProps.Invoke($connection);
                    $config = $netShareManager.INetSharingConfigurationForINetConnection.Invoke($connection);
                    if ($config.SharingEnabled) {{
                        $config.DisableSharing();
                    }}
                }}";
                if (allowManagementOS)
                {
                    script += $"\nif (-not (Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue)) {{ Add-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' }};";
                }
                else
                {
                    script += $"\nGet-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue | Remove-VMNetworkAdapter -Confirm:$false;";
                }
                break;

            default:
                throw new ArgumentException($"错误：未知的网络模式 '{mode}'");
        }
        await RunScriptSTA(script);
        if (enabledhcp){}
    }

    public static Task RunScriptSTA(string script)
    {
        var tcs = new TaskCompletionSource<object?>();

        var staThread = new Thread(() =>
        {
            try
            {
                Run(script);
                tcs.SetResult(null); // 表示成功完成
            }
            catch (Exception ex)
            {
                tcs.SetException(ex); // 将异常传递给 Task
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();

        return tcs.Task;
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

    public static void Show2(string message)
    {
        System.Windows.MessageBox.Show(message);
    }

    public static string Version => "V1.1.0-Beta";
    public static string Author => "砂菱叶";

}