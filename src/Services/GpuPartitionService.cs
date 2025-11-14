using System.Diagnostics;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;
using System.Text.Json;
using ExHyperV.Services; // 引用 SshService
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
                        "所选分区似乎不是一个有效的Windows系统分区。",
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

                        // 将 progressWindow 的定义提到 try-catch 外部
                        ExecutionProgressWindow progressWindow = null;

                        // 自动启动虚拟机
                        var currentState = await GetVmStateAsync(vmName);
                        if (currentState != "Running")
                        {
                            ShowMessageOnUIThread("正在启动虚拟机，请稍候...", "提示");
                            Utils.Run($"Start-VM -Name '{vmName}'");
                            await Task.Delay(20000);
                        }

                        // 获取 SSH 凭据
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

                        // 在 GpuPartitionService.cs 的 else if (...) 块内

                        try
                        {
                            // --- 步骤 1: 创建并显示进度窗口 ---
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                progressWindow = new ExecutionProgressWindow();
                                progressWindow.Show();
                            });

                            Action<string> log = (message) => progressWindow.AppendLog(message);
                            Action<string> updateStatus = (status) => progressWindow.UpdateStatus(status);

                            // --- 步骤 2: 获取远程主目录并创建部署文件夹 ---
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
                                    throw new Exception("无法获取远程用户的主目录路径。");
                                }
                                log($"获取到远程主目录: {homeDirectory}");

                                string remoteTempDir = $"{homeDirectory}/exhyperv_deploy";
                                client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                                log($"已在远程创建部署目录: {remoteTempDir}");
                                client.Disconnect();
                            }


                            if (!string.IsNullOrEmpty(credentials.ProxyHost) && credentials.ProxyPort.HasValue)
                            {
                                updateStatus("正在自动为虚拟机配置 HTTP 代理...");
                                log($"检测到代理设置: {credentials.ProxyHost}:{credentials.ProxyPort}");

                                string proxyUrl = $"http://{credentials.ProxyHost}:{credentials.ProxyPort}";

                                // 1. 在 C# 中生成配置文件内容
                                string aptProxyContent = $"Acquire::http::Proxy \"{proxyUrl}\";\nAcquire::https::Proxy \"{proxyUrl}\";\n";
                                // **修正**: 确保每个环境变量都在新的一行
                                string envProxyContent = $"\nexport http_proxy=\"{proxyUrl}\"\nexport https_proxy=\"{proxyUrl}\"\nexport no_proxy=\"localhost,127.0.0.1\"\n";

                                // 2. 将内容上传到远程临时文件
                                string remoteAptProxyFile = $"{homeDirectory}/99proxy";
                                string remoteEnvProxyFile = $"{homeDirectory}/proxy_env";
                                await sshService.WriteTextFileAsync(credentials, aptProxyContent, remoteAptProxyFile);
                                await sshService.WriteTextFileAsync(credentials, envProxyContent, remoteEnvProxyFile);

                                // 3. 使用 sudo mv 和 sh -c 安全地应用配置
                                var proxyCommands = new List<string>
        {
            // 为 APT 配置代理 (覆盖)
            $"sudo mv {remoteAptProxyFile} /etc/apt/apt.conf.d/99proxy",
            
            // **关键修改**: 为系统环境变量配置代理 (追加)，彻底告别管道
            $"sudo sh -c 'cat {remoteEnvProxyFile} >> /etc/environment'",

            // 清理临时文件
            $"rm {remoteEnvProxyFile}",

            // 立即在当前会话中导出环境变量 (为了后续的 apt-get 和 curl 能用上)
            $"export http_proxy={proxyUrl}",
            $"export https_proxy={proxyUrl}"
        };

                                foreach (var cmd in proxyCommands)
                                {
                                    await sshService.ExecuteSingleCommandAsync(credentials, cmd, log, TimeSpan.FromSeconds(30));
                                }
                                log("代理配置成功。");
                            }

                            // --- 步骤 3: 上传所有本地文件 ---
                            string remoteDriversDir = $"{homeDirectory}/exhyperv_deploy/drivers";
                            string remoteLibDir = $"{homeDirectory}/exhyperv_deploy/lib";

                            updateStatus("正在上传驱动文件 (FileRepository)... 此过程非常耗时，请耐心等待。");
                            await sshService.UploadDirectoryAsync(credentials, @"C:\Windows\System32\DriverStore\FileRepository", remoteDriversDir);
                            log("驱动文件 (FileRepository) 上传完毕。");

                            updateStatus("正在上传核心库文件和安装脚本...");
                            // 调用新的 UploadLocalFilesAsync 方法，它会上传 .so 和 install.sh
                            await UploadLocalFilesAsync(sshService, credentials, remoteLibDir);
                            log("核心库文件和安装脚本上传完毕。");

                            // --- 步骤 4: 准备并分步执行全离线安装 ---
                            updateStatus("正在准备并执行远程安装... 请在下方查看实时进度。");

                            // GpuPartitionService.cs -> try...catch 块内

                            var commandsToExecute = new List<Tuple<string, TimeSpan?>>
{
    // ===================================================================
    // 步骤 A: 环境准备 - 添加 PPA 并全面升级 Mesa
    // ===================================================================
    Tuple.Create("echo '[1/7] 正在添加最新的 Mesa 稳定版驱动源 (PPA)...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    // '-y' 参数会自动确认添加 PPA，无需人工交互
    Tuple.Create("sudo add-apt-repository -y ppa:kisak/turtle", (TimeSpan?)TimeSpan.FromMinutes(2)),

    Tuple.Create("echo '[2/7] 正在更新软件包列表...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo apt-get update", (TimeSpan?)TimeSpan.FromMinutes(5)),

    Tuple.Create("echo '[3/7] 正在升级 Mesa 驱动及相关系统组件...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    // '-y' 参数会自动确认所有升级
    Tuple.Create("sudo apt-get upgrade -y", (TimeSpan?)TimeSpan.FromMinutes(15)),

    // ===================================================================
    // 步骤 B: 安装 dxgkrnl 编译所需的依赖
    // ===================================================================
    Tuple.Create("echo '[4/7] 正在安装 GPU 部署所需的依赖包 (dkms, git...)'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    // 一次性安装所有我们需要的工具
    Tuple.Create("sudo apt-get install -y linux-headers-$(uname -r) build-essential git dkms vainfo", (TimeSpan?)TimeSpan.FromMinutes(10)),

    // ===================================================================
    // 步骤 C: 从本地脚本编译并安装 dxgkrnl 内核模块
    // ===================================================================
    Tuple.Create("echo '[5/7] 正在从本地脚本编译并安装 dxgkrnl 内核模块...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    // 直接执行我们上传的 install.sh 脚本，这步耗时较长
    Tuple.Create($"sudo bash {remoteLibDir}/install.sh", (TimeSpan?)null), // null 表示使用默认的30分钟长超时

    // ===================================================================
    // 步骤 D: 部署 Windows 驱动文件并设置权限
    // ===================================================================
    Tuple.Create("echo '[6/7] 正在部署 Windows 宿主机驱动文件...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo rm -rf /usr/lib/wsl", (TimeSpan?)TimeSpan.FromMinutes(1)),
    Tuple.Create("sudo mkdir -p /usr/lib/wsl", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create($"sudo mv {homeDirectory}/exhyperv_deploy/drivers /usr/lib/wsl/", (TimeSpan?)TimeSpan.FromMinutes(2)),
    Tuple.Create($"sudo mv {homeDirectory}/exhyperv_deploy/lib /usr/lib/wsl/", (TimeSpan?)TimeSpan.FromMinutes(1)),
    Tuple.Create("sudo chmod -R 555 /usr/lib/wsl/drivers/", (TimeSpan?)TimeSpan.FromMinutes(1)),
    Tuple.Create("sudo chmod -R 755 /usr/lib/wsl/lib/", (TimeSpan?)TimeSpan.FromMinutes(1)),
    Tuple.Create("sudo chown -R root:root /usr/lib/wsl", (TimeSpan?)TimeSpan.FromMinutes(1)),

    // ===================================================================
    // 步骤 E: 配置系统环境（符号链接、库缓存）
    // ===================================================================
    Tuple.Create("echo '[7/7] 正在配置系统环境...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo ln -sf /usr/lib/wsl/lib/libd3d12core.so /usr/lib/wsl/lib/libD3D12Core.so", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo ln -sf /usr/lib/wsl/lib/libnvoptix.so.1 /usr/lib/wsl/lib/libnvoptix_loader.so.1", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo ln -sf /usr/lib/wsl/lib/libcuda.so /usr/lib/wsl/lib/libcuda.so.1", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo sh -c 'echo \"/usr/lib/wsl/lib\" > /etc/ld.so.conf.d/ld.wsl.conf'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create("sudo ldconfig", (TimeSpan?)TimeSpan.FromMinutes(1)),

    // ===================================================================
    // 步骤 F: 清理临时文件
    // ===================================================================
    Tuple.Create("echo '[+] 正在清理临时文件...'", (TimeSpan?)TimeSpan.FromSeconds(30)),
    Tuple.Create($"rm -rf {homeDirectory}/exhyperv_deploy", (TimeSpan?)TimeSpan.FromMinutes(1)),
};

                            // ... 后续的 foreach 循环和重启逻辑保持不变 ...
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
        private async Task UploadLocalFilesAsync(SshService sshService, SshCredentials credentials, string remoteDirectory)
        {
            string baseDirectory = AppContext.BaseDirectory;
            string localLibDirectory = Path.Combine(baseDirectory, "Assets", "linuxlib");

            if (!Directory.Exists(localLibDirectory))
            {
                throw new DirectoryNotFoundException($"无法找到本地资源文件夹: {localLibDirectory}。请确保 Assets/linuxlib 文件夹和其中的文件已设置为“如果较新则复制”并随程序一起发布。");
            }

            // **修改**: 增加 install.sh 到文件列表
            string[] fileNames = { "libd3d12.so", "libd3d12core.so", "libdxcore.so", "install.sh" };

            foreach (var name in fileNames)
            {
                string localFilePath = Path.Combine(localLibDirectory, name);
                if (!File.Exists(localFilePath))
                {
                    throw new FileNotFoundException($"无法在资源文件夹中找到文件: {localFilePath}。");
                }
                await sshService.UploadFileAsync(credentials, localFilePath, $"{remoteDirectory}/{name}");
            }
        }

        /// <summary>
        /// 构建将在 Linux 虚拟机上执行的完整安装脚本。
        /// </summary>
        /// <summary>
        /// 构建将在 Linux 虚拟机上执行的完整安装脚本。
        /// </summary>
        /// <param name="tempDir">远程主机上的临时部署目录路径 (例如: ~/exhyperv_deploy)</param>
        private string BuildRemoteInstallScript(string tempDir)
        {
            var scriptBuilder = new System.Text.StringBuilder();
            scriptBuilder.AppendLine("#!/bin/bash -e");
            scriptBuilder.AppendLine("# 由 ExHyperV 自动生成的安装脚本");
            scriptBuilder.AppendLine("echo '--- 开始自动化 GPU 驱动安装 ---'");

            scriptBuilder.AppendLine("echo '[1/5] 正在从网络安装依赖包和 dxgkrnl 内核模块...'");
            scriptBuilder.AppendLine("apt-get update && apt-get install -y curl linux-headers-$(uname -r) build-essential git dkms");
            scriptBuilder.AppendLine("curl -fsSL https://content.staralt.dev/dxgkrnl-dkms/main/install.sh | bash -es");

            scriptBuilder.AppendLine("echo '[2/5] 正在部署宿主机驱动文件和库...'");
            scriptBuilder.AppendLine("rm -rf /usr/lib/wsl");
            scriptBuilder.AppendLine("mkdir -p /usr/lib/wsl");
            // **修改**: 从新的、基于参数的临时路径移动文件
            scriptBuilder.AppendLine($"mv {tempDir}/drivers /usr/lib/wsl/");
            scriptBuilder.AppendLine($"mv {tempDir}/lib /usr/lib/wsl/");

            scriptBuilder.AppendLine("echo '[3/5] 正在设置文件权限...'");
            scriptBuilder.AppendLine("chmod -R 555 /usr/lib/wsl/drivers/");
            scriptBuilder.AppendLine("chmod -R 755 /usr/lib/wsl/lib/");
            scriptBuilder.AppendLine("chown -R root:root /usr/lib/wsl");

            scriptBuilder.AppendLine("echo '[4/5] 正在配置系统环境 (创建符号链接)...'");
            scriptBuilder.AppendLine("ln -sf /usr/lib/wsl/lib/libd3_2core.so /usr/lib/wsl/lib/libD3D12Core.so || true");
            scriptBuilder.AppendLine("ln -sf /usr/lib/wsl/lib/libnvoptix.so.1 /usr/lib/wsl/lib/libnvoptix_loader.so.1 || true");
            scriptBuilder.AppendLine("ln -sf /usr/lib/wsl/lib/libcuda.so /usr/lib/wsl/lib/libcuda.so.1 || true");
            scriptBuilder.AppendLine("sh -c 'echo \"/usr/lib/wsl/lib\" > /etc/ld.so.conf.d/ld.wsl.conf'");
            scriptBuilder.AppendLine("ldconfig");

            scriptBuilder.AppendLine("echo '[5/5] 清理临时文件并准备重启系统...'");
            // **修改**: 删除新的、基于参数的临时路径
            scriptBuilder.AppendLine($"rm -rf {tempDir}");
            scriptBuilder.AppendLine("reboot");

            return scriptBuilder.ToString();
        }
    }
}