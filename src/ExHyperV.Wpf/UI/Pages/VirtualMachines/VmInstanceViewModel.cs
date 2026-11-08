using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    /// <summary>
    /// 为 <see cref="VmInstance"/> 提供界面状态；后端快照更新，绑定集合原位合并。
    /// </summary>
    public partial class VmInstanceViewModel : ObservableObject, IDisposable
    {

        public VmInstance Model { get; private set; }

        public void Dispose()
        {
            if (MemorySettings != null) MemorySettings.PropertyChanged -= OnMemoryCapabilityInputChanged;
            foreach (var item in StorageItems) item.Dispose();
        }



        [ObservableProperty] private bool _isEditing;
        [ObservableProperty] private string _editedName = string.Empty;

        public void StartEditing()
        {
            EditedName = Name;
            IsEditing = true;
        }



        public Guid Id => Model.Id;

        [ObservableProperty] private string _name = string.Empty;
        partial void OnNameChanged(string value) { if (Model != null) Model.Name = value; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))]
        [NotifyPropertyChangedFor(nameof(CanChangeBootOrder))]
        [NotifyPropertyChangedFor(nameof(CanToggleConsoleSupport))]
        [NotifyPropertyChangedFor(nameof(CanEditSecurity))]
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
        [NotifyPropertyChangedFor(nameof(MemoryCapabilities))]
        [NotifyPropertyChangedFor(nameof(CanEditMmio))]
        [NotifyPropertyChangedFor(nameof(CanEditBootNumLock))]
        [NotifyPropertyChangedFor(nameof(CanEditHibernation))]
        [NotifyPropertyChangedFor(nameof(CanEditVideoResolution))]
        [NotifyPropertyChangedFor(nameof(CanEditMacAddress))]
        private bool _isRunning;

        /// <summary>
        /// 仅在 Hyper-V 明确报告 EnabledState=3（已关闭），且当前没有本地过渡状态时为 true。
        /// 保存、暂停和所有启动/停止过渡状态都不属于完全关机。
        /// </summary>
        public bool IsPoweredOff => _transientState == null && Model.IsPoweredOff;

        public bool CanChangeBootOrder => VmBootService.CanChangeBootOrder(Generation, IsRunning);

        // 控制台支持开关仅适用于第 2 代虚拟机（Enable/Disable-VMConsoleSupport 官方仅 Gen2 可用），且需关机时改
        public bool CanToggleConsoleSupport => VmConsoleSupportService.CanToggleConsoleSupport(Generation, IsRunning);

        // 安全启动 / vTPM 仅第 2 代可用，且需关机时改（与创建页的安全特性一致）
        public bool CanEditSecurity => VmSecurityService.CanEdit(Generation, IsRunning);
        public bool CanEditProcessorOffline => VmProcessorService.CanEditOfflineSettings(IsPoweredOff);
        public VmMemoryService.Capabilities MemoryCapabilities => VmMemoryService.GetCapabilities(IsRunning, MemorySettings?.DynamicMemoryEnabled == true);
        public bool CanEditMmio => VmMmioService.CanEdit(IsRunning);
        public bool CanEditBootNumLock => VmAdvancedBehaviorService.CanEditBootNumLock(IsRunning);
        public bool CanEditHibernation => VmAdvancedBehaviorService.CanEdit(VmAdvancedBehavior.EnableHibernation, true, IsRunning);
        public bool CanEditVideoResolution => VmDisplaySettingsService.CanEdit(IsRunning);
        public bool CanEditMacAddress => VmNetworkService.CanEditMacAddress(IsRunning);

        [ObservableProperty] private BitmapSource? _thumbnail;

        [ObservableProperty] private string _ipAddress = "---";
        [ObservableProperty] private string _ipAddressDisplay = "---";

        partial void OnIpAddressChanged(string value)
        {
            if (Model != null) Model.IpAddress = value == "---" ? string.Empty : value;
            RecomputeIpAddressDisplay(value);
        }

        [ObservableProperty] private string _macAddress = "00:00:00:00:00:00";
        partial void OnMacAddressChanged(string value) { if (Model != null) Model.MacAddress = value; }

        private TimeSpan _anchorUptime;
        private DateTime _anchorLocalTime;
        private string? _transientState;
        private string? _backendState;
        private bool _backendIsRunning;



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
        partial void OnMemoryGbChanged(double value) { if (Model != null) Model.MemoryGiB = value; }

        [ObservableProperty] private VmProcessorSettings? _processor;
        partial void OnProcessorChanged(VmProcessorSettings? value) { if (Model != null) Model.Processor = value; }

        [ObservableProperty] private VmMemorySettings? _memorySettings;
        partial void OnMemorySettingsChanged(VmMemorySettings? oldValue, VmMemorySettings? newValue)
        {
            if (oldValue != null) oldValue.PropertyChanged -= OnMemoryCapabilityInputChanged;
            if (newValue != null) newValue.PropertyChanged += OnMemoryCapabilityInputChanged;
            if (Model != null) Model.MemorySettings = newValue;
            OnPropertyChanged(nameof(MemoryCapabilities));
        }

        private void OnMemoryCapabilityInputChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VmMemorySettings.DynamicMemoryEnabled))
                OnPropertyChanged(nameof(MemoryCapabilities));
        }

        [ObservableProperty] private VmMmioSettings? _mmioSettings;
        partial void OnMmioSettingsChanged(VmMmioSettings? value) { if (Model != null) Model.MmioSettings = value; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryUsageString))]
        [NotifyPropertyChangedFor(nameof(MemoryLimitString))]
        private double _assignedMemoryGb;
        partial void OnAssignedMemoryGbChanged(double value) { if (Model != null) Model.AssignedMemoryGiB = value; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryDemandString))]
        private double _demandMemoryGb;
        partial void OnDemandMemoryGbChanged(double value) { if (Model != null) Model.DemandMemoryGiB = value; }

        [ObservableProperty] private PointCollection? _memoryHistoryPoints;

        private readonly LinkedList<double> _memoryUsageHistory = new();
        private const int MaxHistoryLength = 60;



        public ObservableCollection<VmDiskStatus> Disks { get; } = new();
        public ObservableCollection<VmStorageItemViewModel> StorageItems { get; } = new();
        public ObservableCollection<VmNetworkAdapter> NetworkAdapters { get; } = new();
        public ObservableCollection<BootOrderItemViewModel> BootOrderItems { get; } = new();
        public ObservableCollection<VmGpuAssignment> AssignedGpus { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))]
        private double _totalDiskSizeGb;
        partial void OnTotalDiskSizeGbChanged(double value) { if (Model != null) Model.TotalDiskSizeGiB = value; }



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

        public bool HasGpu => Model.HasGpuAssignment
            ?? ((AssignedGpus != null && AssignedGpus.Count > 0) || !string.IsNullOrEmpty(GpuName));

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



        public IAsyncRelayCommand<string>? ControlCommand { get; set; }

        // 右键菜单电源项：运行中请求正常关机，否则启动；失败由 Core 返回，不升级强制动作。
        public string PowerToggleText => IsRunning ? Properties.Resources.Button_ShutDown : Properties.Resources.Button_Start;
        public string PowerToggleAction => IsRunning ? "Stop" : "Start";



        public VmInstanceViewModel(VmInstance model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));

            // 构造期直接写字段，避免 setter 将相同值反写模型。
            _name = model.Name;
            _generation = model.Generation;
            _version = model.Version;
            _osType = model.OsType;
            CpuCount = model.CpuCount;
            MemoryGb = model.MemoryGiB;
            AssignedMemoryGb = model.AssignedMemoryGiB;
            DemandMemoryGb = model.DemandMemoryGiB;
            _totalDiskSizeGb = model.TotalDiskSizeGiB;
            _ipAddress = model.IpAddress;
            _macAddress = model.MacAddress;
            _gpuName = model.GpuName;
            _isGpuActive = model.IsGpuActive;
            _processor = model.Processor;
            MemorySettings = model.MemorySettings;
            MmioSettings = model.MmioSettings;

            foreach (var disk in model.Disks) Disks.Add(disk);
            foreach (var adapter in model.NetworkAdapters) NetworkAdapters.Add(adapter);
            foreach (var gpu in model.AssignedGpus) AssignedGpus.Add(gpu);
            foreach (var item in model.BootOrderItems) BootOrderItems.Add(new BootOrderItemViewModel(item));
            foreach (var item in model.StorageItems) StorageItems.Add(new VmStorageItemViewModel(item));

            RecomputeIpAddressDisplay(_ipAddress);

            InitializeGpuHistory();
            SyncBackendData(model);

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
                IpAddressDisplay = "---";
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


        // 调用方：PageVM.MonitorStateLoop 周期刷新 / SyncSingleVmStateAsync 单 VM 刷新
        // skipNameUpdate=true：PageVM 的 rename lockout 期内跳过 Name 同步
        // skipNetworkAdapters=true：用户在 NetworkSettings 页或 IsLoadingSettings 时跳过适配器抖动

        public void Apply(VmInstance fresh, bool skipNameUpdate = false, bool skipNetworkAdapters = false)
        {
            if (fresh == null) return;
            // 保留当前页的设置对象和稳定绑定集合，后端状态始终来自最新快照。
            fresh.Processor = Processor;
            fresh.MemorySettings = MemorySettings;
            fresh.MmioSettings = MmioSettings;
            // 列表快照只查询 GPU 摘要；完整分配集合由 GPU 设置页单独读取。
            if (fresh.HasGpuAssignment == false) AssignedGpus.Clear();
            else foreach (var gpu in AssignedGpus) fresh.AssignedGpus.Add(gpu);
            // 新快照的缺测状态保留，但界面仍显示最后一次取得的数值。
            if (fresh.AssignedMemorySample?.IsValid != true) fresh.AssignedMemoryGiB = AssignedMemoryGb;
            if (fresh.DemandMemorySample?.IsValid != true) fresh.DemandMemoryGiB = DemandMemoryGb;
            Model = fresh;
            OnPropertyChanged(nameof(HasGpu));

            if (!skipNameUpdate && Name != fresh.Name) Name = fresh.Name;
            if (CpuCount != fresh.CpuCount) CpuCount = fresh.CpuCount;
            if (MemoryGb != fresh.MemoryGiB) MemoryGb = fresh.MemoryGiB;
            if (fresh.AssignedMemorySample?.IsValid == true) AssignedMemoryGb = fresh.AssignedMemoryGiB;
            if (fresh.DemandMemorySample?.IsValid == true) DemandMemoryGb = fresh.DemandMemoryGiB;
            if (Generation != fresh.Generation) Generation = fresh.Generation;
            if (Version != fresh.Version) Version = fresh.Version;

            SyncBackendData(fresh);

            if (!IsRunning) IpAddress = string.Empty;

            // 网络页编辑期间不合并适配器，避免刷新覆盖输入。
            if (!skipNetworkAdapters)
                ReconcileNetworkAdapters(NetworkAdapters, fresh.NetworkAdapters);

            ReconcileDisks(Disks, fresh.Disks);

            // 磁盘容量(MaxSize)可能在合并中变化(扩容/换盘)，而元素属性变化不触发集合事件——
            // 这里重算总量，其 setter 经 NotifyPropertyChangedFor 带动 ConfigSummary 刷新(只靠增删的 CollectionChanged 会漏)
            double freshTotalGb = Disks.Sum(d => d.MaxSize) / 1073741824.0;
            if (Math.Abs(TotalDiskSizeGb - freshTotalGb) > 0.0001) TotalDiskSizeGb = freshTotalGb;

            // ── 6. GPU 摘要名 ─────────────────────────────────────
            GpuName = fresh.GpuName;
        }

        private static void ReconcileNetworkAdapters(
            ObservableCollection<VmNetworkAdapter> currentList,
            IEnumerable<VmNetworkAdapter> newList)
        {
            if (newList == null) return;
            var newSnapshot = newList.ToList();

            var toRemove = currentList.Where(c => !newSnapshot.Any(n => n.DeviceId == c.DeviceId)).ToList();
            foreach (var item in toRemove) currentList.Remove(item);

            foreach (var newItem in newSnapshot)
            {
                var existingItem = currentList.FirstOrDefault(c => c.DeviceId == newItem.DeviceId);
                if (existingItem != null)
                {
                    existingItem.Name = newItem.Name;
                    existingItem.IsConnected = newItem.IsConnected;
                    existingItem.SwitchIdentity = newItem.SwitchIdentity;
                    existingItem.MacAddress = newItem.MacAddress;
                    existingItem.IsStaticMac = newItem.IsStaticMac;
                    if (newItem.PerformanceSample?.IsValid == true)
                    {
                        existingItem.SendBytesPerSecond = newItem.SendBytesPerSecond;
                        existingItem.ReceiveBytesPerSecond = newItem.ReceiveBytesPerSecond;
                    }
                    existingItem.HasNetworkSpeedSample = newItem.HasNetworkSpeedSample;
                    existingItem.PerformanceSample = newItem.PerformanceSample ?? new(PerformanceSampleState.NotSampled, default);
                    if (!(existingItem.IpAddresses ?? []).SequenceEqual(newItem.IpAddresses ?? []))
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

        private static void ReconcileDisks(
            ObservableCollection<VmDiskStatus> currentList,
            IEnumerable<VmDiskStatus> newList)
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
                    if (newDiskData.CapacitySample?.IsValid == true)
                    {
                        existingDisk.MaxSize = newDiskData.MaxSize;
                        existingDisk.DiskType = newDiskData.DiskType;
                    }
                    existingDisk.CapacitySample = newDiskData.CapacitySample ?? new(PerformanceSampleState.NotSampled, default);
                    if (newDiskData.PerformanceSample?.IsValid == true)
                    {
                        existingDisk.ReadBytesPerSecond = newDiskData.ReadBytesPerSecond;
                        existingDisk.WriteBytesPerSecond = newDiskData.WriteBytesPerSecond;
                    }
                    existingDisk.PerformanceSample = newDiskData.PerformanceSample ?? new(PerformanceSampleState.NotSampled, default);

                    if (newDiskData.SizeSample?.IsValid == true)
                        existingDisk.CurrentSize = newDiskData.CurrentSize;
                    existingDisk.SizeSample = newDiskData.SizeSample ?? new(PerformanceSampleState.NotSampled, default);
                }
                else
                {
                    currentList.Add(newDiskData);
                }
            }
        }



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

        public void UpdateMemoryStatus(VmQueryService.VmDynamicMemoryData data)
        {
            if (!IsRunning) return;
            Model.AssignedMemorySample = data.AssignedSample;
            Model.DemandMemorySample = data.AssignedSample?.IsValid == true ? data.AvailableSample : data.AssignedSample;
            if (data.AssignedSample?.IsValid == true)
                AssignedMemoryGb = data.AssignedMiB / 1024.0;
            if (data.AvailableSample?.IsValid != true) return;
            if (data.AssignedSample?.IsValid == true)
                DemandMemoryGb = AssignedMemoryGb * (100d - data.AvailablePercent) / 100.0;
            UpdateHistoryPoints(100d - data.AvailablePercent);
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
            if (!IsRunning) return;
            if (Model.HasGpuAssignment == false && data.Sample?.State == PerformanceSampleState.NotApplicable
                && data.DriverSample?.State == PerformanceSampleState.NotApplicable)
            {
                // 运行中的目标明确没有 GPU，不属于本次读取失败。
                AssignedGpus.Clear();
                Model.AssignedGpus.Clear();
                Gpu3dUsage = GpuCopyUsage = GpuEncodeUsage = GpuDecodeUsage = 0;
                IsGpuActive = false;
                UpdateSingleGpuHistory(_gpu3dHistory, 0);
                UpdateSingleGpuHistory(_gpuCopyHistory, 0);
                UpdateSingleGpuHistory(_gpuEncodeHistory, 0);
                UpdateSingleGpuHistory(_gpuDecodeHistory, 0);
                RefreshGpuPoints();
                return;
            }
            bool hasUsageSample = data.Sample?.IsValid == true;
            bool hasDriverSample = data.DriverSample?.IsValid == true;
            if (hasUsageSample)
            {
                Gpu3dUsage = Math.Clamp(data.Gpu3d, 0, 100);
                GpuCopyUsage = Math.Clamp(data.GpuCopy, 0, 100);
                GpuEncodeUsage = Math.Clamp(data.GpuEncode, 0, 100);
                GpuDecodeUsage = Math.Clamp(data.GpuDecode, 0, 100);
            }
            bool hasEngineUsage = hasUsageSample && (Gpu3dUsage > 0 || GpuCopyUsage > 0 || GpuEncodeUsage > 0 || GpuDecodeUsage > 0);
            bool isLinuxGuest = !string.IsNullOrWhiteSpace(OsType) && OsType.Contains("linux", StringComparison.OrdinalIgnoreCase);
            if ((hasDriverSample && data.IsDriverBound) || hasEngineUsage || (HasGpu && isLinuxGuest))
                IsGpuActive = true;
            else if (hasUsageSample && hasDriverSample)
                IsGpuActive = false;
            if (!hasUsageSample) return;
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


        // 合成最终显示的 State；并由此推导 IsRunning

        public void SyncBackendData(VmInstance snapshot)
        {
            bool wasRunning = this.IsRunning;

            _backendState = snapshot.StateText;
            _backendIsRunning = snapshot.IsRunning;
            _anchorUptime = snapshot.RawUptime;
            _anchorLocalTime = DateTime.Now;

            // Model 已是本次快照；展示的过渡状态不写回后端状态。
            if (Model != null)
            {
                Model.StateText = snapshot.StateText;
                Model.RawUptime = snapshot.RawUptime;
            }

            if (_transientState != null && ShouldClearTransientState(snapshot.StateText))
                _transientState = null;

            RefreshStateDisplay();

            if (wasRunning != this.IsRunning)
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(PowerToggleText));
                OnPropertyChanged(nameof(PowerToggleAction));
            }

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
            // 乐观态期间(用户刚发起操作)一律视为活动；否则按后端原始状态码白名单判定，
            // 避免"合并磁盘/未知/等待启动"等非运行态被误判为运行(原字符串黑名单只排除 Off/Paused/Saved，会漏)
            IsRunning = _transientState != null || _backendIsRunning;
            OnPropertyChanged(nameof(IsPoweredOff));
            OnPropertyChanged(nameof(CanEditProcessorOffline));

            if (!IsRunning && Model.State is VmState.PoweredOff or VmState.Saved
                or VmState.Suspended or VmState.FastSaved or VmState.Hibernated)
            {
                AverageUsage = 0;
                foreach (var core in Cores) core.Usage = 0;
                foreach (var disk in Disks) disk.ReadBytesPerSecond = disk.WriteBytesPerSecond = 0;
                foreach (var adapter in NetworkAdapters) adapter.SendBytesPerSecond = adapter.ReceiveBytesPerSecond = 0;
                AssignedMemoryGb = 0;
                DemandMemoryGb = 0;
                UpdateHistoryPoints(0);
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

                // 关机/暂停/保存：清掉运行时缓存的 guest IP——否则停留在网络页时不随状态清，要退出再进才清
                // (只在有 IP 时清，避免每次轮询都重设空表触发刷新)
                foreach (var nic in NetworkAdapters)
                    if (nic.IpAddresses != null && nic.IpAddresses.Count > 0)
                        nic.IpAddresses = new List<string>();
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
