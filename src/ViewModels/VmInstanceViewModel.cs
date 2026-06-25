using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.ViewModels
{
    /// <summary>
    ///
    /// 设计：
    /// - 包装 <see cref="VmInstance"/> Model：构造时绑定，**生命期内 Model 引用不变**
    /// - **集合 pass-through**：Disks/StorageItems/NetworkAdapters/BootOrderItems/AssignedGpus
    ///   getter 直接返回 Model 的 ObservableCollection——Service mutate 后 UI 自动刷新（同一实例）
    /// - **标量字段 [ObservableProperty] + OnXxxChanged 反写 Model**：保证 PageVM 改 vm.Name 时
    ///   Model.Name 同步更新；Service 读 Model.Name 永远不 stale
    /// - <see cref="Apply(VmInstance)"/>：把 fresh model 数据合入当前 Model（in-place 集合 reconcile）
    /// - VM-only 状态（IsEditing、Thumbnail、PointCollection 历史、Cores、命令、transient 状态机）
    ///   不与 Model 共享
    /// - public surface 与旧 VmInstanceInfo 完全一致，保 XAML binding 零改动
    /// </summary>
    public partial class VmInstanceViewModel : ObservableObject
    {
        // ===== Model 引用（构造时绑定，永不替换；Service 通过它访问底层数据） =====

        public VmInstance Model { get; }


        // ===== 编辑名称（view-only） =====

        [ObservableProperty] private bool _isEditing;
        [ObservableProperty] private string _editedName = string.Empty;

        public void StartEditing()
        {
            EditedName = Name;
            IsEditing = true;
        }


        // ===== 基础字段 — [ObservableProperty] + OnXxxChanged 反写 Model =====

        [ObservableProperty] private Guid _id;
        partial void OnIdChanged(Guid value) { if (Model != null) Model.Id = value; }

        [ObservableProperty] private string _name = string.Empty;
        partial void OnNameChanged(string value) { if (Model != null) Model.Name = value; }

        [ObservableProperty] private string _notes = string.Empty;
        partial void OnNotesChanged(string value) { if (Model != null) Model.Notes = value; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))]
        [NotifyPropertyChangedFor(nameof(CanChangeBootOrder))]
        [NotifyPropertyChangedFor(nameof(CanToggleConsoleSupport))]
        private int _generation;
        partial void OnGenerationChanged(int value) { if (Model != null) Model.Generation = value; }

        [ObservableProperty] private string _version = "0.0";
        partial void OnVersionChanged(string value) { if (Model != null) Model.Version = value; }

        [ObservableProperty] private string _osType = "Windows";
        partial void OnOsTypeChanged(string value) { if (Model != null) Model.OsType = value; }

        [ObservableProperty] private string _state = string.Empty;
        [ObservableProperty] private string _uptime = "00:00:00";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanChangeBootOrder))]
        [NotifyPropertyChangedFor(nameof(CanToggleConsoleSupport))]
        [NotifyPropertyChangedFor(nameof(CanEditSecurity))]
        private bool _isRunning;

        public bool CanChangeBootOrder => !(Generation == 1 && IsRunning);

        // 控制台支持开关仅适用于第 2 代虚拟机（Enable/Disable-VMConsoleSupport 官方仅 Gen2 可用），且需关机时改
        public bool CanToggleConsoleSupport => Generation == 2 && !IsRunning;

        // 安全启动 / vTPM 仅第 2 代可用，且需关机时改（与创建页的安全特性一致）
        public bool CanEditSecurity => Generation == 2 && !IsRunning;

        [ObservableProperty] private BitmapSource? _thumbnail;

        // ─── IP / MAC ─────────────────────────────────────────────
        [ObservableProperty] private string _ipAddress = "---";
        [ObservableProperty] private string _ipAddressDisplay = "---";

        partial void OnIpAddressChanged(string value)
        {
            if (Model != null) Model.IpAddress = value;
            RecomputeIpAddressDisplay(value);
        }

        [ObservableProperty] private string _macAddress = "00:00:00:00:00:00";
        partial void OnMacAddressChanged(string value) { if (Model != null) Model.MacAddress = value; }

        // ─── transient 状态机的内部锚点 ───────────────────────────
        private TimeSpan _anchorUptime;
        private DateTime _anchorLocalTime;
        private string? _transientState;
        private string? _backendState;


        // ===== CPU / 内存 =====

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))]
        private int _cpuCount;
        partial void OnCpuCountChanged(int value) { if (Model != null) Model.CpuCount = value; }

        [ObservableProperty] private double _averageUsage;
        [ObservableProperty] private int _columns = 2;
        [ObservableProperty] private int _rows = 1;
        public ObservableCollection<VmCoreItem> Cores { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))]
        private double _memoryGb;
        partial void OnMemoryGbChanged(double value) { if (Model != null) Model.MemoryGb = value; }

        [ObservableProperty] private VmProcessorSettings? _processor;
        partial void OnProcessorChanged(VmProcessorSettings? value) { if (Model != null) Model.Processor = value; }

        [ObservableProperty] private VmMemorySettings? _memorySettings;
        partial void OnMemorySettingsChanged(VmMemorySettings? value) { if (Model != null) Model.MemorySettings = value; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryUsageString))]
        [NotifyPropertyChangedFor(nameof(MemoryLimitString))]
        private double _assignedMemoryGb;
        partial void OnAssignedMemoryGbChanged(double value) { if (Model != null) Model.AssignedMemoryGb = value; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryDemandString))]
        private double _demandMemoryGb;
        partial void OnDemandMemoryGbChanged(double value) { if (Model != null) Model.DemandMemoryGb = value; }

        [ObservableProperty] private PointCollection? _memoryHistoryPoints;

        private readonly LinkedList<double> _memoryUsageHistory = new();
        private const int MaxHistoryLength = 60;


        // ===== 存储 / 网络 / 启动顺序 / GPU 分配 — pass-through 到 Model 的 ObservableCollection =====

        public ObservableCollection<VmDiskItem> Disks => Model.Disks;
        public ObservableCollection<VmStorageItem> StorageItems => Model.StorageItems;
        public ObservableCollection<VmNetworkAdapter> NetworkAdapters => Model.NetworkAdapters;
        public ObservableCollection<BootOrderItem> BootOrderItems => Model.BootOrderItems;
        public ObservableCollection<VmGpuAssignment> AssignedGpus => Model.AssignedGpus;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))]
        private double _totalDiskSizeGb;
        partial void OnTotalDiskSizeGbChanged(double value) { if (Model != null) Model.TotalDiskSizeGb = value; }


        // ===== GPU =====

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasGpu))]
        [NotifyPropertyChangedFor(nameof(GpuDisplayLabel))]
        private string? _gpuName;
        partial void OnGpuNameChanged(string? value)
        {
            if (Model != null) Model.GpuName = value;
            OnPropertyChanged(nameof(HasGpu));
        }

        public string GpuDisplayLabel
        {
            get
            {
                if (AssignedGpus != null && AssignedGpus.Count > 0)
                {
                    var groups = AssignedGpus.GroupBy(g => g.Name).ToList();
                    var mainGroup = groups[0];
                    string mainName = mainGroup.Key;
                    int mainCount = mainGroup.Count();

                    string result = mainName;
                    if (mainCount > 1) result += $" *{mainCount}";
                    if (groups.Count > 1) result += " +";
                    return result;
                }

                if (!string.IsNullOrEmpty(GpuName))
                    return GpuName;

                return Properties.Resources.Common_None;
            }
        }

        public void RefreshGpuSummary() => OnPropertyChanged(nameof(GpuDisplayLabel));

        public bool HasGpu => (AssignedGpus != null && AssignedGpus.Count > 0) || !string.IsNullOrEmpty(GpuName);

        // GPU 实时监控
        [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuMaxUsage))] private double _gpu3dUsage;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuMaxUsage))] private double _gpuCopyUsage;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuMaxUsage))] private double _gpuEncodeUsage;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuMaxUsage))] private double _gpuDecodeUsage;

        public double GpuMaxUsage => Math.Max(Math.Max(Gpu3dUsage, GpuCopyUsage), Math.Max(GpuEncodeUsage, GpuDecodeUsage));

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasGpu))]
        private bool _isGpuActive;
        partial void OnIsGpuActiveChanged(bool value) { if (Model != null) Model.IsGpuActive = value; }

        [ObservableProperty] private PointCollection? _gpu3dHistoryPoints;
        [ObservableProperty] private PointCollection? _gpuCopyHistoryPoints;
        [ObservableProperty] private PointCollection? _gpuEncodeHistoryPoints;
        [ObservableProperty] private PointCollection? _gpuDecodeHistoryPoints;

        private readonly LinkedList<double> _gpu3dHistory = new();
        private readonly LinkedList<double> _gpuCopyHistory = new();
        private readonly LinkedList<double> _gpuEncodeHistory = new();
        private readonly LinkedList<double> _gpuDecodeHistory = new();


        // ===== 命令（由 PageVM 创建并赋值——需要 powerService 等依赖） =====

        public IAsyncRelayCommand<string>? ControlCommand { get; set; }


        // ===== 构造：从 Model 初始化 VM（Model 引用保留，集合通过 pass-through 共享） =====

        public VmInstanceViewModel(VmInstance model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));

            // ── 标量字段：从 Model 拷入 VM 的 [ObservableProperty] backing field
            //    （直接赋字段、不走 setter，避免构造期触发 OnXxxChanged 反写 Model 同一个值）
            _id = model.Id;
            _name = model.Name;
            _notes = model.Notes;
            _generation = model.Generation;
            _version = model.Version;
            _osType = model.OsType;
            CpuCount = model.CpuCount;
            MemoryGb = model.MemoryGb;
            AssignedMemoryGb = model.AssignedMemoryGb;
            DemandMemoryGb = model.DemandMemoryGb;
            _totalDiskSizeGb = model.TotalDiskSizeGb;
            _ipAddress = model.IpAddress;
            _macAddress = model.MacAddress;
            _gpuName = model.GpuName;
            _isGpuActive = model.IsGpuActive;
            _processor = model.Processor;
            MemorySettings = model.MemorySettings;

            // ── IpAddressDisplay 派生（构造时跳过了 setter 钩子的 display 计算）──
            RecomputeIpAddressDisplay(_ipAddress);

            // ── GPU 历史队列初始化 + transient 状态推进 ──────────
            InitializeGpuHistory();
            SyncBackendData(model.StateText, model.RawUptime);

            // ── Disks 集合变化时刷新总磁盘大小与 ConfigSummary ──
            Disks.CollectionChanged += (s, e) =>
            {
                TotalDiskSizeGb = Disks.Sum(d => d.MaxSize) / 1073741824.0;
                OnPropertyChanged(nameof(ConfigSummary));
            };
            // 集合变化也要刷 HasGpu/GpuDisplayLabel
            AssignedGpus.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasGpu));
                OnPropertyChanged(nameof(GpuDisplayLabel));
            };
        }

        private void RecomputeIpAddressDisplay(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "---")
            {
                IpAddressDisplay = value;
                return;
            }
            var ips = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var ipv4 = ips.FirstOrDefault(ip => ip.Contains(".") && !ip.Contains(":"));
            IpAddressDisplay = !string.IsNullOrWhiteSpace(ipv4)
                ? ipv4.Trim()
                : (ips.FirstOrDefault()?.Trim() ?? value);
        }

        private void InitializeGpuHistory()
        {
            _gpu3dHistory.Clear(); _gpuCopyHistory.Clear();
            _gpuEncodeHistory.Clear(); _gpuDecodeHistory.Clear();
            for (int i = 0; i < MaxHistoryLength; i++)
            {
                _gpu3dHistory.AddLast(0.0);
                _gpuCopyHistory.AddLast(0.0);
                _gpuEncodeHistory.AddLast(0.0);
                _gpuDecodeHistory.AddLast(0.0);
            }
            RefreshGpuPoints();
        }


        // ===== Apply：把 fresh Model 数据合入**当前 Model**（in-place） =====
        //
        // 调用方：PageVM.MonitorStateLoop 周期刷新 / SyncSingleVmStateAsync 单 VM 刷新
        // skipNameUpdate=true：PageVM 的 rename lockout 期内跳过 Name 同步
        // skipNetworkAdapters=true：用户在 NetworkSettings 页或 IsLoadingSettings 时跳过适配器抖动

        public void Apply(VmInstance fresh, bool skipNameUpdate = false, bool skipNetworkAdapters = false)
        {
            if (fresh == null) return;

            // ── 1. 标量字段（按需更新避免冗余 PropertyChanged；setter 会反写 Model）
            if (!skipNameUpdate && Name != fresh.Name) Name = fresh.Name;
            if (CpuCount != fresh.CpuCount) CpuCount = fresh.CpuCount;
            if (MemoryGb != fresh.MemoryGb) MemoryGb = fresh.MemoryGb;
            if (Generation != fresh.Generation) Generation = fresh.Generation;
            if (Version != fresh.Version) Version = fresh.Version;
            Notes = fresh.Notes;

            // ── 2. 推进 transient 状态机 ──────────────────────────
            SyncBackendData(fresh.StateText, fresh.RawUptime);

            // ── 3. IP：未运行清回 "---"（运行时的 ARP 发现侧路在 PageVM）──
            if (!IsRunning) IpAddress = "---";

            // ── 4. 网络适配器 in-place 合并（按 Id 匹配；MAC/IP/VLAN/带宽全部刷新）──
            //      当 UI 正在 NetworkSettings 页面绑定编辑时跳过，避免抖动
            if (!skipNetworkAdapters)
                ReconcileNetworkAdapters(NetworkAdapters, fresh.NetworkAdapters);

            // ── 5. 磁盘 in-place 合并（按 Path 匹配；运行中实时读真实文件大小）──
            ReconcileDisks(Disks, fresh.Disks, runningRefresh: IsRunning);

            // ── 6. GPU 摘要名 ─────────────────────────────────────
            GpuName = fresh.GpuName;
        }

        // ─── 网络适配器合并（lifted from VirtualMachinesPageViewModel.SyncNetworkAdaptersInternal）──
        private static void ReconcileNetworkAdapters(
            ObservableCollection<VmNetworkAdapter> currentList,
            IEnumerable<VmNetworkAdapter> newList)
        {
            if (newList == null) return;
            var newSnapshot = newList.ToList();

            var toRemove = currentList.Where(c => !newSnapshot.Any(n => n.Id == c.Id)).ToList();
            foreach (var item in toRemove) currentList.Remove(item);

            foreach (var newItem in newSnapshot)
            {
                var existingItem = currentList.FirstOrDefault(c => c.Id == newItem.Id);
                if (existingItem != null)
                {
                    existingItem.Name = newItem.Name;
                    existingItem.IsConnected = newItem.IsConnected;
                    existingItem.SwitchName = newItem.SwitchName;
                    existingItem.MacAddress = newItem.MacAddress;
                    existingItem.IsStaticMac = newItem.IsStaticMac;
                    if (newItem.IpAddresses != null && newItem.IpAddresses.Count > 0)
                        existingItem.IpAddresses = newItem.IpAddresses;
                    existingItem.VlanMode = newItem.VlanMode;
                    existingItem.AccessVlanId = newItem.AccessVlanId;
                    existingItem.NativeVlanId = newItem.NativeVlanId;
                    existingItem.TrunkAllowedVlanIds = newItem.TrunkAllowedVlanIds;
                    existingItem.PvlanMode = newItem.PvlanMode;
                    existingItem.PvlanPrimaryId = newItem.PvlanPrimaryId;
                    existingItem.PvlanSecondaryId = newItem.PvlanSecondaryId;
                    existingItem.BandwidthLimit = newItem.BandwidthLimit;
                    existingItem.BandwidthReservation = newItem.BandwidthReservation;
                    existingItem.MacSpoofingAllowed = newItem.MacSpoofingAllowed;
                    existingItem.DhcpGuardEnabled = newItem.DhcpGuardEnabled;
                    existingItem.RouterGuardEnabled = newItem.RouterGuardEnabled;
                    existingItem.MonitorMode = newItem.MonitorMode;
                    existingItem.StormLimit = newItem.StormLimit;
                    existingItem.TeamingAllowed = newItem.TeamingAllowed;
                    existingItem.VmqEnabled = newItem.VmqEnabled;
                    existingItem.SriovEnabled = newItem.SriovEnabled;
                    existingItem.IpsecOffloadEnabled = newItem.IpsecOffloadEnabled;
                }
                else
                {
                    currentList.Add(newItem);
                }
            }
        }

        // ─── 磁盘合并（lifted from VirtualMachinesPageViewModel.MonitorStateLoop）──
        private static void ReconcileDisks(
            ObservableCollection<VmDiskItem> currentList,
            IEnumerable<VmDiskItem> newList,
            bool runningRefresh)
        {
            if (newList == null) return;
            var newSnapshot = newList.ToList();

            var updatePaths = newSnapshot.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int i = currentList.Count - 1; i >= 0; i--)
            {
                if (!updatePaths.Contains(currentList[i].Path))
                    currentList.RemoveAt(i);
            }

            foreach (var newDiskData in newSnapshot)
            {
                var existingDisk = currentList.FirstOrDefault(d => d.Path.Equals(newDiskData.Path, StringComparison.OrdinalIgnoreCase));
                if (existingDisk != null)
                {
                    existingDisk.Name = newDiskData.Name;
                    existingDisk.MaxSize = newDiskData.MaxSize;
                    existingDisk.DiskType = newDiskData.DiskType;

                    if (runningRefresh && existingDisk.DiskType != "Physical" && System.IO.File.Exists(existingDisk.Path))
                    {
                        try
                        {
                            long realSizeBytes = new System.IO.FileInfo(existingDisk.Path).Length;
                            if (existingDisk.CurrentSize != realSizeBytes)
                                existingDisk.CurrentSize = realSizeBytes;
                        }
                        catch { }
                    }
                    else
                    {
                        existingDisk.CurrentSize = newDiskData.CurrentSize;
                    }
                }
                else
                {
                    currentList.Add(newDiskData);
                }
            }
        }


        // ===== 业务逻辑方法（内存、GPU、状态推进、计时） =====

        public string MemoryUsageString => AssignedMemoryGb.ToString("N1");
        public string MemoryDemandString => DemandMemoryGb.ToString("N1");

        public string MemoryLimitString
        {
            get
            {
                if (MemorySettings != null)
                {
                    double limitMb = MemorySettings.DynamicMemoryEnabled ? MemorySettings.Maximum : MemorySettings.Startup;
                    return (limitMb / 1024.0).ToString("N1");
                }
                return MemoryGb > 0 ? MemoryGb.ToString("N1") : "0.0";
            }
        }

        public void UpdateMemoryStatus(long assignedMb, int availablePercent)
        {
            if (!IsRunning || assignedMb == 0)
            {
                AssignedMemoryGb = 0; DemandMemoryGb = 0; UpdateHistoryPoints(0); return;
            }
            AssignedMemoryGb = assignedMb / 1024.0;
            double usedPercentage = (100 - availablePercent) / 100.0;
            DemandMemoryGb = AssignedMemoryGb * usedPercentage;
            UpdateHistoryPoints(100 - availablePercent);
        }

        private void UpdateHistoryPoints(double pressurePercent)
        {
            pressurePercent = Math.Max(0, Math.Min(100, pressurePercent));
            _memoryUsageHistory.AddLast(pressurePercent);
            if (_memoryUsageHistory.Count > MaxHistoryLength) _memoryUsageHistory.RemoveFirst();
            var points = new PointCollection();
            int count = _memoryUsageHistory.Count;
            int offset = MaxHistoryLength - count;
            points.Add(new Point(offset, 100));

            int i = 0;
            foreach (var val in _memoryUsageHistory)
            {
                points.Add(new Point(offset + i, 100 - val));
                i++;
            }
            points.Add(new Point(MaxHistoryLength - 1, 100));
            points.Freeze();
            MemoryHistoryPoints = points;
        }

        public void UpdateGpuStats(VmQueryService.GpuUsageData data)
        {
            if (!IsRunning)
            {
                Gpu3dUsage = 0;
                GpuCopyUsage = 0;
                GpuEncodeUsage = 0;
                GpuDecodeUsage = 0;
                IsGpuActive = false;
            }
            else
            {
                Gpu3dUsage = Math.Clamp(data.Gpu3d, 0, 100);
                GpuCopyUsage = Math.Clamp(data.GpuCopy, 0, 100);
                GpuEncodeUsage = Math.Clamp(data.GpuEncode, 0, 100);
                GpuDecodeUsage = Math.Clamp(data.GpuDecode, 0, 100);

                bool hasEngineUsage = Gpu3dUsage > 0 || GpuCopyUsage > 0 || GpuEncodeUsage > 0 || GpuDecodeUsage > 0;
                bool isLinuxGuest = !string.IsNullOrWhiteSpace(OsType) && OsType.Contains("linux", StringComparison.OrdinalIgnoreCase);

                IsGpuActive = data.IsDriverBound || hasEngineUsage || (HasGpu && isLinuxGuest);
            }
            UpdateSingleGpuHistory(_gpu3dHistory, Gpu3dUsage);
            UpdateSingleGpuHistory(_gpuCopyHistory, GpuCopyUsage);
            UpdateSingleGpuHistory(_gpuEncodeHistory, GpuEncodeUsage);
            UpdateSingleGpuHistory(_gpuDecodeHistory, GpuDecodeUsage);

            RefreshGpuPoints();
            OnPropertyChanged(nameof(GpuMaxUsage));
        }

        private void UpdateSingleGpuHistory(LinkedList<double> history, double value)
        {
            history.AddLast(Math.Max(0, Math.Min(100, value)));
            if (history.Count > MaxHistoryLength) history.RemoveFirst();
        }

        private void RefreshGpuPoints()
        {
            Gpu3dHistoryPoints = CalculateGpuPoints(_gpu3dHistory);
            GpuCopyHistoryPoints = CalculateGpuPoints(_gpuCopyHistory);
            GpuEncodeHistoryPoints = CalculateGpuPoints(_gpuEncodeHistory);
            GpuDecodeHistoryPoints = CalculateGpuPoints(_gpuDecodeHistory);
        }

        private static PointCollection CalculateGpuPoints(LinkedList<double> history)
        {
            double w = 100.0, h = 100.0;
            double step = w / (MaxHistoryLength - 1);
            var points = new PointCollection(MaxHistoryLength + 2) { new Point(0, h) };
            int i = 0;
            foreach (var val in history) points.Add(new Point(i++ * step, h - val));
            points.Add(new Point(w, h));
            points.Freeze();
            return points;
        }

        public string ConfigSummary
        {
            get
            {
                string diskPart;
                if (Disks == null || Disks.Count == 0)
                    diskPart = Properties.Resources.Common_NoDisk;
                else
                {
                    diskPart = string.Join(" + ", Disks
                        .Select(d => d.MaxSize / 1073741824.0)
                        .OrderByDescending(g => g)
                        .Select(g => g >= 1 ? $"{g:0.#} GB" : $"{g * 1024:0} MB"));
                }
                return string.Format(Properties.Resources.Format_VmSummary, CpuCount, MemoryGb, diskPart);
            }
        }


        // ===== transient 状态机：将 backend raw state（StateText）和 view 端 transient state（如 "Starting"） =====
        // 合成最终显示的 State；并由此推导 IsRunning

        public void SyncBackendData(string realState, TimeSpan realUptime)
        {
            bool wasRunning = this.IsRunning;

            _backendState = realState;
            _anchorUptime = realUptime;
            _anchorLocalTime = DateTime.Now;

            // 同步进 Model（StateText / RawUptime 是 Service 读取的字段）
            if (Model != null)
            {
                Model.StateText = realState;
                Model.RawUptime = realUptime;
            }

            if (_transientState != null && ShouldClearTransientState(realState))
                _transientState = null;

            RefreshStateDisplay();

            if (wasRunning != this.IsRunning)
                OnPropertyChanged(nameof(IsRunning));

            TickUptime();
        }

        public void TickUptime()
        {
            if (!IsRunning) { Uptime = "00:00:00"; return; }
            var currentTotal = _anchorUptime + (DateTime.Now - _anchorLocalTime);
            Uptime = currentTotal.TotalDays >= 1
                ? $"{(int)currentTotal.TotalDays}.{currentTotal.Hours:D2}:{currentTotal.Minutes:D2}:{currentTotal.Seconds:D2}"
                : $"{currentTotal.Hours:D2}:{currentTotal.Minutes:D2}:{currentTotal.Seconds:D2}";
        }

        private void RefreshStateDisplay()
        {
            State = _transientState ?? _backendState ?? string.Empty;
            IsRunning = !string.IsNullOrEmpty(State) && !new[] {
                Properties.Resources.Status_Off, "Off",
                Properties.Resources.Status_Suspended, "Paused",
                Properties.Resources.Status_Saved, "Saved"
            }.Contains(State);

            if (!IsRunning)
            {
                UpdateMemoryStatus(0, 0);
                IsGpuActive = false;
                Gpu3dUsage = 0;
                GpuCopyUsage = 0;
                GpuEncodeUsage = 0;
                GpuDecodeUsage = 0;
                UpdateSingleGpuHistory(_gpu3dHistory, 0);
                UpdateSingleGpuHistory(_gpuCopyHistory, 0);
                UpdateSingleGpuHistory(_gpuEncodeHistory, 0);
                UpdateSingleGpuHistory(_gpuDecodeHistory, 0);
                RefreshGpuPoints();
            }
        }

        private bool ShouldClearTransientState(string backend)
        {
            if ((_transientState == Properties.Resources.Status_Starting || _transientState == Properties.Resources.Status_Restarting) && (backend == Properties.Resources.Status_Running || backend == "Running")) return true;
            if ((_transientState == Properties.Resources.Status_StoppingPresent || _transientState == Properties.Resources.Status_Saving) && (backend == Properties.Resources.Status_Off || backend == "Off" || backend == Properties.Resources.Status_Saved || backend == "Saved" || backend == Properties.Resources.Status_Suspended || backend == "Paused")) return true;
            if (_transientState == Properties.Resources.Status_Suspending && (backend == Properties.Resources.Status_Suspended || backend == "Paused" || backend == Properties.Resources.Status_Saved || backend == "Saved")) return true;
            return false;
        }

        public void SetTransientState(string text) { _transientState = text; RefreshStateDisplay(); }
        public void ClearTransientState() { _transientState = null; RefreshStateDisplay(); }
    }
}
