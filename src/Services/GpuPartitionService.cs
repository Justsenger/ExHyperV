using System.Diagnostics;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Views;   // 引用 SshLoginWindow
using Renci.SshNet;     // 引用 SSH.NET 库

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
                        var currentState = await GetVmStateAsync(vmName);
                        if (currentState != "Running")
                        {
                            Utils.Run($"Start-VM -Name '{vmName}'");
                        }
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                            var loginWindow = new SshLoginWindow();
                            if (loginWindow.ShowDialog() == true)
                            {
                                credentials = loginWindow.Credentials;
                            }
                        });
                        if (credentials == null)
                        {
                            return "用户取消了 SSH 登录操作。";
                        }
                        try
                        {
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                progressWindow = new ExecutionProgressWindow();
                                progressWindow.Show();
                            });
                            Action<string> log = (message) => progressWindow.AppendLog(message);
                            Action<string> updateStatus = (status) => progressWindow.UpdateStatus(status);
                            updateStatus("正在连接到虚拟机并准备环境...");
                            string homeDirectory;
                            using (var client = new SshClient(credentials.Host, credentials.Username, credentials.Password))
                            {
                                client.Connect();
                                log("SSH 连接成功。");
                                var pwdResult = client.RunCommand("pwd");
                                homeDirectory = pwdResult.Result.Trim();
                                if (string.IsNullOrEmpty(homeDirectory))
                                {
                                    throw new Exception("无法获取Linux主目录路径。");
                                }
                                log($"获取到Linux主目录: {homeDirectory}");

                                string remoteTempDir = $"{homeDirectory}/exhyperv_deploy";
                                client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                                log($"已创建部署目录: {remoteTempDir}");
                                client.Disconnect();
                            }
                            if (!string.IsNullOrEmpty(credentials.ProxyHost) && credentials.ProxyPort.HasValue)
                            {
                                updateStatus("正在为虚拟机配置 HTTP 代理...");
                                log($"代理设置: {credentials.ProxyHost}:{credentials.ProxyPort}");
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
                                log("代理配置完成。");
                                log("导入驱动中...");
                            }
                            string remoteDriversDir = $"{homeDirectory}/exhyperv_deploy/drivers";
                            string remoteLibDir = $"{homeDirectory}/exhyperv_deploy/lib";
                            updateStatus("正在导入驱动... 此过程比较耗时，请耐心等待。");
                            await sshService.UploadDirectoryAsync(credentials, @"C:\Windows\System32\DriverStore\FileRepository", remoteDriversDir);
                            log("驱动文件导入完毕。");
                            updateStatus("正在导入核心库文件和安装脚本...");
                            await UploadLocalFilesAsync(sshService, credentials, remoteLibDir);
                            log("核心库文件和安装脚本上传完毕。");
                            updateStatus("正在准备并执行安装... 请在下方查看实时进度。");
                            // 在 GpuPartitionService.cs 中

                            var commandsToExecute = new List<Tuple<string, TimeSpan?>>
{
    // ===================================================================
    // 步骤 1: 环境准备 - 纠正 Mesa 版本并安装核心依赖
    // ===================================================================
    Tuple.Create("echo '[1/7] 正在准备环境，确保使用系统默认的稳定版 Mesa 驱动...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    
    // 1.1 安装 ppa-purge 工具，用于安全地移除 PPA 及其软件包
    Tuple.Create("echo '[+] 正在安装 ppa-purge 工具...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo apt-get install -y ppa-purge", (TimeSpan?)TimeSpan.FromMinutes(2)),

    // 1.2 移除可能存在的不稳定 Kisak PPA，并将 Mesa 降级到官方稳定版
    Tuple.Create("echo '[+] 正在移除 Kisak PPA (如果存在) 并降级相关软件包...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    // 使用 || true 确保即使 PPA 不存在，脚本也不会因错误而中止
    Tuple.Create("sudo ppa-purge -y ppa:kisak/turtle || true", (TimeSpan?)TimeSpan.FromMinutes(3)),
    Tuple.Create("sudo ppa-purge -y ppa:kisak/kisak-mesa || true", (TimeSpan?)TimeSpan.FromMinutes(3)),

    // ===================================================================
    // 步骤 2: 更新系统并安装所有依赖
    // ===================================================================
    Tuple.Create("echo '[2/7] 正在更新软件包列表并升级系统...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo apt-get update", (TimeSpan?)TimeSpan.FromMinutes(5)),
    Tuple.Create("sudo apt-get upgrade -y", (TimeSpan?)TimeSpan.FromMinutes(15)),

    // 2.1 强制重装核心图形组件，确保状态纯净
    Tuple.Create("echo '[3/7] 正在强制重装核心图形组件以确保稳定...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo apt-get install --reinstall -y mesa-va-drivers vainfo libgl1-mesa-dri libglx-mesa0 libgbm1", (TimeSpan?)TimeSpan.FromMinutes(5)),

    // 2.2 安装编译内核模块及其他功能所需的全部依赖包
    Tuple.Create("echo '[+] 正在安装所有其他必要的依赖包...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo apt-get install -y linux-headers-$(uname -r) build-essential git dkms curl mesa-utils mesa-vulkan-drivers", (TimeSpan?)TimeSpan.FromMinutes(10)),
    
    // ===================================================================
// 新增步骤 4: 从源码编译并安装 dxgkrnl 内核模块
// ===================================================================
Tuple.Create("echo '[4/7] 正在编译并安装 GPU-PV 核心内核模块 (dxgkrnl)... 此过程耗时较长，请耐心等待。'", (TimeSpan?)TimeSpan.FromSeconds(30)),
// remoteLibDir 变量是在这个列表外部定义的，包含了 install.sh 的路径
Tuple.Create($"sudo chmod +x {remoteLibDir}/install.sh", (TimeSpan?)TimeSpan.FromSeconds(20)),
// 使用 sudo 执行脚本，因为它需要系统权限来安装 DKMS 模块
Tuple.Create($"sudo {remoteLibDir}/install.sh", (TimeSpan?)TimeSpan.FromMinutes(30)), // 编译和安装非常耗时，给予足够长的超时时间

    
    // ===================================================================
    // 步骤 3: 部署 GPU 驱动和库文件
    // ===================================================================
    Tuple.Create("echo '[5/7] 正在部署 GPU 驱动及库文件到系统标准路径...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo mkdir -p /usr/lib/wsl/drivers /usr/lib/wsl/lib", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo rm -rf /usr/lib/wsl/drivers/* /usr/lib/wsl/lib/*", (TimeSpan?)TimeSpan.FromMinutes(1)),
    Tuple.Create($"sudo cp -r {homeDirectory}/exhyperv_deploy/drivers/* /usr/lib/wsl/drivers/", (TimeSpan?)TimeSpan.FromMinutes(2)),
    Tuple.Create($"sudo cp -a {homeDirectory}/exhyperv_deploy/lib/*.so* /usr/lib/wsl/lib/", (TimeSpan?)TimeSpan.FromMinutes(2)),
    Tuple.Create($"sudo cp {homeDirectory}/exhyperv_deploy/lib/nvidia-smi /usr/bin/nvidia-smi", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo chmod 755 /usr/bin/nvidia-smi", (TimeSpan?)TimeSpan.FromSeconds(10)),
    Tuple.Create("sudo ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so", (TimeSpan?)TimeSpan.FromSeconds(10)),
    Tuple.Create("sudo chmod -R 0555 /usr/lib/wsl", (TimeSpan?)TimeSpan.FromMinutes(1)),
    Tuple.Create("sudo chown -R root:root /usr/lib/wsl", (TimeSpan?)TimeSpan.FromMinutes(1)),

    // ===================================================================
    // 步骤 4: 配置系统环境 (最终修正版)
    // ===================================================================
    Tuple.Create("echo '[6/7] 正在配置链接库和全局环境变量...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    
    // 4.1. 配置链接库路径
    Tuple.Create("sudo sh -c 'echo \"/usr/lib/wsl/lib\" > /etc/ld.so.conf.d/ld.wsl.conf'", (TimeSpan?)TimeSpan.FromSeconds(10)),
    Tuple.Create("sudo ldconfig", (TimeSpan?)TimeSpan.FromMinutes(1)),

    // 4.2. 【核心修复】使用 /etc/environment 设置全局环境变量
    Tuple.Create("echo '[+] 正在设置全局环境变量以强制启用 D3D12 驱动...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo rm -f /etc/profile.d/99-dxgkrnl.sh", (TimeSpan?)TimeSpan.FromSeconds(10)), // 清理旧配置文件
    // 创建一个包含所有必需变量的临时文件
    Tuple.Create($"echo 'GALLIUM_DRIVERS=d3d12' > {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
    Tuple.Create($"echo 'LIBVA_DRIVER_NAME=d3d12' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
    Tuple.Create($"echo 'MESA_LOADER_DRIVER_OVERRIDE=vgem' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create($"echo 'GST_VAAPI_DRM_DEVICE=/dev/dri/card0' >> {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),

    // 安全地将新变量合并到 /etc/environment，并处理已存在PATH的情况
    Tuple.Create($"sudo sh -c 'grep -q -F \"PATH=\" /etc/environment || echo \"PATH=\\\"/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/games:/usr/local/games:/snap/bin\\\"\" >> /etc/environment'", (TimeSpan?)TimeSpan.FromSeconds(20)),
    Tuple.Create($"sudo sh -c 'cat {homeDirectory}/env_tmp /etc/environment | sort | uniq > /etc/environment.new && mv /etc/environment.new /etc/environment'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create($"rm {homeDirectory}/env_tmp", (TimeSpan?)TimeSpan.FromSeconds(10)),

// ===================================================================
// 步骤 4.3: 配置用户权限
// ===================================================================
Tuple.Create("echo '[+] 正在将用户添加到 video 和 render 组以获取硬件访问权限...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
Tuple.Create("sudo usermod -a -G video $USER || sudo usermod -a -G video $SUDO_USER || true", (TimeSpan?)TimeSpan.FromSeconds(30)),
Tuple.Create("sudo usermod -a -G render $USER || sudo usermod -a -G render $SUDO_USER || true", (TimeSpan?)TimeSpan.FromSeconds(30)),

// ===================================================================
// 步骤 4.4: 确保核心内核模块开机自启并立即加载
// ===================================================================
Tuple.Create("echo '[+] 正在确保 vgem 和 dxgkrnl 模块开机自启并立即加载...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
// 4.4.1. 设置开机自动加载 (永久生效)
Tuple.Create("sudo sh -c \"echo 'vgem' > /etc/modules-load.d/vgem.conf\"", (TimeSpan?)TimeSpan.FromSeconds(20)),
Tuple.Create("sudo sh -c \"echo 'dxgkrnl' > /etc/modules-load.d/dxgkrnl.conf\"", (TimeSpan?)TimeSpan.FromSeconds(20)),
// 4.4.2. 立即加载模块以供当前会话使用 (即时生效)
Tuple.Create("sudo modprobe vgem", (TimeSpan?)TimeSpan.FromSeconds(20)),
Tuple.Create("sudo modprobe dxgkrnl", (TimeSpan?)TimeSpan.FromSeconds(20)),

// ===================================================================
// 步骤 4.5: 开放设备节点权限
// ===================================================================
Tuple.Create("echo '[+] 正在开放设备节点权限...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
// 注意：现在 modprobe 已经执行，这些设备应该都存在了
Tuple.Create("sudo chmod 666 /dev/dxg", (TimeSpan?)TimeSpan.FromSeconds(10)),
Tuple.Create("sudo chmod 666 /dev/dri/card0 || true", (TimeSpan?)TimeSpan.FromSeconds(10)), // card0 可能不存在于无头环境，所以 || true
Tuple.Create("sudo chmod 666 /dev/dri/renderD128 || true", (TimeSpan?)TimeSpan.FromSeconds(10)), // 同上

    // ===================================================================
    // 步骤 5: 清理临时文件
    // ===================================================================
    Tuple.Create("echo '[7/7] 正在清理临时文件...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create($"rm -rf {homeDirectory}/exhyperv_deploy", (TimeSpan?)TimeSpan.FromMinutes(1)),
};


                            const int maxRetries = 2; // 离线操作，减少重试次数
                            foreach (var cmdInfo in commandsToExecute)
                            {
                                var command = cmdInfo.Item1;
                                var timeout = cmdInfo.Item2;

                                for (int retry = 1; retry <= maxRetries; retry++)
                                {
                                    try
                                    {
                                        await sshService.ExecuteSingleCommandAsync(credentials, command, log, timeout);
                                        break;
                                    }
                                    catch (Exception ex) when (ex is Renci.SshNet.Common.SshOperationTimeoutException || ex.InnerException is TimeoutException)
                                    {
                                        log($"--- 命令执行超时 (尝试 {retry}/{maxRetries}) ---");
                                        if (retry == maxRetries)
                                        {
                                            throw new Exception("命令执行超时，部署中止。", ex);
                                        }
                                        await Task.Delay(2000);
                                    }
                                }
                            }

                            // --- 步骤 5: 单独处理重启 ---
                            updateStatus("部署完成！虚拟机即将重启...");
                            log("\n[+] 部署完成！虚拟机即将重启...");
                            await sshService.ExecuteCommandAsyncFireAndForget(credentials, $"echo '{credentials.Password}' | sudo -S reboot");

                            progressWindow.EnableCloseButton();

                            return "OK";
                        }
                        catch (Exception ex)
                        {
                            // ... catch 块保持不变
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
        /// 从项目的 Assets/linuxlib 文件夹中读取核心 .so 文件并上传到虚拟机的指定目录。
        /// </summary>
        /// <summary>
        /// 从项目的 Assets/linuxlib 文件夹中读取本地文件 (.so 和 install.sh) 并上传。
        /// </summary>
        /// <summary>
        /// 将主机系统的WSL GPU库和自定义安装脚本上传到虚拟机。
        /// </summary>
        private async Task UploadLocalFilesAsync(SshService sshService, SshCredentials credentials, string remoteDirectory)
        {
            // 步骤 1: 从主机系统路径获取并上传 GPU 相关的 .so 库文件
            // 这是更健壮的方式，确保与主机驱动版本一致
            string systemWslLibPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "lxss", "lib");

            if (!Directory.Exists(systemWslLibPath))
            {
                throw new DirectoryNotFoundException($"无法在主机上找到 WSL GPU 库文件夹: '{systemWslLibPath}'。请确认 NVIDIA/Intel/AMD 驱动已正确安装并支持 WSLg。");
            }

            // 使用 SshService 的目录上传功能，将整个 lib 目录内容上传到目标 remoteDirectory
            // 注意：这里我们是上传目录的 *内容*，而不是目录本身。
            // 假设 sshService.UploadDirectoryAsync 能够处理这个问题，如果不行，我们需要遍历文件。
            // 我们先假设可以直接上传文件夹。
            var filesInSystemDir = Directory.GetFiles(systemWslLibPath);
            foreach (var filePath in filesInSystemDir)
            {
                string fileName = Path.GetFileName(filePath);
                await sshService.UploadFileAsync(credentials, filePath, $"{remoteDirectory}/{fileName}");
            }

            // 步骤 2: 从项目内嵌资源上传自定义的 install.sh 脚本
            // 这个脚本是你自己编写的，用于编译 dxgkrnl 内核模块，所以它必须随程序分发
            string baseDirectory = AppContext.BaseDirectory;
            string localAssetDirectory = Path.Combine(baseDirectory, "Assets", "linuxlib");
            string installScriptPath = Path.Combine(localAssetDirectory, "install.sh");

            if (!File.Exists(installScriptPath))
            {
                throw new FileNotFoundException($"无法在资源文件夹中找到安装脚本: {installScriptPath}。请确保 install.sh 文件已设置为“如果较新则复制”并随程序一起发布。");
            }

            await sshService.UploadFileAsync(credentials, installScriptPath, $"{remoteDirectory}/install.sh");

            // （可选）如果还有其他必须随包分发的 .so 文件 (比如微软的 core D3D 库，以防万一系统里没有)
            // 你也可以在这里添加一个检查，如果 lxss/lib 中不存在，就从 Assets 中上传作为后备。
            // 但根据 WSLg 的设计，这些文件应该存在。
            string[] coreDxFiles = { "libd3d12.so", "libd3d12core.so", "libdxcore.so" };
            foreach (var file in coreDxFiles)
            {
                string systemFilePath = Path.Combine(systemWslLibPath, file);
                if (!File.Exists(systemFilePath))
                {
                    // 如果系统目录里没有，就从我们的资源包里上传
                    string localFilePath = Path.Combine(localAssetDirectory, file);
                    if (File.Exists(localFilePath))
                    {
                        await sshService.UploadFileAsync(credentials, localFilePath, $"{remoteDirectory}/{file}");
                    }
                }
            }
        }
    }
}