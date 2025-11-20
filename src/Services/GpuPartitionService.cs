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

        private const string ScriptBaseUrl = "https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/";
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

        private async Task<string> InjectWindowsDriversAsync(string vmName, string harddiskpath, PartitionInfo partition, string gpuManu, string gpuInstancePath)
        {
            string assignedDriveLetter = null;

            try
            {
                // 1. 挂载分区
                char suggestedLetter = GetFreeDriveLetter();
                var mountScript = $@"
            $diskImage = Mount-DiskImage -ImagePath '{harddiskpath}' -PassThru | Get-Disk;
            $partitionToMount = Get-Partition -DiskNumber $diskImage.Number | Where-Object {{ $_.PartitionNumber -eq {partition.PartitionNumber} }};
            
            if ($partitionToMount.DriveLetter) {{ return $partitionToMount.DriveLetter }}
            
            try {{
                $partitionToMount | Set-Partition -NewDriveLetter '{suggestedLetter}' -ErrorAction Stop;
                return '{suggestedLetter}'
            }} catch {{
                return ($partitionToMount | Get-Partition).DriveLetter
            }}
        ";

                var letterResult = Utils.Run(mountScript);

                if (letterResult == null || letterResult.Count == 0 || string.IsNullOrEmpty(letterResult[0].ToString()))
                {
                    return string.Format(Properties.Resources.Error_FailedToFindSystemPartition, harddiskpath);
                }

                string actualLetter = letterResult[0].ToString();
                assignedDriveLetter = actualLetter.EndsWith(":") ? actualLetter : $"{actualLetter}:";

                // 2. 验证系统路径
                string system32Path = Path.Combine(assignedDriveLetter, "Windows", "System32");
                if (!Directory.Exists(system32Path))
                {
                    return string.Format("所选分区似乎不是一个有效的Windows系统分区，或已开启Bitlocker。", partition.PartitionNumber, assignedDriveLetter, system32Path);
                }

                string letter = assignedDriveLetter.TrimEnd(':');
                string driverStoreBase = @"C:\Windows\System32\DriverStore\FileRepository";

                string sourceFolder = FindGpuDriverSourcePath(gpuInstancePath);


                // 逻辑判定：如果找到了具体文件夹，就只复制那个文件夹；如果没找到，回退到全量复制
                bool isFullCopy = false;
                if (string.IsNullOrEmpty(sourceFolder))
                {
                    sourceFolder = driverStoreBase;
                    isFullCopy = true;
                }

                // 确定目标路径
                string destinationBase = letter + @":\Windows\System32\HostDriverStore\FileRepository";
                string destinationFolder;

                if (isFullCopy)
                {
                    destinationFolder = destinationBase;
                }
                else
                {
                    // 精准复制模式：目标路径要包含具体的驱动文件夹名
                    string folderName = new DirectoryInfo(sourceFolder).Name;
                    destinationFolder = Path.Combine(destinationBase, folderName);
                }

                // 创建目录结构
                if (!Directory.Exists(destinationBase)) Directory.CreateDirectory(destinationBase);
                if (!Directory.Exists(destinationFolder)) Directory.CreateDirectory(destinationFolder);

                // 处理只读属性
                if (Directory.Exists(destinationFolder))
                {
                    try { RemoveReadOnlyAttribute(destinationFolder); }
                    catch (Exception ex) { return string.Format(Properties.Resources.Error_RemoveOldDriverFolderReadOnlyFailed, ex.Message); }
                }

                // 执行 Robocopy
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

                // 3. 注册表注入
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
                            Remove-PartitionAccessPath -DiskNumber $diskImage.Number -PartitionNumber {partition.PartitionNumber} -AccessPath '{assignedDriveLetter}\' -ErrorAction SilentlyContinue;
                         }} catch {{}}
                    }}
                    Dismount-DiskImage -ImagePath '{harddiskpath}' -ErrorAction SilentlyContinue;
                }}
            ";
                    Utils.Run(cleanupScript);
                }
            }
        }
        public Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu, PartitionInfo selectedPartition, string id)
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
                        string injectionResult = await InjectWindowsDriversAsync(vmName, harddiskpath, selectedPartition, gpuManu, id);
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
                            vmIpAddress = await Utils.GetVmIpAddressAsync(vmName, macAddressWithColons);
                            stopwatch.Stop();

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


                                updateStatus("[1/5] 正在连接到虚拟机...");
                                log("[1/5] 正在连接到虚拟机并初始化远程环境...");
                                string homeDirectory;
                                string remoteTempDir;

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

                                    remoteTempDir = $"{homeDirectory}/exhyperv_deploy";
                                    client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                                    log($"[+] 已创建临时部署目录: {remoteTempDir}");
                                    client.Disconnect();
                                }
                                log("[✓] 远程环境初始化完成。");

                                if (!string.IsNullOrEmpty(credentials.ProxyHost) && credentials.ProxyPort.HasValue)
                                {
                                    updateStatus("[2/5] 正在配置网络代理...");
                                    log("\n[2/5] 正在为虚拟机配置 HTTP 网络代理...");
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

                                log("\n[3/5] 正在定位主机 GPU 驱动...");
                                string driverStoreBase = @"C:\Windows\System32\DriverStore\FileRepository";
                                string preciseDriverPath = FindGpuDriverSourcePath(id);
                                string sourceDriverPath = preciseDriverPath; // 默认为精准路径

                                if (string.IsNullOrEmpty(preciseDriverPath))
                                {
                                    log("[!] 未能定位到特定 GPU 驱动文件夹，将执行全量驱动拷贝。");
                                    sourceDriverPath = driverStoreBase; // 回退到全量拷贝
                                }
                                else
                                {
                                    log($"[✓] 已定位到精准驱动路径: {new DirectoryInfo(preciseDriverPath).Name}");
                                }


                                updateStatus("[3/5] 正在上传驱动与库文件...");
                                log("\n[3/5] 正在上传主机 GPU 驱动与本地库文件...");
                                await sshService.UploadDirectoryAsync(credentials, @"C:\Windows\System32\DriverStore\FileRepository", $"{remoteTempDir}/drivers");
                                log("[✓] 主机驱动文件上传完毕。");
                                await UploadLocalFilesAsync(sshService, credentials, $"{remoteTempDir}/lib");
                                log("[✓] 本地核心库检查完毕。");

                                updateStatus("[4/5] 正在下载并执行部署脚本...");
                                log("\n[4/5] 正在从 GitHub 下载最新部署脚本...");

                                // =========================================================
                                // 核心修改：命令生成逻辑替换
                                // =========================================================
                                var commandsToExecute = new List<Tuple<string, TimeSpan?>>();
                                bool enableGraphics = credentials.InstallGraphics;

                                // 1. 下载脚本
                                commandsToExecute.Add(Tuple.Create($"wget -O {remoteTempDir}/install_dxgkrnl.sh {ScriptBaseUrl}install_dxgkrnl.sh", (TimeSpan?)TimeSpan.FromMinutes(2)));
                                if (enableGraphics)
                                {
                                    commandsToExecute.Add(Tuple.Create($"wget -O {remoteTempDir}/setup_graphics.sh {ScriptBaseUrl}setup_graphics.sh", (TimeSpan?)TimeSpan.FromMinutes(2)));
                                }
                                commandsToExecute.Add(Tuple.Create($"wget -O {remoteTempDir}/configure_system.sh {ScriptBaseUrl}configure_system.sh", (TimeSpan?)TimeSpan.FromMinutes(2)));
                                commandsToExecute.Add(Tuple.Create($"chmod +x {remoteTempDir}/*.sh", (TimeSpan?)TimeSpan.FromSeconds(10)));

                                // 2. 编译内核模块
                                commandsToExecute.Add(Tuple.Create(withSudo($"{remoteTempDir}/install_dxgkrnl.sh"), (TimeSpan?)TimeSpan.FromMinutes(60)));

                                // 3. 图形环境 (可选)
                                if (enableGraphics)
                                {
                                    commandsToExecute.Add(Tuple.Create(withSudo($"{remoteTempDir}/setup_graphics.sh"), (TimeSpan?)TimeSpan.FromMinutes(20)));
                                }

                                // 4. 系统配置
                                string configArgs = enableGraphics ? "enable_graphics" : "no_graphics";
                                commandsToExecute.Add(Tuple.Create(withSudo($"{remoteTempDir}/configure_system.sh {configArgs}"), (TimeSpan?)TimeSpan.FromMinutes(5)));

                                const int maxRetries = 2;
                                foreach (var cmdInfo in commandsToExecute)
                                {
                                    if (cts.IsCancellationRequested) throw new OperationCanceledException();

                                    if (cmdInfo.Item1.Contains("install_dxgkrnl.sh"))
                                        log("\n[!] 正在编译内核模块，此过程可能需要较长时间 (10-30分钟)，请耐心等待...");
                                    else if (cmdInfo.Item1.Contains("setup_graphics.sh"))
                                        log("\n[!] 正在配置 Mesa 图形环境...");

                                    for (int retry = 1; retry <= maxRetries; retry++)
                                    {
                                        try
                                        {
                                            await sshService.ExecuteSingleCommandAsync(credentials, cmdInfo.Item1, log, cmdInfo.Item2);
                                            break;
                                        }
                                        catch (Exception ex)
                                        {
                                            if (cts.IsCancellationRequested) throw new OperationCanceledException();
                                            if (ex is Renci.SshNet.Common.SshOperationTimeoutException || ex.InnerException is TimeoutException)
                                            {
                                                log($"--- 命令执行超时 (尝试 {retry}/{maxRetries}) ---");
                                                if (retry == maxRetries) throw new Exception("命令执行超时，部署中止。", ex);
                                                await Task.Delay(2000, cts.Token);
                                            }
                                            else throw;
                                        }
                                    }
                                }

                                updateStatus("部署完成！虚拟机即将重启...");
                                log("\n[5/5] 部署完成！虚拟机即将重启...");
                                try
                                {
                                    await sshService.ExecuteSingleCommandAsync(credentials, "sudo reboot", log, TimeSpan.FromSeconds(5));
                                }
                                catch { }

                                progressWindow.ShowSuccessState();
                                return "OK";
                            }
                            catch (OperationCanceledException)
                            {
                                return "操作已取消";
                            }
                            catch (Exception ex)
                            {
                                string errorMsg = $"部署失败: {ex.Message}";
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

        private async Task UploadLocalFilesAsync(SshService sshService, SshCredentials credentials, string remoteDirectory)
        {
            string systemWslLibPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "lxss", "lib");

            if (Directory.Exists(systemWslLibPath))
            {
                var allFiles = Directory.GetFiles(systemWslLibPath);

                foreach (var filePath in allFiles)
                {
                    string fileName = Path.GetFileName(filePath);
                    await sshService.UploadFileAsync(credentials, filePath, $"{remoteDirectory}/{fileName}");
                }
            }
        }
        private string FindGpuDriverSourcePath(string gpuInstancePath)
        {
            string sourceFolder = null;
            string driverStoreBase = @"C:\Windows\System32\DriverStore\FileRepository";

            // WMI 极速查询脚本 (从 InjectWindowsDriversAsync 方法中复制过来)
            string fastScript = $@"
        $ErrorActionPreference = 'Stop';
        try {{
            $targetId = '{gpuInstancePath}'.Trim();
            $wmi = Get-CimInstance Win32_VideoController | Where-Object {{ $_.PNPDeviceID -like ""*$targetId*"" }} | Select-Object -First 1;
            
            if ($wmi -and $wmi.InstalledDisplayDrivers) {{
                $drivers = $wmi.InstalledDisplayDrivers -split ',';
                $repoDriver = $drivers | Where-Object {{ $_ -match 'FileRepository' }} | Select-Object -First 1;
                
                if ($repoDriver) {{
                    return (Split-Path -Parent $repoDriver.Trim())
                }}
            }}
        }} catch {{ }}";

            try
            {
                var fastRes = Utils.Run(fastScript);
                if (fastRes != null && fastRes.Count > 0 && fastRes[0] != null)
                {
                    string resultPath = fastRes[0].ToString().Trim();
                    if (!string.IsNullOrEmpty(resultPath) && Directory.Exists(resultPath))
                    {
                        sourceFolder = resultPath;
                    }
                }
            }
            catch { /* 忽略异常，后面会处理 sourceFolder 为 null 的情况 */ }

            // 如果没找到，返回 null。如果找到了，返回具体路径。
            return sourceFolder;
        }
    }
}