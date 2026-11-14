using ExHyperV.Tools;
using System.Text.RegularExpressions;

namespace ExHyperV.Services;

/// <summary>校验并修改宿主动态 MAC 地址池。</summary>
public static class HostNetworkService
{


    /// <summary>将支持的 MAC 输入规范为无分隔符的大写十六进制。</summary>
    /// <returns>12 位规范 MAC；空值或格式不合法时为 null。</returns>
    /// <param name="value">12 位十六进制，或由冒号/短横线分隔的六组地址。</param>
    internal static string? NormalizeDynamicMacAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        if (!Regex.IsMatch(trimmed, "^(?:[0-9A-Fa-f]{12}|(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2})$"))
            return null;
        return Regex.Replace(trimmed, "[:-]", string.Empty).ToUpperInvariant();
    }


    /// <summary>规范化动态 MAC 地址池，并校验输入格式及上下界顺序。</summary>
    /// <returns>规范化上下界及错误；Error=null 表示校验通过。</returns>
    /// <param name="minimum">动态 MAC 池下界，支持规范化输入格式。</param>
    /// <param name="maximum">动态 MAC 池上界，必须不小于下界。</param>
    /// <remarks>检查范围为输入格式与上下界顺序；地址分配限制由 Hyper-V 在提交时校验。</remarks>
    public static (string? Minimum, string? Maximum, string? Error) ValidateDynamicMacAddressRange(string? minimum, string? maximum)
    {
        string? normalizedMinimum = NormalizeDynamicMacAddress(minimum);
        string? normalizedMaximum = NormalizeDynamicMacAddress(maximum);
        string? error = normalizedMinimum == null || normalizedMaximum == null
            ? Properties.Resources.Error_Host_DynamicMacRangeInvalid
            : string.CompareOrdinal(normalizedMinimum, normalizedMaximum) > 0
                ? Properties.Resources.Error_Host_DynamicMacRangeOrder
                : null;
        return (normalizedMinimum, normalizedMaximum, error);
    }


    /// <summary>校验后提交宿主动态 MAC 地址池上下界。</summary>
    /// <returns>校验或 Hyper-V 提交响应。</returns>
    /// <param name="minimum">动态 MAC 池下界，支持规范化输入格式。</param>
    /// <param name="maximum">动态 MAC 池上界，必须不小于下界。</param>
    public static Task<ApiResponse> SetDynamicMacAddressRangeAsync(string minimum, string maximum)
    {
        var range = ValidateDynamicMacAddressRange(minimum, maximum);
        if (range.Error != null) return Task.FromResult(ApiResponse.Fail(range.Error));
        return HyperVHostSettingsService.ModifyAsync(obj =>
        {
            obj["MinimumMacAddress"] = range.Minimum;
            obj["MaximumMacAddress"] = range.Maximum;
        });
    }

}
