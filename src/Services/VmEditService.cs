using ExHyperV.Api;

namespace ExHyperV.Services;

public class VmEditService
{
    public async Task<(bool Success, string Message)> RenameVmAsync(Guid vmGuid, string newName)
    {
        // Msvm_ComputerSystem 里 Name 字段存的是 GUID，不是显示名
        // 先找到 VM 对应的 VirtualSystemSettingData，改 ElementName，再提交
        string settingsWql = $@"SELECT * FROM Msvm_VirtualSystemSettingData
                                WHERE VirtualSystemIdentifier = '{vmGuid}'
                                AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

        var result = await WmiApi.WithObjectAsync(
            wql: settingsWql,
            modifier: obj => obj["ElementName"] = newName,
            submitMethod: "ModifySystemSettings",
            submitParamName: "SystemSettings",
            wrapInArray: false);

        return result.Success
            ? (true, Properties.Resources.Msg_Success)
            : (false, result.Error);
    }
}