using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

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








}