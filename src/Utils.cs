using System.Collections.ObjectModel;
using System.Data.Common;
using System.Dynamic;
using System.Management.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
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

    public static CardExpander CardExpander1()
    {
        var cardExpander = new CardExpander
        {
            Margin = new Thickness(20, 5, 0, 0),
            ContentPadding = new Thickness(6),
        };

        return cardExpander;
    }

    public static string GetIconPath(string deviceType, string friendlyName)
    {
        switch (deviceType)
        {
            case "Display":
                return "\xF211";  // �Կ�ͼ�� 
            case "Net":
                return "\xE839";  // ����ͼ��
            case "USB":
                return friendlyName.Contains("USB4")
                    ? "\xE945"    // �׵�ӿ�ͼ��
                    : "\xECF0";   // ��ͨUSBͼ��
            case "HIDClass":
                return "\xE928";  // HID�豸ͼ��
            case "SCSIAdapter":
            case "HDC":
                return "\xEDA2";  // �洢������ͼ��
            default:
                return friendlyName.Contains("Audio")
                    ? "\xE995"     // ��Ƶ�豸ͼ��
                    : "\xE950";    // Ĭ��ͼ��
        }
    }


    public static FontIcon FontIcon1(string classType, string friendlyName) {
        var icon = new FontIcon
        {
            FontSize = 24,
            FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
            Glyph = GetIconPath(classType, friendlyName) // ��ȡͼ��Unicode
        };
        return icon;
    }
    public static TextBlock TextBlock1(string friendlyName) {
        var headerText = new TextBlock
        {
            Text = friendlyName,
            FontSize = 16,
            Margin = new System.Windows.Thickness(0, 6, 0, 0),
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


    public static TextBlock TextBlock2(string text,int row,int column)
    {
        var textBlock = new TextBlock { Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) };
        Grid.SetRow(textBlock, row);
        Grid.SetColumn(textBlock, column);

        return textBlock;
    }

    public static string GetGpuImagePath(string Manu)
    {
        string imageName;

        // ���� Manu ���ò�ͬ��ͼƬ�ļ���
        if (Manu.Contains("NVIDIA")) // ����� NVIDIA �Կ���ʹ�� NVIDIA ��ͼƬ
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
            imageName = "Qualcomm.png";  // ���������������Adreno��ħ��������ʲôXiaomi֮�������
        }

        return $"pack://application:,,,/Assets/Gpuicons/{imageName}";
    }











}