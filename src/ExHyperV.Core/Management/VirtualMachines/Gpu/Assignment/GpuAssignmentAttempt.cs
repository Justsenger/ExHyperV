using ExHyperV.Tools;

namespace ExHyperV.Services;

// 失败的操作仍可能创建了适配器；结果未确认时，即使有候选身份也不能授权清理。
internal sealed record GpuAssignmentAttempt(
    ApiResponse Response,
    string? CreatedAdapterId = null,
    bool OwnershipUncertain = false);
