using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmBootService
{

    /// <summary>判断当前状态是否允许修改引导 Num Lock。</summary>
    /// <param name="isRunning">虚拟机当前是否处于活动状态。</param>
    /// <returns>未运行时为 true。</returns>
    internal static bool CanEditBootNumLock(bool isRunning) => !isRunning;

    /// <summary>评估引导 Num Lock 的修改条件。</summary>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>是否允许修改、原因及需要的电源状态。</returns>
    public static VmModificationCondition GetNumLockModificationCondition(VmState state) => VmModificationCondition.PoweredOff(state);

}
