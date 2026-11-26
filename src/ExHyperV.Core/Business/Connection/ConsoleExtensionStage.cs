namespace ExHyperV.Services;

/// <summary>保留原始状态码以兼容未来版本。Preserves unrecognized status codes.</summary>
/// <param name="RawCode">提供方报告的原始整数状态码。
/// <fielddoc><meaning>提供方报告的原始整数状态码。</meaning></fielddoc></param>
public sealed record ConsoleExtensionStage(int RawCode)
{
    /// <summary>当前版本可以解释的状态。Known interpretation of the raw code.</summary>
    /// <fielddoc><meaning>未识别的原码映射为 Unknown。</meaning></fielddoc>
    public ConsoleExtensionStateCode Code => Enum.IsDefined((ConsoleExtensionStateCode)RawCode)
        ? (ConsoleExtensionStateCode)RawCode : ConsoleExtensionStateCode.Unknown;
}
