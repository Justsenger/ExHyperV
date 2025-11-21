using System.Diagnostics;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Views;  
using Renci.SshNet;
using System.Net.Sockets; 


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


        //SSH重新连接
        private async Task<bool> WaitForVmToBeResponsiveAsync(string host, int port, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromMinutes(1)) // 1分钟总超时
            {
                if (cancellationToken.IsCancellationRequested) return false;
                try
                {
                    using (var client = new TcpClient())
                    {
                        var connectTask = client.ConnectAsync(host, port);
                        if (await Task.WhenAny(connectTask, Task.Delay(2000, cancellationToken)) == connectTask)
                        {
                            await connectTask;
                            return true;
                        }
                    }
                }
                catch {}
                await Task.Delay(5000, cancellationToken);
            }
            return false; // 超时
        }

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


        //挂载VHDX时寻找可用的盘符，可能存在问题。
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
            throw new IOException(ExHyperV.Properties.Resources.Error_NoAvailableDriveLetters);
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
                        throw new FileNotFoundException(string.Format(Properties.Resources.Error_VmHardDiskNotFound, vmName));
                    }
                    harddiskpath = harddiskPathResult[0].ToString();

                    var mountScript = $@"
                $diskImage = Mount-DiskImage -ImagePath '{harddiskpath}' -NoDriveLetter -PassThru;
                ($diskImage | Get-Disk).Number;
            ";
                    var mountResult = Utils.Run(mountScript);

                    if (mountResult == null || mountResult.Count == 0 || !int.TryParse(mountResult[0].ToString(), out int num))
                    {
                        throw new InvalidOperationException(ExHyperV.Properties.Resources.Error_MountVhdOrGetDiskNumberFailed);
                    }
                    diskNumber = num;
                    string devicePath = $@"\\.\PhysicalDrive{diskNumber}";
                    var diskParser = new DiskParserService();
                    List<PartitionInfo> initialPartitions = diskParser.GetPartitions(devicePath);
                    return initialPartitions;
                }
                catch (UnauthorizedAccessException)
                {
                    throw new UnauthorizedAccessException(ExHyperV.Properties.Resources.Error_AdminRequiredForPartitionInfo);
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
                    return string.Format(Properties.Resources.Error_InvalidWindowsPartition, partition.PartitionNumber, assignedDriveLetter, system32Path);
                }

                string letter = assignedDriveLetter.TrimEnd(':');
                string driverStoreBase = @"C:\Windows\System32\DriverStore\FileRepository";

                string sourceFolder = FindGpuDriverSourcePath(gpuInstancePath);


                // 逻辑判定：如果找到了驱动文件夹，就只复制那个文件夹；如果没找到，回退到全量复制
                bool isFullCopy = false;
                if (string.IsNullOrEmpty(sourceFolder))
                {
                    sourceFolder = driverStoreBase;
                    isFullCopy = true;
                }
                string destinationBase = letter + @":\Windows\System32\HostDriverStore\FileRepository";
                string destinationFolder;

                if (isFullCopy)
                {
                    destinationFolder = destinationBase;
                }
                else
                {
                    // 精准复制模式
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
                        return string.Format(Properties.Resources.Error_VmMustBeOff, vmName);
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
                            return ExHyperV.Properties.Resources.Error_GetVmHardDiskPathFailed;
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
                            return $"echo '{escapedPassword}' | sudo -S -E -p '' {cmd}";
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
                                return string.Format(Properties.Resources.Error_GetVmMacAddressFailed, vmName);
                            }
                            string macAddressWithoutColons = macResult[0].ToString();
                            string macAddressWithColons = System.Text.RegularExpressions.Regex.Replace(macAddressWithoutColons, "(.{2})", "$1:").TrimEnd(':');

                            string vmIpAddress = string.Empty;
                            var stopwatch = Stopwatch.StartNew();
                            vmIpAddress = await Utils.GetVmIpAddressAsync(vmName, macAddressWithColons);
                            stopwatch.Stop();

                            string targetIp = vmIpAddress.Split(',').Select(ip => ip.Trim()).FirstOrDefault(ip => System.Net.IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                            //弹出登录框
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                var loginWindow = new SshLoginWindow(vmName, targetIp);
                                if (loginWindow.ShowDialog() == true)
                                {
                                    credentials = loginWindow.Credentials;
                                }
                            });

                            if (credentials == null)return ExHyperV.Properties.Resources.Info_SshLoginCancelledByUser;
                            if (!string.IsNullOrEmpty(targetIp))
                            {
                                credentials.Host = targetIp;
                            }
                            if (string.IsNullOrEmpty(credentials.Host))
                            {
                                return string.Format(Properties.Resources.Error_NoValidIpv4AddressFound, "Unknown");
                            }

                        }
                        catch (Exception ex)
                        {
                            ShowMessageOnUIThread(string.Format(Properties.Resources.Error_PreparationFailed, ex.Message));
                            return string.Format(Properties.Resources.Error_PreparationFailed, ex.Message);
                        }


                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            progressWindow = new ExecutionProgressWindow();
                            progressWindow.Show();
                        });

                        progressWindow.Closed += (s, e) =>
                        {
                            cts.Cancel();
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

                                if (cts.IsCancellationRequested) throw new OperationCanceledException();


                                updateStatus(ExHyperV.Properties.Resources.LinuxDeploy_Step1);
                                log(ExHyperV.Properties.Resources.LinuxDeploy_Step1);
                                string homeDirectory;
                                string remoteTempDir;

                                using (var client = new SshClient(credentials.Host, credentials.Port, credentials.Username, credentials.Password))
                                {
                                    client.Connect();
                                    log(ExHyperV.Properties.Resources.Log_SshConnectionSuccess);
                                    var pwdResult = client.RunCommand("pwd");
                                    homeDirectory = pwdResult.Result.Trim();
                                    if (string.IsNullOrEmpty(homeDirectory))
                                    {
                                        throw new Exception(ExHyperV.Properties.Resources.Error_GetLinuxHomeDirectoryFailed);
                                    }
                                    log(string.Format(Properties.Resources.Log_LinuxHomeDirectoryFound, homeDirectory));

                                    remoteTempDir = $"{homeDirectory}/exhyperv_deploy";
                                    client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                                    log(string.Format(Properties.Resources.Log_TempDeployDirectoryCreated, remoteTempDir));
                                    client.Disconnect();
                                }
                                log(ExHyperV.Properties.Resources.Log_RemoteEnvInitializationComplete);

                                if (!string.IsNullOrEmpty(credentials.ProxyHost) && credentials.ProxyPort.HasValue)
                                {
                                    updateStatus(ExHyperV.Properties.Resources.LinuxDeploy_Step2);
                                    log(ExHyperV.Properties.Resources.LinuxDeploy_Step2);
                                    log(string.Format(Properties.Resources.Log_ProxyServerInfo, credentials.ProxyHost, credentials.ProxyPort));
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
                                    log(ExHyperV.Properties.Resources.Log_ProxyConfigurationComplete);
                                }

                                log(ExHyperV.Properties.Resources.LinuxDeploy_Step3);
                                string driverStoreBase = @"C:\Windows\System32\DriverStore\FileRepository";
                                string preciseDriverPath = FindGpuDriverSourcePath(id);
                                string sourceDriverPath = preciseDriverPath; // 默认为精准路径

                                if (string.IsNullOrEmpty(preciseDriverPath))
                                {
                                    log(ExHyperV.Properties.Resources.Log_GpuDriverNotFoundFallback);
                                    sourceDriverPath = driverStoreBase; // 回退到全量拷贝
                                }
                                else
                                {
                                    log(string.Format(Properties.Resources.Log_PreciseDriverPathLocated, new DirectoryInfo(preciseDriverPath).Name));
                                }


                                updateStatus(ExHyperV.Properties.Resources.LinuxDeploy_Step3_Status_Import);
                                log(ExHyperV.Properties.Resources.LinuxDeploy_Step3_Status_Import);
                                string sourceFolderName = new DirectoryInfo(sourceDriverPath).Name;
                                string remoteDestinationPath = $"{remoteTempDir}/drivers/{sourceFolderName}";
                                await sshService.UploadDirectoryAsync(credentials, sourceDriverPath, remoteDestinationPath);
                                log(ExHyperV.Properties.Resources.Log_HostDriverImportComplete);
                                await UploadLocalFilesAsync(sshService, credentials, $"{remoteTempDir}/lib");
                                log(ExHyperV.Properties.Resources.Log_LocalLibrariesCheckComplete);

                                updateStatus(ExHyperV.Properties.Resources.LinuxDeploy_Step4);
                                log(ExHyperV.Properties.Resources.LinuxDeploy_Step4);

                                var commandsToExecute = new List<Tuple<string, TimeSpan?>>();
                                bool enableGraphics = credentials.InstallGraphics;

                                // 1. 下载脚本
                                var scriptsToDownload = new List<string>
                                {
                                    $"wget -O {remoteTempDir}/install_dxgkrnl.sh {ScriptBaseUrl}install_dxgkrnl.sh",
                                    enableGraphics ? $"wget -O {remoteTempDir}/setup_graphics.sh {ScriptBaseUrl}setup_graphics.sh" : null,
                                    $"wget -O {remoteTempDir}/configure_system.sh {ScriptBaseUrl}configure_system.sh"
                                }.Where(s => s != null).ToList();

                                foreach (var scriptCmd in scriptsToDownload)
                                {
                                    await sshService.ExecuteSingleCommandAsync(credentials, scriptCmd, log, TimeSpan.FromMinutes(2));
                                }
                                await sshService.ExecuteSingleCommandAsync(credentials, $"chmod +x {remoteTempDir}/*.sh", log, TimeSpan.FromSeconds(10));
                                log(ExHyperV.Properties.Resources.Log_DxgkrnlModuleCompiling);
                                string dxgkrnlCommand = withSudo($"{remoteTempDir}/install_dxgkrnl.sh");
                                var dxgkrnlResult = await sshService.ExecuteCommandAndCaptureOutputAsync(credentials, dxgkrnlCommand, log, TimeSpan.FromMinutes(60));
                                if (dxgkrnlResult.Output.Contains("STATUS: REBOOT_REQUIRED"))
                                {
                                    log(ExHyperV.Properties.Resources.Log_KernelUpdateRebootRequired);
                                    updateStatus(ExHyperV.Properties.Resources.Status_RebootingVm);

                                    try
                                    {
                                        await sshService.ExecuteSingleCommandAsync(credentials, withSudo("reboot"), log, TimeSpan.FromSeconds(10));
                                    }
                                    catch (Exception) {}
                                    log(ExHyperV.Properties.Resources.Log_WaitingForVmToComeOnline);
                                    bool isVmUp = await WaitForVmToBeResponsiveAsync(credentials.Host, credentials.Port, cts.Token);
                                    if (!isVmUp) throw new Exception(ExHyperV.Properties.Resources.Error_VmDidNotComeBackOnline);

                                    log(ExHyperV.Properties.Resources.Log_VmReconnectedRestartingDeploy);
                                    continue; //重新执行整个流程
                                }

                                if (!dxgkrnlResult.Output.Contains("STATUS: SUCCESS"))
                                {
                                    throw new Exception(ExHyperV.Properties.Resources.Error_KernelModuleScriptFailed);
                                }

                                log(ExHyperV.Properties.Resources.Log_KernelModuleInstallSuccess);


                                if (enableGraphics)
                                {
                                    log(ExHyperV.Properties.Resources.Log_ConfiguringMesa);
                                    await sshService.ExecuteSingleCommandAsync(credentials, withSudo($"{remoteTempDir}/setup_graphics.sh"), log, TimeSpan.FromMinutes(20));
                                }

                                log(ExHyperV.Properties.Resources.Log_ConfiguringSystem);
                                string configArgs = enableGraphics ? "enable_graphics" : "no_graphics";
                                await sshService.ExecuteSingleCommandAsync(credentials, withSudo($"{remoteTempDir}/configure_system.sh {configArgs}"), log, TimeSpan.FromMinutes(5));



                                updateStatus(ExHyperV.Properties.Resources.LinuxDeploy_Step5);
                                log(ExHyperV.Properties.Resources.LinuxDeploy_Step5);
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
                                return ExHyperV.Properties.Resources.Info_OperationCancelled;
                            }
                            catch (Exception ex)
                            {
                                string errorMsg = string.Format(Properties.Resources.Error_DeploymentFailed, ex.Message);
                                if (!cts.IsCancellationRequested)
                                {
                                    progressWindow.AppendLog(string.Format(Properties.Resources.Log_ErrorBlockHeader, errorMsg));
                                    progressWindow.ShowErrorState(ExHyperV.Properties.Resources.Status_DeploymentFailedCheckLogs);
                                }
                                else
                                {
                                    return ExHyperV.Properties.Resources.Info_OperationAbortedByUser;
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
                    ShowMessageOnUIThread(string.Format(Properties.Resources.Error_FatalExceptionOccurred, ex.Message));
                    return string.Format(Properties.Resources.Error_OperationFailed, ex.Message);
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
                        string finalMessage = Properties.Resources.Warning_Win10GpuAssignmentNotPersistent;
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

            //极速查询
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
            catch {}
            return sourceFolder;
        }
    }
}