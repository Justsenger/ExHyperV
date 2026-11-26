using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExHyperV.Services;

/// <summary>宿主电源设置项的值输入模式。</summary>
public enum HostPowerSettingValueKind
{
    /// <summary>固定离散选项索引列表。</summary>
    Choice = 0,
    /// <summary>定义了最小、最大与步长的连续无符号整数范围。</summary>
    Range = 1,
    /// <summary>未定义选项列表与上下限的原始无符号整数设置。</summary>
    RawValue = 2
}

/// <summary>离散电源设置的可选索引与显示名称。</summary>
/// <param name="Value">写入电源方案时使用的选项索引。</param>
/// <param name="Name">系统提供的选项友好名称。</param>
public sealed record HostPowerSettingOption(uint Value, string Name);

/// <summary>Windows 电源计划中的单个可调设置项及其交流、直流当前值。</summary>
/// <param name="SubgroupId">所属子组 GUID。</param>
/// <param name="Id">电源设置 GUID。</param>
/// <param name="Name">设置显示名称；不可读时回退为 GUID 文本。</param>
/// <param name="Description">系统原生提供的设置说明文本；无说明时为空字符串。</param>
/// <param name="IsHidden">系统属性是否将其标记为默认隐藏（ATTRIB_HIDE）。</param>
/// <param name="ValueKind">值表示类型：离散选项、数值范围或原始无符号整数。</param>
/// <param name="Minimum">范围设置最小值；非范围类型为 0。</param>
/// <param name="Maximum">范围设置最大值；非范围类型为 0，原始值为 uint.MaxValue。</param>
/// <param name="Increment">范围设置步长；至少为 1。</param>
/// <param name="Unit">范围设置单位文本（如 %、秒、毫秒）；无单位时为空字符串。</param>
/// <param name="Options">离散设置的所有可选索引；非离散类型为只读空列表。</param>
/// <param name="AcValue">接通电源 (AC) 下的当前设置索引。</param>
/// <param name="DcValue">使用电池 (DC) 下的当前设置索引。</param>
public sealed record HostPowerSetting(
    Guid SubgroupId,
    Guid Id,
    string Name,
    string Description,
    bool IsHidden,
    HostPowerSettingValueKind ValueKind,
    uint Minimum,
    uint Maximum,
    uint Increment,
    string Unit,
    IReadOnlyList<HostPowerSettingOption> Options,
    uint AcValue,
    uint DcValue);

/// <summary>Windows 电源计划中的设置子组及其包含的电源设置项。</summary>
/// <param name="Id">子组 GUID（全局无子组项对应 HostPowerPlanService.NoSubgroupId）。</param>
/// <param name="Name">子组友好名称；对于 NoSubgroupId 返回空字符串，由调用方按需本地化展示。</param>
/// <param name="Settings">该子组下已读取的电源设置集合。</param>
public sealed record HostPowerSubgroup(
    Guid Id,
    string Name,
    IReadOnlyList<HostPowerSetting> Settings);

public static partial class HostPowerPlanService
{
    private const uint AccessSubgroup = 17;
    private const uint AccessIndividualSetting = 18;
    private const uint AttributeHide = 1;

    /// <summary>Windows 电源策略中用于归类无子组全局设置的特殊 GUID。</summary>
    public static readonly Guid NoSubgroupId = new("fea3413e-7e05-4911-9a71-700331f1c294");

    private static readonly Guid[] SubgroupDisplayOrder =
    [
        new("54533251-82be-4824-96c1-47b60b740d00"), // 处理器电源管理
        new("501a4d13-42af-4429-9fd1-a8218c268e20"), // PCI Express
        new("0012ee47-9041-4b5d-9b77-535fba8b1442"), // 硬盘
        new("238c9fa8-0aad-41ed-83f4-97be242c8f20"), // 睡眠
        new("7516b95f-f776-4464-8c53-06167f40cc99"), // 显示
        new("2a737441-1930-4402-8d77-b2bebba308a3"), // USB 设置
        new("4f971e89-eebd-4455-a8de-9e59040e7347"), // 电源按钮和盖子
        new("e73a048d-bf27-4f12-9731-8b2076e8891f"), // 电池
        new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1"), // 无线适配器设置
        new("9596fb26-9850-41fd-ac3e-f7c3c00afd4b"), // 多媒体设置
        new("0d7dbae2-4294-402a-ba8e-26777e8488cd"), // 桌面背景设置
        new("02f815b5-a5cf-4c84-bf20-649d1f75d3d8"), // Internet Explorer
        new("5fb4938d-1ee8-4b0f-9a3c-5036b0ab995c"), // 图形设置
        new("de830923-a562-41af-a086-e3a2c6bad2da"), // 节能模式设置
        new("2e601130-5351-4d9d-8e04-252966bad054"), // 空闲复原
        new("48672f38-7a9a-4bb2-8bf8-3d85be19de4e"), // 中断路由控制设置
        new("8619b916-e004-4dd8-9b66-dae86f806698"), // 状态感知电源操作
        NoSubgroupId
    ];

    private static readonly Dictionary<Guid, int> PrioritySettingOrder = new()
    {
        [new("893dee8e-2bef-41e0-89c6-b55d0929964c")] = 0, // 最小处理器状态
        [new("bc5038f7-23e0-4960-96da-33abaf5935ec")] = 1, // 最大处理器状态
        [new("be337238-0d82-4146-a960-4f3749d470c7")] = 2, // 处理器性能提升模式
        [new("0cc5b647-c1df-4637-891a-dec35c318583")] = 3, // 核心放置最小核心数量
        [new("93b8b6dc-0698-4d1c-9ee4-0644e900c85d")] = 4, // 异类线程调度策略
        [new("bae08b81-2d5e-4688-ad6a-13243356654b")] = 5, // 异类短运行线程调度策略
        [new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864")] = 6, // 第 1 类 EPP
        [new("5d76a2ca-e8c0-402f-a133-2158492d58ad")] = 7, // 处理器闲置禁用
        [new("94d3a615-a899-4ac5-ae2b-e4d8f634367f")] = 8, // 系统散热方式
        [new("75b0ae3f-bce0-45a7-8c89-c9611c25e100")] = 9  // 处理器最大频率
    };

    /// <summary>检测当前宿主是否安装了系统电池。</summary>
    /// <returns>检测到系统电池时为 true；台式机/服务器或无法识别为带电池设备时为 false。</returns>
    public static bool HasBattery()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
            return false;

        return (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;
    }

    /// <summary>枚举指定 Windows 电源计划下的全部子组及电源设置项元数据与当前 AC/DC 值。</summary>
    /// <remarks>同时查询默认可枚举子组与无子组全局项（NoSubgroupId）；无可用设置项的子组不会出现在结果中。</remarks>
    /// <param name="planId">电源计划 GUID。</param>
    /// <param name="includeHidden">是否包含系统默认隐藏（ATTRIB_HIDE）的底层设置项。</param>
    /// <returns>按常用硬件类别排序的子组列表；planId 为空或枚举无结果时返回空列表。</returns>
    public static IReadOnlyList<HostPowerSubgroup> GetPowerPlanSubgroups(Guid planId, bool includeHidden = false)
    {
        if (planId == Guid.Empty)
            return [];

        var subgroupIds = EnumerateSubgroupIds(planId);
        var groups = new List<HostPowerSubgroup>();

        foreach (Guid subgroupId in subgroupIds)
        {
            var settings = ReadSubgroupSettings(planId, subgroupId, includeHidden);
            if (settings.Count == 0)
                continue;

            string groupName = subgroupId == NoSubgroupId ? string.Empty : ReadSubgroupName(planId, subgroupId);
            groups.Add(new HostPowerSubgroup(subgroupId, groupName, settings));
        }

        groups.Sort(CompareSubgroups);
        return groups;
    }

    /// <summary>更新指定电源计划中单项设置的交流（AC）或直流（DC）值。</summary>
    /// <remarks>若目标计划为宿主当前活动计划，写入后会同步激活方案使其立即生效；原生接口失败时抛出 Win32Exception。</remarks>
    /// <param name="planId">目标电源计划 GUID。</param>
    /// <param name="subgroupId">所属子组 GUID（无子组设置传 NoSubgroupId）。</param>
    /// <param name="settingId">目标设置 GUID。</param>
    /// <param name="value">要写入的无符号整数值或选项索引。</param>
    /// <param name="applyToDc">true 表示写入使用电池 (DC) 索引，false 表示写入接通电源 (AC) 索引。</param>
    /// <exception cref="ArgumentException">planId、subgroupId 或 settingId 为空 GUID。</exception>
    /// <exception cref="Win32Exception">系统拒绝写入或激活电源方案，错误码保留在 NativeErrorCode 中。</exception>
    public static void SetPowerSettingValue(Guid planId, Guid subgroupId, Guid settingId, uint value, bool applyToDc)
    {
        if (planId == Guid.Empty) throw new ArgumentException("Power plan id cannot be empty.", nameof(planId));
        if (subgroupId == Guid.Empty) throw new ArgumentException("Subgroup id cannot be empty.", nameof(subgroupId));
        if (settingId == Guid.Empty) throw new ArgumentException("Setting id cannot be empty.", nameof(settingId));

        uint result = applyToDc
            ? PowerWriteDCValueIndex(IntPtr.Zero, ref planId, ref subgroupId, ref settingId, value)
            : PowerWriteACValueIndex(IntPtr.Zero, ref planId, ref subgroupId, ref settingId, value);

        if (result != ErrorSuccess)
            throw new Win32Exception((int)result);

        if (GetActivePowerPlanId() == planId)
            SetActivePowerPlan(planId);
    }

    private static List<Guid> EnumerateSubgroupIds(Guid planId)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        uint guidSize = (uint)Marshal.SizeOf<Guid>();
        IntPtr buffer = Marshal.AllocHGlobal((int)guidSize);

        try
        {
            for (uint index = 0; ; index++)
            {
                uint size = guidSize;
                uint result = PowerEnumerate(
                    IntPtr.Zero,
                    ref planId,
                    IntPtr.Zero,
                    AccessSubgroup,
                    index,
                    buffer,
                    ref size);

                if (result != ErrorSuccess)
                    break;

                Guid id = Marshal.PtrToStructure<Guid>(buffer);
                if (seen.Add(id))
                    ids.Add(id);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (seen.Add(NoSubgroupId))
            ids.Add(NoSubgroupId);

        return ids;
    }

    private static List<HostPowerSetting> ReadSubgroupSettings(Guid planId, Guid subgroupId, bool includeHidden)
    {
        var settings = new List<HostPowerSetting>();
        uint guidSize = (uint)Marshal.SizeOf<Guid>();
        IntPtr buffer = Marshal.AllocHGlobal((int)guidSize);

        try
        {
            for (uint index = 0; ; index++)
            {
                uint size = guidSize;
                uint result = PowerEnumerate(
                    IntPtr.Zero,
                    ref planId,
                    ref subgroupId,
                    AccessIndividualSetting,
                    index,
                    buffer,
                    ref size);

                if (result != ErrorSuccess)
                    break;

                Guid settingId = Marshal.PtrToStructure<Guid>(buffer);
                uint attributes = PowerReadSettingAttributes(ref subgroupId, ref settingId);
                bool isHidden = (attributes & AttributeHide) != 0;
                if (isHidden && !includeHidden)
                    continue;

                if (PowerReadACValueIndex(IntPtr.Zero, ref planId, ref subgroupId, ref settingId, out uint acValue) != ErrorSuccess)
                    continue;
                if (PowerReadDCValueIndex(IntPtr.Zero, ref planId, ref subgroupId, ref settingId, out uint dcValue) != ErrorSuccess)
                    dcValue = acValue;

                string name = ReadSettingName(planId, subgroupId, settingId);
                string description = ReadSettingDescription(planId, subgroupId, settingId);

                HostPowerSettingValueKind kind;
                uint minimum = 0;
                uint maximum = 0;
                uint increment = 1;
                string unit = string.Empty;
                IReadOnlyList<HostPowerSettingOption> options = [];

                if (PowerIsSettingRangeDefined(ref subgroupId, ref settingId))
                {
                    kind = HostPowerSettingValueKind.Range;
                    _ = PowerReadValueMin(IntPtr.Zero, ref subgroupId, ref settingId, out minimum);
                    if (PowerReadValueMax(IntPtr.Zero, ref subgroupId, ref settingId, out maximum) != ErrorSuccess || maximum < minimum)
                        maximum = uint.MaxValue;
                    if (PowerReadValueIncrement(IntPtr.Zero, ref subgroupId, ref settingId, out increment) != ErrorSuccess || increment == 0)
                        increment = 1;
                    unit = ReadUnitsSpecifier(subgroupId, settingId);
                }
                else
                {
                    var possible = ReadPossibleOptions(subgroupId, settingId);
                    if (possible.Count > 0)
                    {
                        kind = HostPowerSettingValueKind.Choice;
                        options = possible;
                    }
                    else
                    {
                        kind = HostPowerSettingValueKind.RawValue;
                        minimum = 0;
                        maximum = uint.MaxValue;
                        increment = 1;
                    }
                }

                settings.Add(new HostPowerSetting(
                    subgroupId,
                    settingId,
                    name,
                    description,
                    isHidden,
                    kind,
                    minimum,
                    maximum,
                    increment,
                    unit,
                    options,
                    acValue,
                    dcValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        settings.Sort(CompareSettings);
        return settings;
    }

    private static List<HostPowerSettingOption> ReadPossibleOptions(Guid subgroupId, Guid settingId)
    {
        var options = new List<HostPowerSettingOption>();
        for (uint index = 0; index < 256; index++)
        {
            bool ok = TryReadUnicodeString((byte[]? buffer, ref uint size) =>
                PowerReadPossibleFriendlyName(IntPtr.Zero, ref subgroupId, ref settingId, index, buffer, ref size), out string optionName);
            if (!ok)
                break;

            options.Add(new HostPowerSettingOption(index, string.IsNullOrEmpty(optionName) ? index.ToString() : optionName));
        }

        return options;
    }

    private static string ReadSubgroupName(Guid planId, Guid subgroupId) =>
        TryReadUnicodeString((byte[]? buffer, ref uint size) =>
            PowerReadFriendlyName(IntPtr.Zero, ref planId, ref subgroupId, IntPtr.Zero, buffer, ref size), out string text) && text.Length > 0
            ? text
            : subgroupId.ToString();

    private static string ReadSettingName(Guid planId, Guid subgroupId, Guid settingId) =>
        TryReadUnicodeString((byte[]? buffer, ref uint size) =>
            PowerReadFriendlyName(IntPtr.Zero, ref planId, ref subgroupId, ref settingId, buffer, ref size), out string text) && text.Length > 0
            ? text
            : settingId.ToString();

    private static string ReadSettingDescription(Guid planId, Guid subgroupId, Guid settingId) =>
        TryReadUnicodeString((byte[]? buffer, ref uint size) =>
            PowerReadDescription(IntPtr.Zero, ref planId, ref subgroupId, ref settingId, buffer, ref size), out string text)
            ? text
            : string.Empty;

    private static string ReadUnitsSpecifier(Guid subgroupId, Guid settingId) =>
        TryReadUnicodeString((byte[]? buffer, ref uint size) =>
            PowerReadValueUnitsSpecifier(IntPtr.Zero, ref subgroupId, ref settingId, buffer, ref size), out string text)
            ? text
            : string.Empty;

    private static int CompareSubgroups(HostPowerSubgroup left, HostPowerSubgroup right)
    {
        int li = Array.IndexOf(SubgroupDisplayOrder, left.Id);
        int ri = Array.IndexOf(SubgroupDisplayOrder, right.Id);
        int lr = li >= 0 ? li : int.MaxValue;
        int rr = ri >= 0 ? ri : int.MaxValue;
        int rankCmp = lr.CompareTo(rr);
        return rankCmp != 0 ? rankCmp : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    private static int CompareSettings(HostPowerSetting left, HostPowerSetting right)
    {
        int hiddenCmp = left.IsHidden.CompareTo(right.IsHidden);
        if (hiddenCmp != 0)
            return hiddenCmp;

        int lr = PrioritySettingOrder.TryGetValue(left.Id, out int lv) ? lv : int.MaxValue;
        int rr = PrioritySettingOrder.TryGetValue(right.Id, out int rv) ? rv : int.MaxValue;
        int priCmp = lr.CompareTo(rr);
        return priCmp != 0 ? priCmp : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerEnumerate(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags,
        uint index,
        IntPtr buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerEnumerate(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        uint accessFlags,
        uint index,
        IntPtr buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadDescription(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadSettingAttributes(
        ref Guid subGroupGuid,
        ref Guid powerSettingGuid);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerIsSettingRangeDefined(
        ref Guid subKeyGuid,
        ref Guid settingGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadValueMin(
        IntPtr rootPowerKey,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint valueMinimum);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadValueMax(
        IntPtr rootPowerKey,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint valueMaximum);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadValueIncrement(
        IntPtr rootPowerKey,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint valueIncrement);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadValueUnitsSpecifier(
        IntPtr rootPowerKey,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadPossibleFriendlyName(
        IntPtr rootPowerKey,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint possibleSettingIndex,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint acValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint dcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint acValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint dcValueIndex);
}
