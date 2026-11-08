using System.Diagnostics;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>读取和修改宿主 Hyper-V 跨 NUMA 节点分配内存策略。</summary>
public static class HyperVNumaService
{
    private const string SettingWql = "SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData";
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    /// <summary>读取宿主是否允许跨 NUMA 节点分配。</summary>
    /// <returns>true 允许跨 NUMA 节点分配内存；false 不允许或系统未提供有效设置值。查询失败时返回错误。</returns>
    public static async Task<ApiResponse<bool>> GetNumaSpanningEnabledAsync()
    {
        try
        {
            var response = await WmiApi.QueryFirstAsync(
                SettingWql,
                obj => obj["NumaSpanningEnabled"] is bool val && val);

            return response.HasData ? response : ApiResponse<bool>.Fail(
                response.Success ? "未找到宿主 NUMA 设置。" : response.Error, response.Code, response.ErrorSource,
                response.Exception, response.Success ? ApiErrorKind.NotFound : response.ErrorKind);
        }
        catch (Exception ex)
        {
            return ApiResponse<bool>.Fail(ex.Message, exception: ex);
        }
    }

    /// <summary>提交宿主跨 NUMA 节点分配策略。</summary>
    /// <remarks>设置宿主后续的内存分配策略，已有虚拟机的内存保持原位置。</remarks>
    /// <returns>成功标志与失败说明。</returns>
    /// <param name="enabled">是否允许 NUMA 跨节点分配。</param>
    public static async Task<ApiResponse> SetNumaSpanningEnabledAsync(bool enabled)
    {
        var result = await WmiApi.WithObjectAsync(
            wql: SettingWql,
            modifier: obj => obj["NumaSpanningEnabled"] = enabled,
            submitMethod: "ModifyServiceSettings",
            submitParamName: "SettingData",
            wrapInArray: false,
            serviceWql: ServiceWql);

        return result;
    }
}
