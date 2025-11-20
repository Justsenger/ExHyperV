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
    private static RunspacePool? _runspacePool;


    public static void InitializePowerShell()
    {
        if (_runspacePool == null)
        {
            InitialSessionState initialSessionState = InitialSessionState.CreateDefault();
            _runspacePool = RunspaceFactory.CreateRunspacePool(initialSessionState);
            _runspacePool.Open();
        }
    }

    public static void CleanupPowerShell()
    {
        _runspacePool?.Close();
        _runspacePool?.Dispose();
        _runspacePool = null;
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
        if (enabledhcp) { }
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
        staThread.IsBackground = true;
        staThread.Start();

        return tcs.Task;
    }


    /// <summary>
    /// 添加Hyper-V GPU分配策略注册表项，以允许不受支持的GPU进行分区。
    /// </summary>
    public static void AddGpuAssignmentStrategyReg()
    {
        string path = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        string script = $@"
            if (-not (Test-Path '{path}')) {{ New-Item -Path '{path}' -Force }}
            Set-ItemProperty -Path '{path}' -Name 'RequireSecureDeviceAssignment' -Value 0 -Type DWord
            Set-ItemProperty -Path '{path}' -Name 'RequireSupportedDeviceAssignment' -Value 0 -Type DWord";
        Run(script); 
    }

    /// <summary>
    /// 移除Hyper-V GPU分配策略注册表项。
    /// </summary>
    public static void RemoveGpuAssignmentStrategyReg()
    {
        string path = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
        string script = $@"
            if (Test-Path '{path}') {{
                Remove-ItemProperty -Path '{path}' -Name 'RequireSecureDeviceAssignment' -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path '{path}' -Name 'RequireSupportedDeviceAssignment' -ErrorAction SilentlyContinue
            }}";
        Run(script); 
    }


    #region Hyper-V Network Helpers

    /// <summary>
    /// 异步获取虚拟机的IP地址。
    /// 首先尝试通过Hyper-V集成服务直接获取，如果失败，则回退到使用主机的ARP缓存通过MAC地址查找。
    /// </summary>
    /// <param name="vmName">虚拟机的名称。</param>
    /// <param name="macAddressWithColons">虚拟机网络适配器的MAC地址，格式为 "00:15:5D:..."。</param>
    /// <returns>一个包含逗号分隔的IP地址的字符串，如果未找到则为空字符串。</returns>
    public static async Task<string> GetVmIpAddressAsync(string vmName, string macAddressWithColons)
    {
        if (string.IsNullOrEmpty(vmName) || string.IsNullOrEmpty(macAddressWithColons))
        {
            return string.Empty;
        }

        // 1. 尝试直接从Hyper-V获取IP
        string macAddressWithoutColons = macAddressWithColons.Replace(":", "");
        // 注意: Get-VMNetworkAdapter 返回的 IPAddresses 是一个字符串数组
        string directIpScript = $"@(Get-VMNetworkAdapter -VMName '{vmName}' | Where-Object {{ $_.MacAddress -eq '{macAddressWithoutColons}' }}).IPAddresses";

        string ipAddresses = string.Empty;
        try
        {
            // 使用你现有的健壮的 Run2 方法
            var directResults = await Run2(directIpScript);

            if (directResults != null && directResults.Count > 0)
            {
                var ips = directResults.Select(pso => pso?.BaseObject?.ToString()).Where(ip => !string.IsNullOrEmpty(ip));
                ipAddresses = string.Join(", ", ips);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Direct IP lookup for VM '{vmName}' failed: {ex.Message}");
            // 失败时继续尝试ARP，所以这里不抛出异常
        }


        // 2. 如果直接获取失败，则尝试从ARP缓存获取
        if (string.IsNullOrEmpty(ipAddresses))
        {
            System.Diagnostics.Debug.WriteLine($"Direct IP lookup failed or returned empty for VM '{vmName}' (MAC: {macAddressWithColons}). Trying ARP cache.");
            ipAddresses = await GetIpFromArpCacheByMacAsync(macAddressWithColons);
        }

        return ipAddresses;
    }

    /// <summary>
    /// 通过MAC地址在主机的ARP缓存中查找对应的IPv4地址。
    /// </summary>
    /// <param name="macWithColons">带冒号的MAC地址。</param>
    /// <returns>找到的IP地址，如果未找到则为空字符串。</returns>
    private static async Task<string> GetIpFromArpCacheByMacAsync(string macWithColons)
    {
        string formattedMacForArp = macWithColons.Replace(':', '-');
        string script = $"Get-NetNeighbor -AddressFamily IPv4 | Where-Object {{ $_.LinkLayerAddress -eq '{formattedMacForArp}' -and $_.State -ne 'Incomplete' }} | Select-Object -ExpandProperty IPAddress -First 1 -ErrorAction SilentlyContinue";

        try
        {
            // 使用 Run2 方法
            var results = await Run2(script);
            if (results != null && results.Count > 0)
            {
                return results[0]?.BaseObject?.ToString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ARP lookup failed for MAC {formattedMacForArp}: {ex.Message}");
        }
        return string.Empty;
    }

    #endregion


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
    public static string Version => "V1.2.3-Beta5";
    public static string Author => "砂菱叶";

}