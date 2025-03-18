using System.Collections.ObjectModel;
using System.Data.Common;
using System.Dynamic;
using System.Management.Automation;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Wpf.Ui.Controls;
using Image = Wpf.Ui.Controls.Image;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.Views.Pages;


public partial class Utils
{

    public static Collection<PSObject> Run(string script)
    {
        PowerShell ps = PowerShell.Create();
        ps.AddScript(script);
        return ps.Invoke();
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
            HorizontalAlignment = HorizontalAlignment.Center
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


    public static string GetGpuImagePath(string Manu)
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

    public static Image CreateGpuImage(string key,int size)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri(GetGpuImagePath(key)))
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

    public static string Version => "V1.0.5";
    public static string Author => "砂菱叶";










}