using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmBootService
{

    /// <summary>判断当前状态是否允许修改引导 Num Lock。</summary>
    /// <param name="isRunning">当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。</param>
    /// <returns>未运行时为 true。</returns>
    public static bool CanEditBootNumLock(bool isRunning) => !isRunning;

}
