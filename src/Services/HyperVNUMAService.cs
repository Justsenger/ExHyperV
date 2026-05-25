using System.Diagnostics;
using ExHyperV.Api;

namespace ExHyperV.Services;

public class HyperVNUMAService
{
    private const string SettingWql = "SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData";
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    public static async Task<bool> GetNumaSpanningEnabledAsync()
    {
        try
        {
            var response = await WmiApi.QueryFirstAsync(
                SettingWql,
                obj => obj["NumaSpanningEnabled"] is bool val && val);

            if (response.Success && !response.IsEmpty)
                return response.Data;

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HyperVNUMAService] GetNumaSpanningEnabled error: {ex.Message}");
            return true;
        }
    }

    public static async Task<(bool success, string message)> SetNumaSpanningEnabledAsync(bool enabled)
    {
        var result = await WmiApi.WithObjectAsync(
            wql: SettingWql,
            modifier: obj => obj["NumaSpanningEnabled"] = enabled,
            submitMethod: "ModifyServiceSettings",
            submitParamName: "SettingData",
            wrapInArray: false,
            serviceWql: ServiceWql);

        return result.Success
            ? (true, Properties.Resources.Msg_SettingsUpdated)
            : (false, result.Error);
    }
}