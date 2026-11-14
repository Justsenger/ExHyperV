using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>单台虚拟机导出的完成状态和保留文件。</summary>
/// <param name="Success">是否确认导出成功并找到配置文件。</param>
/// <param name="VmId">请求导出的虚拟机稳定 GUID。</param>
/// <param name="OutputPath">成功完成的导出目录；未成功时为 null。</param>
/// <param name="OperationStateUnknown">原生导出任务的最终状态是否仍待确认。</param>
/// <param name="RetainedPaths">本次产生且保留供检查的文件或目录；未知状态时不得直接删除。</param>
/// <param name="Error">失败原因，成功时为 null。</param>
/// <param name="Code">底层错误码，需结合 ErrorSource 解释。</param>
/// <param name="ErrorSource">错误码来源。</param>
/// <param name="ErrorKind">与底层接口无关的失败类别。</param>
public sealed record VmExportResult(bool Success, Guid VmId, string? OutputPath,
    bool OperationStateUnknown, IReadOnlyList<string> RetainedPaths, string? Error = null,
    int Code = 0, ApiErrorSource ErrorSource = ApiErrorSource.None, ApiErrorKind ErrorKind = ApiErrorKind.None);
