using System.Diagnostics;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Views;  
using Renci.SshNet; 

namespace ExHyperV.Services
{
    public class GpuPartitionService : IGpuPartitionService
    {

        private bool IsWindows11OrGreater() => Environment.OSVersion.Version.Build >= 22000;

        // PowerShell 脚本常量
        private const string GetGpuWmiInfoScript = "Get-CimInstance -Class Win32_VideoController | select PNPDeviceID,name,AdapterCompatibility,DriverVersion";
        private const string GetGpuRamScript = @"
            Get-ItemProperty -Path ""HKLM:\SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0*"" -ErrorAction SilentlyContinue |
                Select-Object MatchingDeviceId,
                      @{Name='MemorySize'; Expression={
                          if ($_. ""HardwareInformation.qwMemorySize"") {
                              $_.""HardwareInformation.qwMemorySize""
                          } 
                          elseif ($_. ""HardwareInformation.MemorySize"" -and $_.""HardwareInformation.MemorySize"" -isnot [byte[]]) {
                              $_.""HardwareInformation.MemorySize""
                          }
                          else {
                              $null
                          }
                      }} |
                Where-Object { $_.MemorySize -ne $null -and $_.MemorySize -gt 0 }";

        private const string GetPartitionableGpusWin11Script = "Get-VMHostPartitionableGpu | select name";
        private const string GetPartitionableGpusWin10Script = "Get-VMPartitionableGpu | select name"; 



        private const string CheckHyperVModuleScript = "Get-Module -ListAvailable -Name Hyper-V";
        private const string GetVmsScript = "Hyper-V\\Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace,Notes";

        public Task<string> GetVmStateAsync(string vmName)
        {
            return Task.Run(() =>
            {
                var result = Utils.Run($"(Get-VM -Name '{vmName}').State");
                if (result != null && result.Count > 0)
                {
                    return result[0].ToString();
                }
                return "NotFound";
            });
        }
        public Task ShutdownVmAsync(string vmName)
        {
            return Task.Run(() =>
            {
                Utils.Run($"Stop-VM -Name '{vmName}' -TurnOff");

                while (true)
                {
                    var result = Utils.Run($"(Get-VM -Name '{vmName}').State");
                    if (result != null && result.Count > 0 && result[0].ToString() == "Off")
                    {
                        break;
                    }
                    System.Threading.Thread.Sleep(500); 
                }
            });
        }


        private char GetFreeDriveLetter()
        {
            var usedLetters = DriveInfo.GetDrives().Select(d => d.Name[0]).ToList();
            for (char c = 'Z'; c >= 'A'; c--)
            {
                if (!usedLetters.Contains(c))
                {
                    return c;
                }
            }
            throw new IOException("没有可用的盘符");
        }
        public Task<List<PartitionInfo>> GetPartitionsFromVmAsync(string vmName)
        {
            return Task.Run(() =>
            {
                string harddiskpath = null;
                int? diskNumber = null;

                try
                {
                    var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                    if (harddiskPathResult == null || harddiskPathResult.Count == 0)
                    {
                        throw new FileNotFoundException($"无法找到虚拟机 '{vmName}' 的虚拟硬盘文件。");
                    }
                    harddiskpath = harddiskPathResult[0].ToString();

                    var mountScript = $@"
                $diskImage = Mount-DiskImage -ImagePath '{harddiskpath}' -NoDriveLetter -PassThru;
                ($diskImage | Get-Disk).Number;
            ";
                    var mountResult = Utils.Run(mountScript);

                    if (mountResult == null || mountResult.Count == 0 || !int.TryParse(mountResult[0].ToString(), out int num))
                    {
                        throw new InvalidOperationException("挂载虚拟磁盘或获取其磁盘编号失败。");
                    }
                    diskNumber = num;
                    string devicePath = $@"\\.\PhysicalDrive{diskNumber}";
                    var diskParser = new DiskParserService();
                    List<PartitionInfo> initialPartitions = diskParser.GetPartitions(devicePath);
                    return initialPartitions;
                }
                catch (UnauthorizedAccessException)
                {
                    throw new UnauthorizedAccessException("需要管理员权限才能读取磁盘分区信息，请以管理员身份重新启动应用程序。");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(harddiskpath))
                    {
                        Utils.Run($"Dismount-DiskImage -ImagePath '{harddiskpath}' -ErrorAction SilentlyContinue");
                    }
                }
            });
        }
        private string NormalizeDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return string.Empty;
            }
            var normalizedId = deviceId.ToUpper();
            if (normalizedId.StartsWith(@"\\?\"))
            {
                normalizedId = normalizedId.Substring(4);
            }
            int suffixIndex = normalizedId.IndexOf("#{");
            if (suffixIndex != -1)
            {
                normalizedId = normalizedId.Substring(0, suffixIndex);
            }
            normalizedId = normalizedId.Replace('\\', '#');

            return normalizedId;
        }
        public Task<List<GPUInfo>> GetHostGpusAsync()
        {
            return Task.Run(() =>
            {
                var pciInfoProvider = new PciInfoProvider();
                pciInfoProvider.EnsureInitializedAsync().Wait();

                var gpuList = new List<GPUInfo>();
                var gpulinked = Utils.Run(GetGpuWmiInfoScript);
                if (gpulinked.Count > 0)
                {
                    foreach (var gpu in gpulinked)
                    {
                        string name = gpu.Members["name"]?.Value.ToString();
                        string instanceId = gpu.Members["PNPDeviceID"]?.Value.ToString();
                        string manu = gpu.Members["AdapterCompatibility"]?.Value.ToString();
                        string driverVersion = gpu.Members["DriverVersion"]?.Value.ToString();
                        string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId);
                        //if (vendor == "Unknown") { continue; }
                        if (instanceId != null && !instanceId.ToUpper().StartsWith("PCI\\")){continue; }
                        gpuList.Add(new GPUInfo(name, "True", manu, instanceId, null, null, driverVersion, vendor));
                    }
                }

                bool hasHyperV = Utils.Run(CheckHyperVModuleScript).Count > 0;
                if (!hasHyperV)
                {
                    return gpuList;
                }

                var gpuram = Utils.Run(GetGpuRamScript);
                if (gpuram.Count > 0)
                {
                    foreach (var existingGpu in gpuList)
                    {
                        var matchedGpu = gpuram.FirstOrDefault(g =>
                        {
                            string id = g.Members["MatchingDeviceId"]?.Value?.ToString().ToUpper().Substring(0, 21);
                            return !string.IsNullOrEmpty(id) && existingGpu.InstanceId.Contains(id);
                        });

                        string preram = matchedGpu?.Members["MemorySize"]?.Value?.ToString() ?? "0";
                        existingGpu.Ram = long.TryParse(preram, out long _) ? preram : "0";
                    }
                }

                string GetPartitionableGpusScript = IsWindows11OrGreater() ? GetPartitionableGpusWin11Script : GetPartitionableGpusWin10Script;
                var partitionableGpus = Utils.Run(GetPartitionableGpusScript);
                if (partitionableGpus.Count > 0)
                {
                    foreach (var gpu in partitionableGpus)
                    {
                        string pname = gpu.Members["Name"]?.Value.ToString();
                        string normalizedPNameId = NormalizeDeviceId(pname);

                        if (string.IsNullOrEmpty(normalizedPNameId)) continue;
                        var existingGpu = gpuList.FirstOrDefault(g =>
                            NormalizeDeviceId(g.InstanceId) == normalizedPNameId
                        );
                        if (existingGpu != null)
                        {
                            existingGpu.Pname = pname;
                        }
                    }
                }
                return gpuList;
            });
        }

        public Task<List<VMInfo>> GetVirtualMachinesAsync()
        {
            return Task.Run(() =>
            {
                var vmList = new List<VMInfo>();
                var vms = Utils.Run(GetVmsScript);
                if (vms.Count > 0)
                {
                    foreach (var vm in vms)
                    {
                        var gpulist = new Dictionary<string, string>();
                        string vmname = vm.Members["VMName"]?.Value?.ToString() ?? string.Empty;
                        string highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value?.ToString() ?? string.Empty;
                        string guest = vm.Members["GuestControlledCacheTypes"]?.Value?.ToString() ?? string.Empty;
                        string notes = vm.Members["Notes"]?.Value?.ToString() ?? string.Empty;

                        var vmgpus = Utils.Run($@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id");
                        if (vmgpus.Count > 0)
                        {
                            foreach (var gpu in vmgpus)
                            {
                                string gpupath = gpu.Members["InstancePath"]?.Value?.ToString() ?? string.Empty;
                                string gpuid = gpu.Members["Id"]?.Value?.ToString() ?? string.Empty;
                                // 如果InstancePath为空 (Win10场景)，则尝试从备注中解析
                                if (string.IsNullOrEmpty(gpupath) && !string.IsNullOrEmpty(notes))
                                {
                                    string tagPrefix = "[AssignedGPU:";
                                    int startIndex = notes.IndexOf(tagPrefix);
                                    if (startIndex != -1)
                                    {
                                        startIndex += tagPrefix.Length;
                                        int endIndex = notes.IndexOf("]", startIndex);
                                        if (endIndex != -1)
                                        {
                                            gpupath = notes.Substring(startIndex, endIndex - startIndex);
                                        }
                                    }
                                }
                                gpulist[gpuid] = gpupath;
                            }
                        }
                        vmList.Add(new VMInfo(vmname, null, highmmio, guest, gpulist));
                    }
                }
                return vmList;
            });
        }
        
        private async Task<string> InjectWindowsDriversAsync(string vmName, string harddiskpath, PartitionInfo partition, string gpuManu)
        {
            string assignedDriveLetter = null;

            try
            {
                char suggestedLetter = GetFreeDriveLetter();

                var script = $@"
            $diskImage = Mount-DiskImage -ImagePath '{harddiskpath}' -PassThru | Get-Disk;
            $partitionToMount = Get-Partition -DiskNumber $diskImage.Number | Where-Object {{ $_.PartitionNumber -eq {partition.PartitionNumber} }};
            
            try {{
                $partitionToMount | Set-Partition -NewDriveLetter '{suggestedLetter}' -ErrorAction Stop;
            }} catch {{
            }}
            
            (Get-Partition -DiskNumber $diskImage.Number | Where-Object {{ $_.PartitionNumber -eq {partition.PartitionNumber} }}).DriveLetter
        ";

                var letterResult = Utils.Run(script);

                if (letterResult == null || letterResult.Count == 0 || string.IsNullOrEmpty(letterResult[0].ToString()))
                {
                    return string.Format(Properties.Resources.Error_FailedToFindSystemPartition, harddiskpath);
                }

                string actualLetter = letterResult[0].ToString();
                assignedDriveLetter = $"{actualLetter}:";

                string system32Path = Path.Combine(assignedDriveLetter, "Windows", "System32");
                if (!Directory.Exists(system32Path))
                {
                    return string.Format(
                        "所选分区似乎不是一个有效的Windows系统分区，或已开启Bitlocker。",
                        partition.PartitionNumber,
                        assignedDriveLetter,
                        system32Path
                    );

                }

                string letter = assignedDriveLetter.TrimEnd(':');
                string sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";
                string destinationFolder = letter + @":\Windows\System32\HostDriverStore\FileRepository";

                if (Directory.Exists(destinationFolder))
                {
                    try { RemoveReadOnlyAttribute(destinationFolder); }
                    catch (Exception ex) { return string.Format(Properties.Resources.Error_RemoveOldDriverFolderReadOnlyFailed, ex.Message); }
                }
                else
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                Process robocopyProcess = new()
                {
                    StartInfo = {
                FileName = "robocopy",
                Arguments = $"\"{sourceFolder}\" \"{destinationFolder}\" /E /R:2 /W:5 /NP /NJH /NFL /NDL",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
                };
                robocopyProcess.Start();
                await robocopyProcess.WaitForExitAsync();

                if (robocopyProcess.ExitCode >= 8)
                {
                    return string.Format(Properties.Resources.Error_RobocopyDriverCopyFailed, robocopyProcess.ExitCode);
                }

                SetFolderReadOnly(destinationFolder);

                if (gpuManu.Contains("NVIDIA"))
                {
                    string regResult = NvidiaReg(letter + ":");
                    if (regResult != "OK")
                    {
                        return regResult;
                    }
                }

                return "OK";
            }
            finally
            {
                if (!string.IsNullOrEmpty(harddiskpath))
                {
                    var cleanupScript = $@"
                $diskImage = Get-DiskImage -ImagePath '{harddiskpath}';
                if ($diskImage -and $diskImage.Attached) {{
                    if ('{assignedDriveLetter}') {{
                         try {{
                            Remove-PartitionAccessPath -DiskNumber $diskImage.Number -PartitionNumber {partition.PartitionNumber} -AccessPath '{assignedDriveLetter}\' -ErrorAction Stop;
                         }} catch {{}}
                    }}
                    Dismount-DiskImage -ImagePath '{harddiskpath}';
                }}
            ";
                    Utils.Run(cleanupScript);
                }
            }
        }
        public Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu, PartitionInfo selectedPartition)
        {
            return Task.Run(async () =>
            {
                bool isWin10 = !IsWindows11OrGreater();
                var disabledGpuInstanceIds = new List<string>();
                int partitionableGpuCount = 0;
                string NormalizeForComparison(string deviceId)
                {
                    if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
                    var normalizedId = deviceId.Replace('#', '\\').ToUpper();
                    if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);
                    int suffixIndex = normalizedId.IndexOf('{');
                    if (suffixIndex != -1)
                    {
                        int lastSeparatorIndex = normalizedId.LastIndexOf('\\', suffixIndex);
                        if (lastSeparatorIndex != -1)
                        {
                            normalizedId = normalizedId.Substring(0, lastSeparatorIndex);
                        }
                    }
                    return normalizedId;
                }
                void ShowMessageOnUIThread(string message, string title = "提示")
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => Utils.Show2(message));
                }


                try
                {
                    Utils.AddGpuAssignmentStrategyReg();
                    var vmStateResult = Utils.Run($"(Get-VM -Name '{vmName}').State");
                    if (vmStateResult == null || vmStateResult.Count == 0 || vmStateResult[0].ToString() != "Off")
                    {
                        return $"错误：虚拟机 '{vmName}' 必须处于关闭状态。";
                    }
                    if (isWin10)
                    {
                        var allHostGpus = await GetHostGpusAsync();
                        partitionableGpuCount = allHostGpus.Count(gpu => !string.IsNullOrEmpty(gpu.Pname));
                        string normalizedSelectedGpuId = NormalizeForComparison(gpuInstancePath);

                        foreach (var gpu in allHostGpus)
                        {
                            if (!gpu.InstanceId.ToUpper().StartsWith("PCI\\")) continue;

                            string normalizedCurrentGpuId = NormalizeForComparison(gpu.InstanceId);

                            if (!string.Equals(normalizedCurrentGpuId, normalizedSelectedGpuId, StringComparison.OrdinalIgnoreCase))
                            {
                                disabledGpuInstanceIds.Add(gpu.InstanceId);
                            }
                        }

                        if (disabledGpuInstanceIds.Any())
                        {
                            foreach (var id in disabledGpuInstanceIds)
                            {
                                Utils.Run($"Disable-PnpDevice -InstanceId '{id}' -Confirm:$false");
                            }
                            await Task.Delay(2000);
                        }
                    }
                    string addGpuCommand = isWin10
                        ? $"Add-VMGpuPartitionAdapter -VMName '{vmName}'"
                        : $"Add-VMGpuPartitionAdapter -VMName '{vmName}' -InstancePath '{gpuInstancePath}'";
                    
                    string vmConfigScript = $@"
                        Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}';
                        Set-VM -HighMemoryMappedIoSpace 64GB –VMName '{vmName}';
                        Set-VM -LowMemoryMappedIoSpace 1GB -VMName '{vmName}';
                        {addGpuCommand};
                        ";
                    Utils.Run(vmConfigScript);

                    if (isWin10)
                    {
                        string gpuTag = $"[AssignedGPU:{gpuInstancePath}]";
                        string updateNotesScript = $@"
                        $vm = Get-VM -Name '{vmName}';
                        $currentNotes = $vm.Notes;
                        $cleanedNotes = $currentNotes -replace '\[AssignedGPU:[^\]]+\]', '';
                        $newNotes = ($cleanedNotes.Trim() + ' ' + '{gpuTag}').Trim();
                        Set-VM -VM $vm -Notes $newNotes;
                        ";
                        Utils.Run(updateNotesScript);
                    }

                    if (selectedPartition.OsType == OperatingSystemType.Windows)
                    {
                        var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                        if (harddiskPathResult == null || harddiskPathResult.Count == 0)
                        {
                            return "错误：无法获取虚拟机硬盘路径以注入驱动。";
                        }
                        string harddiskpath = harddiskPathResult[0].ToString();
                        string injectionResult = await InjectWindowsDriversAsync(vmName, harddiskpath, selectedPartition, gpuManu);
                        if (injectionResult != "OK")
                        {
                            return injectionResult;
                        }
                    }
                    else if (selectedPartition.OsType == OperatingSystemType.Linux)
                    {
                        var sshService = new SshService();
                        SshCredentials credentials = null;
                        ExecutionProgressWindow progressWindow = null;

                        // 定义取消令牌源
                        var cts = new CancellationTokenSource();


                        Action<string> showMessage = (msg) => System.Windows.Application.Current.Dispatcher.Invoke(() => Utils.Show2(msg));


                        Func<string, string> withSudo = (cmd) =>
                        {
                            // 移除命令开头可能存在的 sudo，以防重复
                            if (cmd.Trim().StartsWith("sudo "))
                            {
                                cmd = cmd.Trim().Substring(5);
                            }
                            // 对密码中的单引号进行转义，防止shell注入
                            string escapedPassword = credentials.Password.Replace("'", "'\\''");
                            return $"echo '{escapedPassword}' | sudo -S -p '' {cmd}";
                        };
                        try
                        {
                            var currentState = await GetVmStateAsync(vmName);
                            if (currentState != "Running")
                            {
                                Utils.Run($"Start-VM -Name '{vmName}'");
                            }
                            string getMacScript = $"(Get-VMNetworkAdapter -VMName '{vmName}').MacAddress | Select-Object -First 1";
                            var macResult = await Utils.Run2(getMacScript);
                            if (macResult == null || macResult.Count == 0 || string.IsNullOrEmpty(macResult[0]?.ToString()))
                            {
                                return $"错误：无法获取虚拟机 '{vmName}' 的MAC地址。请检查虚拟机网络设置。";
                            }
                            string macAddressWithoutColons = macResult[0].ToString();
                            string macAddressWithColons = System.Text.RegularExpressions.Regex.Replace(macAddressWithoutColons, "(.{2})", "$1:").TrimEnd(':');

                            string vmIpAddress = string.Empty;
                            var stopwatch = Stopwatch.StartNew();
                            while (stopwatch.Elapsed.TotalSeconds < 5 && string.IsNullOrEmpty(vmIpAddress))
                            {
                                vmIpAddress = await Utils.GetVmIpAddressAsync(vmName, macAddressWithColons);
                                if (string.IsNullOrEmpty(vmIpAddress))
                                {
                                    await Task.Delay(5000);
                                }
                            }
                            stopwatch.Stop();

                            if (string.IsNullOrEmpty(vmIpAddress))
                            {
                                return $"错误：在60秒内无法自动获取到虚拟机 '{vmName}' 的IP地址。\n\n可能的原因：\n- 虚拟机未成功启动或卡住\n- 网络配置问题 (如DHCP服务)\n- Hyper-V集成服务未运行";
                            }

                            string targetIp = vmIpAddress.Split(',').Select(ip => ip.Trim()).FirstOrDefault(ip => System.Net.IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                            if (string.IsNullOrEmpty(targetIp))
                            {
                                return $"错误：找到了地址 '{vmIpAddress}' 但无法解析为有效的IPv4地址。";
                            }

                            // 步骤 1.3: 弹出窗口让用户仅输入用户名和密码 (此部分代码从原 try 块移动而来，内容不变)
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                var loginWindow = new SshLoginWindow(vmName, targetIp);
                                if (loginWindow.ShowDialog() == true)
                                {
                                    credentials = loginWindow.Credentials;
                                }
                            });

                            if (credentials == null)return "用户取消了 SSH 登录操作。";
                            credentials.Host = targetIp;
                        }
                        catch (Exception ex)
                        {
                            ShowMessageOnUIThread($"准备阶段发生错误: {ex.Message}");
                            return $"准备阶段发生错误: {ex.Message}";
                        }


                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            progressWindow = new ExecutionProgressWindow();
                            progressWindow.Show();
                        });

                        // 【关键修改】绑定窗口关闭事件到取消令牌
                        progressWindow.Closed += (s, e) =>
                        {
                            cts.Cancel(); // 用户关闭窗口时，触发取消
                        };

                        while (true)
                        {
                            var userActionTcs = new TaskCompletionSource<bool>();
                            EventHandler retryHandler = (s, e) => userActionTcs.TrySetResult(true);
                            EventHandler closeHandler = (s, e) => userActionTcs.TrySetResult(false);


                            try
                            {
                                progressWindow.RetryClicked += retryHandler;
                                progressWindow.Closed += closeHandler;

                                if (cts.IsCancellationRequested)
                                {
                                    cts.Dispose();
                                    cts = new CancellationTokenSource();
                                    progressWindow.Closed += (s, e) => cts.Cancel();
                                }

                                if (userActionTcs.Task.IsCompleted && userActionTcs.Task.Result)
                                {
                                    progressWindow.ResetForRetry();
                                }
                                Action<string> log = (message) =>
                                {
                                    if (!cts.IsCancellationRequested) progressWindow.AppendLog(message);
                                };
                                Action<string> updateStatus = (status) =>
                                {
                                    if (!cts.IsCancellationRequested) progressWindow.UpdateStatus(status);
                                };

                                // 【检查取消】
                                if (cts.IsCancellationRequested) throw new OperationCanceledException();


                                updateStatus("[1/9] 正在连接到虚拟机...");
                                log("[1/9] 正在连接到虚拟机并初始化远程环境...");
                                string homeDirectory;

                                using (var client = new SshClient(credentials.Host, credentials.Port, credentials.Username, credentials.Password))
                                {
                                    client.Connect();
                                    log("[+] SSH 连接成功。");
                                    var pwdResult = client.RunCommand("pwd");
                                    homeDirectory = pwdResult.Result.Trim();
                                    if (string.IsNullOrEmpty(homeDirectory))
                                    {
                                        throw new Exception("无法获取Linux主目录路径。");
                                    }
                                    log($"[+] 获取到Linux主目录: {homeDirectory}");

                                    string remoteTempDir = $"{homeDirectory}/exhyperv_deploy";
                                    client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                                    log($"[+] 已创建临时部署目录: {remoteTempDir}");
                                    client.Disconnect();
                                }
                                log("[✓] 远程环境初始化完成。");

                                if (!string.IsNullOrEmpty(credentials.ProxyHost) && credentials.ProxyPort.HasValue)
                                {
                                    updateStatus("[2/9] 正在配置网络代理...");
                                    log("\n[2/9] 正在为虚拟机配置 HTTP 网络代理...");
                                    log($"[+] 代理服务器: {credentials.ProxyHost}:{credentials.ProxyPort}");
                                    string proxyUrl = $"http://{credentials.ProxyHost}:{credentials.ProxyPort}";
                                    string aptProxyContent = $"Acquire::http::Proxy \"{proxyUrl}\";\nAcquire::https::Proxy \"{proxyUrl}\";\n";
                                    string envProxyContent = $"\nexport http_proxy=\"{proxyUrl}\"\nexport https_proxy=\"{proxyUrl}\"\nexport no_proxy=\"localhost,127.0.0.1\"\n";
                                    string remoteAptProxyFile = $"{homeDirectory}/99proxy";
                                    string remoteEnvProxyFile = $"{homeDirectory}/proxy_env";
                                    await sshService.WriteTextFileAsync(credentials, aptProxyContent, remoteAptProxyFile);
                                    await sshService.WriteTextFileAsync(credentials, envProxyContent, remoteEnvProxyFile);
                                    var proxyCommands = new List<string>
                                {
                                    $"sudo mv {remoteAptProxyFile} /etc/apt/apt.conf.d/99proxy",
                                    $"sudo sh -c 'cat {remoteEnvProxyFile} >> /etc/environment'",
                                    $"rm {remoteEnvProxyFile}",
                                    $"export http_proxy={proxyUrl}",
                                    $"export https_proxy={proxyUrl}"
                                };
                                    foreach (var cmd in proxyCommands)
                                    {
                                        await sshService.ExecuteSingleCommandAsync(credentials, cmd, log, TimeSpan.FromSeconds(30));
                                    }
                                    log("[✓] 网络代理配置完成。");
                                }
                                string remoteDriversDir = $"{homeDirectory}/exhyperv_deploy/drivers";
                                string remoteLibDir = $"{homeDirectory}/exhyperv_deploy/lib";
                                updateStatus("[3/9] 正在上传主机驱动文件...");
                                log("\n[3/9] 正在上传主机 GPU 驱动文件... (此过程耗时较长，请耐心等待)");
                                await sshService.UploadDirectoryAsync(credentials, @"C:\Windows\System32\DriverStore\FileRepository", remoteDriversDir);
                                log("[✓] 主机驱动文件上传完毕。");
                                updateStatus("[4/9] 正在上传核心库与脚本...");
                                log("\n[4/9] 正在上传核心库文件和安装脚本...");
                                await UploadLocalFilesAsync(sshService, credentials, remoteLibDir);
                                log("[✓] 核心库与脚本上传完毕。");
                                updateStatus("[5/9] 开始执行系统配置脚本...");

                                string password = credentials.Password; // 假设密码存储在这里

                                var commandsToExecute = new List<Tuple<string, TimeSpan?>>
                                {


// 步骤 5: 【全新逻辑 v3】准备并安装 Mesa 驱动
// ===========================================================================================
Tuple.Create("echo '\n[5/9] 正在准备并安装 Mesa 图形驱动...'", (TimeSpan?)TimeSpan.FromSeconds(30)),

// 5.1 清理环境：移除所有旧的 PPA 和配置，确保一个干净的开始
Tuple.Create("echo '[+] 清理旧的 PPA 配置...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
Tuple.Create("sudo apt-get install -y -qq ppa-purge", (TimeSpan?)TimeSpan.FromMinutes(2)),
Tuple.Create("sudo ppa-purge -y ppa:kisak/turtle || true", (TimeSpan?)TimeSpan.FromMinutes(3)),
Tuple.Create("sudo ppa-purge -y ppa:kisak/kisak-mesa || true", (TimeSpan?)TimeSpan.FromMinutes(3)),
Tuple.Create("sudo rm -f /etc/apt/preferences.d/99-mesa-pinning", (TimeSpan?)TimeSpan.FromSeconds(10)),

// 5.2 安装基础依赖和官方版 Mesa
Tuple.Create("echo '[+] 安装基础依赖和 Ubuntu 官方稳定版 Mesa...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
Tuple.Create("sudo apt-get update -qq", (TimeSpan?)TimeSpan.FromMinutes(5)),
// 安装所有必需品，包括 vulkan-tools！
Tuple.Create("sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq linux-headers-$(uname -r) build-essential git dkms curl software-properties-common mesa-utils vulkan-tools mesa-va-drivers vainfo libgl1-mesa-dri", (TimeSpan?)TimeSpan.FromMinutes(10)),

Tuple.Create("echo '\n[6/9] 正在精确升级 Vulkan 驱动至最新版...'", (TimeSpan?)TimeSpan.FromSeconds(30)),

// 6.1 添加 Kisak PPA 软件源 (不变)
Tuple.Create("echo '[+] 添加 Kisak PPA 软件源...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
Tuple.Create("sudo add-apt-repository ppa:kisak/turtle -y", (TimeSpan?)TimeSpan.FromMinutes(2)),
Tuple.Create("sudo apt-get update -qq", (TimeSpan?)TimeSpan.FromMinutes(2)),

// 6.2 【核心修正 v4】创建通过版本号锁定的 APT Pinning 规则
Tuple.Create("echo '[+] 创建通过版本号锁定的 APT Pinning 规则...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
// 首先，用 > 创建并覆盖文件
Tuple.Create("sudo sh -c 'echo \"# 规则: 优先选择版本号中包含 ~kisak 的 mesa-vulkan-drivers\" > /etc/apt/preferences.d/99-mesa-pinning'", (TimeSpan?)TimeSpan.FromSeconds(10)),
// 之后，用 >> 追加内容
Tuple.Create("sudo sh -c 'echo \"Package: mesa-vulkan-drivers\" >> /etc/apt/preferences.d/99-mesa-pinning'", (TimeSpan?)TimeSpan.FromSeconds(10)),
// 【关键】使用版本号通配符 *kisak* 来锁定 PPA 的版本
Tuple.Create("sudo sh -c 'echo \"Pin: version *kisak*\" >> /etc/apt/preferences.d/99-mesa-pinning'", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create("sudo sh -c 'echo \"Pin-Priority: 900\" >> /etc/apt/preferences.d/99-mesa-pinning'", (TimeSpan?)TimeSpan.FromSeconds(10)),

// 6.3 应用规则并安装/升级 Vulkan 驱动
Tuple.Create("echo '[+] 应用规则并强制安装/升级 Vulkan 驱动...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
Tuple.Create("sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mesa-vulkan-drivers", (TimeSpan?)TimeSpan.FromMinutes(5)),

                                // ===================================================================
                                // 新增步骤 4: 从源码编译并安装 dxgkrnl 内核模块
                                // ===================================================================
    Tuple.Create("echo '\n[7/9] 正在编译并安装 GPU-PV 核心内核模块 (dxgkrnl)...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("echo '[+] 赋予安装脚本执行权限...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
                                // remoteLibDir 变量是在这个列表外部定义的，包含了 install.sh 的路径
                                Tuple.Create($"sudo chmod +x {remoteLibDir}/install.sh", (TimeSpan?)TimeSpan.FromSeconds(20)),
    Tuple.Create("echo '[+] 开始执行编译和安装 (此过程耗时最长，请耐心等待)...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
                                Tuple.Create($"sudo {remoteLibDir}/install.sh", (TimeSpan?)TimeSpan.FromMinutes(30)), // 编译和安装非常耗时，给予足够长的超时时间

                                    
                                    // ===================================================================
                                    // 步骤 3: 部署 GPU 驱动和库文件
                                    // ===================================================================
Tuple.Create("echo '\n[8/9] 正在部署驱动文件并配置系统环境...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("echo '[+] 部署 GPU 驱动及库文件到系统路径...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create(withSudo("mkdir -p /usr/lib/wsl/drivers /usr/lib/wsl/lib"), (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create(withSudo("rm -rf /usr/lib/wsl/drivers/* /usr/lib/wsl/lib/*"), (TimeSpan?)TimeSpan.FromMinutes(1)),
    Tuple.Create(withSudo($"cp -r {homeDirectory}/exhyperv_deploy/drivers/* /usr/lib/wsl/drivers/"), (TimeSpan?)TimeSpan.FromMinutes(2)),
    Tuple.Create(withSudo($"cp -a {homeDirectory}/exhyperv_deploy/lib/*.so* /usr/lib/wsl/lib/"), (TimeSpan?)TimeSpan.FromMinutes(2)),
    Tuple.Create($"[ -f {homeDirectory}/exhyperv_deploy/lib/nvidia-smi ] && {withSudo($"cp {homeDirectory}/exhyperv_deploy/lib/nvidia-smi /usr/bin/nvidia-smi")} || echo 'nvidia-smi not found, skipping.'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create($"[ -f /usr/bin/nvidia-smi ] && {withSudo("chmod 755 /usr/bin/nvidia-smi")} || echo 'nvidia-smi not found in /usr/bin, skipping chmod.'", (TimeSpan?)TimeSpan.FromSeconds(10)),
    Tuple.Create(withSudo("ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so"), (TimeSpan?)TimeSpan.FromSeconds(10)),
    Tuple.Create(withSudo("chmod -R 0555 /usr/lib/wsl"), (TimeSpan?)TimeSpan.FromMinutes(1)),
    Tuple.Create(withSudo("chown -R root:root /usr/lib/wsl"), (TimeSpan?)TimeSpan.FromMinutes(1)),

                                    // ===================================================================
                                    // 步骤 4: 配置系统环境 (最终修正版)
                                    // ===================================================================
    Tuple.Create("echo '[+] 配置链接库和全局环境变量...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
                                    
                                    // 4.1. 配置链接库路径
                                    Tuple.Create("sudo sh -c 'echo \"/usr/lib/wsl/lib\" > /etc/ld.so.conf.d/ld.wsl.conf'", (TimeSpan?)TimeSpan.FromSeconds(10)),
                                    Tuple.Create("sudo ldconfig", (TimeSpan?)TimeSpan.FromMinutes(1)),


// 【核心修正】将环境变量配置写入用户 .bashrc 以实现精细化控制
Tuple.Create("echo '[+] 将优化配置写入用户 .bashrc 文件...'", (TimeSpan?)TimeSpan.FromSeconds(30)),

// 创建一个包含所有优化配置的临时脚本文件
Tuple.Create($"echo '' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create($"echo '# ===============================================' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create($"echo '# GPU-PV (D3D12 Backend) Configuration' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create($"echo '# ===============================================' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create($"echo 'export GALLIUM_DRIVERS=d3d12' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create($"echo 'export DRI_PRIME=1' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
// 【关键】为 vainfo 单独设置视频驱动变量
Tuple.Create($"echo 'export LIBVA_DRIVER_NAME=d3d12' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),

// 将临时文件的内容追加到目标用户的 .bashrc 文件中
Tuple.Create($"sudo sh -c 'cat {homeDirectory}/env_tmp >> /home/$SUDO_USER/.bashrc || cat {homeDirectory}/env_tmp >> {homeDirectory}/.bashrc'", (TimeSpan?)TimeSpan.FromSeconds(20)),
Tuple.Create($"rm {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),

// 清理旧的、可能冲突的系统级配置文件
Tuple.Create("echo '[+] 清理系统级全局环境变量以避免冲突...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
Tuple.Create("sudo rm -f /etc/profile.d/99-dxgkrnl.sh", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create("sudo sed -i '/GALLIUM_DRIVERS/d' /etc/environment", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create("sudo sed -i '/MESA_LOADER_DRIVER_OVERRIDE/d' /etc/environment", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create("sudo sed -i '/DRI_PRIME/d' /etc/environment", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create("sudo sed -i '/LIBVA_DRIVER_NAME/d' /etc/environment", (TimeSpan?)TimeSpan.FromSeconds(10)),




                                // ===================================================================
                                // 步骤 4.3: 配置用户权限
                                // ===================================================================
    Tuple.Create("echo '[+] 配置用户硬件访问权限...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
                                Tuple.Create("sudo usermod -a -G video $USER || sudo usermod -a -G video $SUDO_USER || true", (TimeSpan?)TimeSpan.FromSeconds(30)),
                                Tuple.Create("sudo usermod -a -G render $USER || sudo usermod -a -G render $SUDO_USER || true", (TimeSpan?)TimeSpan.FromSeconds(30)),

                                // ===================================================================
                                // 步骤 4.4: 确保核心内核模块开机自启并立即加载
                                // ===================================================================
    Tuple.Create("echo '[+] 配置核心内核模块自启动并立即加载...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
                                // 4.4.1. 设置开机自动加载 (永久生效)
                                Tuple.Create("sudo sh -c \"echo 'vgem' > /etc/modules-load.d/vgem.conf\"", (TimeSpan?)TimeSpan.FromSeconds(20)),
                                Tuple.Create("sudo sh -c \"echo 'dxgkrnl' > /etc/modules-load.d/dxgkrnl.conf\"", (TimeSpan?)TimeSpan.FromSeconds(20)),
                                // 4.4.2. 立即加载模块以供当前会话使用 (即时生效)
                                Tuple.Create("sudo modprobe vgem", (TimeSpan?)TimeSpan.FromSeconds(20)),
                                Tuple.Create("sudo modprobe dxgkrnl", (TimeSpan?)TimeSpan.FromSeconds(20)),

// ===================================================================
// 步骤 4.5: 开放设备节点权限并创建兼容性链接
// ===================================================================
Tuple.Create("echo '[+] 开放设备节点权限并创建兼容性链接...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
Tuple.Create("sudo chmod 666 /dev/dxg", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create("sudo chmod 666 /dev/dri/* || true", (TimeSpan?)TimeSpan.FromSeconds(10)),
// 【核心修正】为 vainfo 等老程序创建 card0 符号链接
Tuple.Create("sudo ln -sf /dev/dri/card1 /dev/dri/card0", (TimeSpan?)TimeSpan.FromSeconds(10)),

                                    // ===================================================================
                                    // 步骤 5: 清理临时文件
                                    // ===================================================================
    Tuple.Create("echo '\n[9/9] 正在清理临时文件...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
                                    Tuple.Create($"rm -rf {homeDirectory}/exhyperv_deploy", (TimeSpan?)TimeSpan.FromMinutes(1)),
                                };


                                const int maxRetries = 2; // 离线操作，减少重试次数
                                foreach (var cmdInfo in commandsToExecute)
                                {
                                    if (cts.IsCancellationRequested) throw new OperationCanceledException();
                                    var command = cmdInfo.Item1;
                                    var timeout = cmdInfo.Item2;

                                    for (int retry = 1; retry <= maxRetries; retry++)
                                    {
                                        try
                                        {
                                            await sshService.ExecuteSingleCommandAsync(credentials, command, log, timeout);
                                            break;
                                        }
                                        catch (Exception ex)
                                        {
                                            // 如果是因为我们手动取消引发的异常，直接抛出
                                            if (cts.IsCancellationRequested) throw new OperationCanceledException();

                                            if (ex is Renci.SshNet.Common.SshOperationTimeoutException || ex.InnerException is TimeoutException)
                                            {
                                                log($"--- 命令执行超时 (尝试 {retry}/{maxRetries}) ---");
                                                if (retry == maxRetries) throw new Exception("命令执行超时，部署中止。", ex);
                                                await Task.Delay(2000, cts.Token); // 使用带 Token 的延迟
                                            }
                                            else
                                            {
                                                throw; // 其他错误直接抛出
                                            }
                                        }
                                    }
                                }

                                updateStatus("部署完成！虚拟机即将重启...");
                                log("\n[+] 部署完成！虚拟机即将重启...");
                                try
                                {
                                    await sshService.ExecuteSingleCommandAsync(credentials, "sudo reboot", log, TimeSpan.FromSeconds(5));
                                }
                                catch {}

                                progressWindow.ShowSuccessState();
                                return "OK";
                            }
                            catch (OperationCanceledException)
                            {
                                // 捕获取消异常，直接返回
                                return "操作已取消";
                            }
                            catch (Exception ex)
                            {
                                // 错误处理逻辑 (重试或退出)
                                string errorMsg = $"部署失败: {ex.Message}";

                                // 确保窗口没关才更新 UI
                                if (!cts.IsCancellationRequested)
                                {
                                    progressWindow.AppendLog($"\n\n--- 错误发生 ---\n{errorMsg}");
                                    progressWindow.ShowErrorState("部署失败，请检查日志");
                                }
                                else
                                {
                                    return "操作被用户强行中止。";
                                }

                                bool shouldRetry = await userActionTcs.Task;
                                if (!shouldRetry) return errorMsg;
                            }
                            finally
                            {
                                progressWindow.RetryClicked -= retryHandler;
                                progressWindow.Closed -= closeHandler;
                            }
                        }
                    }
                    if (isWin10 && partitionableGpuCount > 1)
                    {
                        Utils.Run($"Start-VM -Name '{vmName}'");
                    }
                    return "OK"; 
                }
                catch (Exception ex)
                {
                    ShowMessageOnUIThread($"【严重异常】\n\n操作中发生异常: {ex.Message}");
                    return $"操作失败: {ex.Message}";
                }
                finally
                {
                    if (disabledGpuInstanceIds.Any())
                    {
                        await Task.Delay(1000);
                        foreach (var instanceId in disabledGpuInstanceIds)
                        {
                            Utils.Run($"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false");
                        }
                    }
                    if (isWin10 && partitionableGpuCount > 1)
                    {
                        string finalMessage = "Windows10作为宿主时，本次指定的GPU分区仅本次有效。\n\n" +
                                              "若要确保虚拟机下次冷启动仍使用该GPU，请先手动禁用其他GPU；或通过本工具再次分配。";
                        System.Windows.Application.Current.Dispatcher.Invoke(() => Utils.Show(finalMessage));

                    }
                }
            });
        }
        public Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId)
        {
            return Task.Run(() =>
            {
                var results = Utils.Run2($@"Remove-VMGpuPartitionAdapter -VMName '{vmName}' -AdapterId '{adapterId}' -Confirm:$false");
                if (results != null)
                {
                    string cleanupNotesScript = $@"
                    $vm = Get-VM -Name '{vmName}';
                    $currentNotes = $vm.Notes;
                    if ($currentNotes -match '\[AssignedGPU:[^\]]+\]') {{
                    $cleanedNotes = $currentNotes -replace '\[AssignedGPU:[^\]]+\]', '';
                    Set-VM -VM $vm -Notes $cleanedNotes.Trim();
                        }}
                    ";
                    Utils.Run(cleanupNotesScript);
                    return true;
                }
                return false;
            });
        }

        private string NvidiaReg(string letter)
        {
            // 为NVIDIA显卡注入注册表信息。
            string tempRegFile = Path.Combine(Path.GetTempPath(), $"nvlddmkm_{Guid.NewGuid()}.reg");
            string systemHiveFile = $@"{letter}\Windows\System32\Config\SYSTEM";

            try
            {
                ExecuteCommand($"reg unload HKLM\\OfflineSystem");

                string localKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm";
                if (ExecuteCommand($@"reg export ""{localKeyPath}"" ""{tempRegFile}"" /y") != 0) return Properties.Resources.Error_ExportLocalRegistryInfoFailed;
                if (ExecuteCommand($@"reg load HKLM\OfflineSystem ""{systemHiveFile}""") != 0) return Properties.Resources.Error_OfflineLoadVmRegistryFailed;

                string originalText = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm";
                string targetText = @"HKEY_LOCAL_MACHINE\OfflineSystem\ControlSet001\Services\nvlddmkm";
                string regContent = File.ReadAllText(tempRegFile);
                regContent = regContent.Replace(originalText, targetText);
                regContent = regContent.Replace("DriverStore", "HostDriverStore");
                File.WriteAllText(tempRegFile, regContent);
                ExecuteCommand($@"reg import ""{tempRegFile}""");

                return "OK";
            }
            catch (Exception ex)
            {
                return string.Format(Properties.Resources.Error_NvidiaRegistryProcessingException, ex.Message);
            }
            finally
            {
                ExecuteCommand($"reg unload HKLM\\OfflineSystem");
                if (File.Exists(tempRegFile))
                {
                    File.Delete(tempRegFile);
                }
            }
        }

        private int ExecuteCommand(string command)
        {
            try
            {
                Process process = new()
                {
                    StartInfo =
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {command}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit();
                return process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        private void SetFolderReadOnly(string folderPath)
        {
            var dirInfo = new DirectoryInfo(folderPath);
            dirInfo.Attributes |= FileAttributes.ReadOnly;
            foreach (var subDir in dirInfo.GetDirectories())
            {
                SetFolderReadOnly(subDir.FullName);
            }
            foreach (var file in dirInfo.GetFiles())
            {
                file.Attributes |= FileAttributes.ReadOnly;
            }
        }

        private void RemoveReadOnlyAttribute(string path)
        {
            if (Directory.Exists(path))
            {
                RemoveReadOnlyAttribute(new DirectoryInfo(path));
            }
        }

        private void RemoveReadOnlyAttribute(DirectoryInfo dirInfo)
        {
            dirInfo.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var subDir in dirInfo.GetDirectories())
            {
                RemoveReadOnlyAttribute(subDir);
            }
            foreach (var file in dirInfo.GetFiles())
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        public async Task<bool> IsHyperVModuleAvailableAsync()
        {
            return await Task.Run(() =>
            {
                var result = Utils.Run("(Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V).State");
                return result.Count > 0;
            });
        }

        /// <summary>
        /// 准备虚拟机部署所需的所有文件。
        /// 它会优先从主机系统上传库文件，然后上传自定义安装脚本。
        /// 最后，它会通过 SSH 在虚拟机内部检查核心库文件是否存在，如果不存在，则从网络下载。
        /// </summary>
        private async Task UploadLocalFilesAsync(SshService sshService, SshCredentials credentials, string remoteDirectory)
        {
            // ===================================================================================================
            // 步骤 1: 从主机上传文件 (如果存在)
            // ===================================================================================================
            string systemWslLibPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "lxss", "lib");

            if (Directory.Exists(systemWslLibPath))
            {
                var filesInSystemDir = Directory.GetFiles(systemWslLibPath);
                foreach (var filePath in filesInSystemDir)
                {
                    string fileName = Path.GetFileName(filePath);
                    await sshService.UploadFileAsync(credentials, filePath, $"{remoteDirectory}/{fileName}");
                }
            }
            // 如果主机目录不存在（例如N卡环境），则此步骤被安全跳过。

            // ===================================================================================================
            // 步骤 2: 从应用程序资源上传必要的脚本
            // ===================================================================================================
            string baseDirectory = AppContext.BaseDirectory;
            string localAssetDirectory = Path.Combine(baseDirectory, "Assets", "linuxlib");
            string installScriptPath = Path.Combine(localAssetDirectory, "install.sh");

            if (!File.Exists(installScriptPath))
            {
                throw new FileNotFoundException($"错误：无法在应用程序资源文件夹中找到安装脚本 '{installScriptPath}'。\n\n" +
                                                "请确保 install.sh 文件已包含在项目中，并设置为“内容”以及“如果较新则复制”。");
            }
            await sshService.UploadFileAsync(credentials, installScriptPath, $"{remoteDirectory}/install.sh");

            // ===================================================================================================
            // 步骤 3: 在虚拟机内部检查并按需下载缺失的核心库文件
            // ===================================================================================================
            const string GitHubRawContentBaseUrl = "https://raw.githubusercontent.com/Justsenger/wsl2lib/main/";
            string[] coreDxFiles = { "libd3d12.so", "libd3d12core.so", "libdxcore.so" };

            // 定义一个简单的日志委托，用于在控制台或UI上显示进度
            // 如果您能从这里访问 ExecutionProgressWindow.AppendLog，效果会更好
            Action<string> log = (message) => Debug.WriteLine(message);

            log("正在通过 SSH 检查并下载缺失的核心库文件...");

            foreach (var file in coreDxFiles)
            {
                string remoteFilePath = $"{remoteDirectory}/{file}";
                string fileUrl = GitHubRawContentBaseUrl + file;

                // 构建一个单行的 shell 命令: [ ! -f "文件路径" ] && wget ...
                string checkAndDownloadCommand = $"[ ! -f \"{remoteFilePath}\" ] && echo \"[+] 文件 {file} 不存在，正在从网络下载...\" && wget -q -c \"{fileUrl}\" -O \"{remoteFilePath}\" || echo \"[✓] 文件 {file} 已存在，跳过下载。\"";

                try
                {
                    // 使用 SshService 执行这个命令
                    await sshService.ExecuteSingleCommandAsync(credentials, checkAndDownloadCommand, log, TimeSpan.FromMinutes(5));
                }
                catch (Exception ex)
                {
                    // 如果命令执行失败（例如，wget 未安装或网络问题），抛出更详细的异常
                    throw new InvalidOperationException($"在虚拟机内部检查或下载文件 '{file}' 时失败。请确保虚拟机已安装 'wget' 并且可以访问互联网。\n\n命令: {checkAndDownloadCommand}\n错误: {ex.Message}", ex);
                }
            }

            log("核心库文件准备完毕。");
        }
    }
}