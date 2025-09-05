using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace ExHyperV.Tools;


public class PowerShellScriptException : Exception
{
    public PowerShellScriptException(string message) : base(message) { }
}

public class Utils
{
    private static readonly RunspacePool _runspacePool;
    static Utils()
    {
        InitialSessionState initialSessionState = InitialSessionState.CreateDefault();
        _runspacePool = RunspaceFactory.CreateRunspacePool(initialSessionState);
        _runspacePool.Open();

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            _runspacePool?.Close();
            _runspacePool?.Dispose();
        };
    }

    public static Collection<PSObject> Run(string script)
    {
        using (PowerShell ps = PowerShell.Create())
        {
            ps.AddScript(script);
            var results = ps.Invoke();
            return results;
        }
    }
    public static async Task<Collection<PSObject>> Run2(string script, CancellationToken cancellationToken = default)
    {
        return await ExecuteCoreAsync(script, cancellationToken);
    }

    private static async Task<Collection<PSObject>> ExecuteCoreAsync(string script, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            using (var ps = PowerShell.Create())
            {
                ps.RunspacePool = _runspacePool;
                ps.AddScript(script);
                var psDataCollection = await ps.InvokeAsync().WaitAsync(cancellationToken);

                if (ps.HadErrors)
                {
                    var errorMessages = new StringBuilder();
                    foreach (var error in ps.Streams.Error)
                    {
                        errorMessages.AppendLine(error.ToString());
                    }
                    throw new PowerShellScriptException(errorMessages.ToString());
                }
                return new Collection<PSObject>(psDataCollection);
            }
        }, cancellationToken);
    }
    public static string GetIconPath(string deviceType, string friendlyName)
    {
        switch (deviceType)
        {
            case "Switch":
                return "\xE967";  // 交换机图标 
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


    public static FontIcon FontIcon1(string classType, string friendlyName)
    {
        return new FontIcon
        {
            FontSize = 24,
            FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
            Glyph = GetIconPath(classType, friendlyName) // 获取图标Unicode
        };
    }


    public static string GetGpuImagePath(string Manu, string name)
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
            if (name.ToLower().Contains("iris"))
            {
                imageName = "Intel-IrisXe.png";
            }
            if (name.ToLower().Contains("arc"))
            {
                imageName = "Inter-ARC.png";
            }
            if (name.ToLower().Contains("data"))
            {
                imageName = "Inter-DataCenter.png";
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
            imageName = "Default.png";  // 其他情况
        }

        return $"pack://application:,,,/Assets/{imageName}";
    }



    public static string GetMemoryImagePath(string manufacturer)
    {
        if (string.IsNullOrEmpty(manufacturer) || manufacturer.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return $"pack://application:,,,/Assets/Memory/Memory_Default.png";
        }
        string imageName;
        string lowerManu = manufacturer.ToLower();

        // 完整的厂商 -> 图片文件名映射
        if (lowerManu.Contains("samsung")) imageName = "Samsung.png";
        else if (lowerManu.Contains("sk hynix") || lowerManu.Contains("klevv")) imageName = "SKHynix.png";
        else if (lowerManu.Contains("micron") || lowerManu.Contains("crucial")) imageName = "Micron.png";
        else if (lowerManu.Contains("kingston") || lowerManu.Contains("hyperx")) imageName = "Kingston.png";
        else if (lowerManu.Contains("corsair")) imageName = "Corsair.png";
        else if (lowerManu.Contains("g.skill")) imageName = "GSkill.png";
        else if (lowerManu.Contains("adata") || lowerManu.Contains("xpg")) imageName = "ADATA.png";
        else if (lowerManu.Contains("team group") || lowerManu.Contains("t-force")) imageName = "TeamGroup.png";
        else if (lowerManu.Contains("patriot") || lowerManu.Contains("viper")) imageName = "Patriot.png";
        else if (lowerManu.Contains("apacer")) imageName = "Apacer.png";
        else if (lowerManu.Contains("gloway")) imageName = "Gloway.png";
        else if (lowerManu.Contains("asgard")) imageName = "Asgard.png";
        else if (lowerManu.Contains("kingbank")) imageName = "KingBank.png";
        else if (lowerManu.Contains("lexar")) imageName = "Lexar.png";
        else if (lowerManu.Contains("pny")) imageName = "PNY.png";
        else if (lowerManu.Contains("geil")) imageName = "GeIL.png";
        else if (lowerManu.Contains("mushkin")) imageName = "Mushkin.png";
        else if (lowerManu.Contains("v-color")) imageName = "VColor.png";
        else if (lowerManu.Contains("kingmax")) imageName = "Kingmax.png";
        else if (lowerManu.Contains("ramaxel")) imageName = "Ramaxel.png";
        else if (lowerManu.Contains("cxmt")) imageName = "CXMT.png";
        else if (lowerManu.Contains("transcend")) imageName = "Transcend.png";
        else if (lowerManu.Contains("silicon power")) imageName = "SiliconPower.png";
        else if (lowerManu.Contains("acer")) imageName = "Acer.png";
        else if (lowerManu.Contains("galax")) imageName = "GALAX.png";
        else if (lowerManu.Contains("colorful")) imageName = "Colorful.png";
        else if (lowerManu.Contains("maxsun")) imageName = "Maxsun.png";
        else if (lowerManu.Contains("zadak")) imageName = "ZADAK.png";
        else if (lowerManu.Contains("innodisk")) imageName = "Innodisk.png";
        else if (lowerManu.Contains("biwin")) imageName = "BIWIN.png";
        else if (lowerManu.Contains("netac")) imageName = "Netac.png";
        else if (lowerManu.Contains("tigo")) imageName = "Tigo.png";
        else if (lowerManu.Contains("zotac")) imageName = "Zotac.png";
        else if (lowerManu.Contains("inno3d")) imageName = "Inno3D.png";
        else if (lowerManu.Contains("ocz")) imageName = "OCZ.png";
        else if (lowerManu.Contains("hp")) imageName = "HP.png";
        else if (lowerManu.Contains("dell")) imageName = "Dell.png";
        else if (lowerManu.Contains("lenovo")) imageName = "Lenovo.png";
        else if (lowerManu.Contains("microsoft")) imageName = "microsoft.png";
        else
        {
            imageName = "Memory_Default.png";
        }

        return $"pack://application:,,,/Assets/Memory/{imageName}";
    }
    public static FontIcon FontIcon(int Size, string Glyph)
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

    // 这个方法可能在您的 Utils.cs 文件中
    public static async Task UpdateSwitchConfigurationAsync(string switchName, string mode, string? physicalAdapterName, bool allowManagementOS)
    {
        string script;
        string allowManagementOsScriptValue = allowManagementOS ? "$true" : "$false";

        switch (mode)
        {
            case "External":
                if (string.IsNullOrEmpty(physicalAdapterName))
                {
                    throw new ArgumentException("External switch requires a physical adapter.", nameof(physicalAdapterName));
                }

                // 对于外部交换机，先清理再用一个命令重建是最可靠的方式
                string cleanupScript = $"Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue | Remove-VMNetworkAdapter -Confirm:$false;";
                script = cleanupScript;
                script += $"\nSet-VMSwitch -Name '{switchName}' -NetAdapterInterfaceDescription '{physicalAdapterName}' -AllowManagementOS {allowManagementOsScriptValue}";
                break;

            case "Internal":
                // 步骤 1: 确保交换机类型为 Internal。这会安全地断开它与任何物理网卡的连接。
                script = $"Set-VMSwitch -Name '{switchName}' -SwitchType Internal;";

                // === 核心修正 ===
                // 步骤 2: 根据 allowManagementOS 的值，显式地添加或删除主机虚拟网卡。
                if (allowManagementOS)
                {
                    // 如果主机适配器不存在，则添加它。
                    script += $"\nif (-not (Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue)) {{ Add-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' }}";
                }
                else
                {
                    // 如果主机适配器存在，则删除它。
                    script += $"\nGet-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction SilentlyContinue | Remove-VMNetworkAdapter -Confirm:$false";
                }
                break;

            default:
                throw new ArgumentException($"Unknown network mode '{mode}'", nameof(mode));
        }

        await RunScriptSTA(script);
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
    public static string Version => "V1.2.3-Beta";
    public static string Author => "砂菱叶";

}