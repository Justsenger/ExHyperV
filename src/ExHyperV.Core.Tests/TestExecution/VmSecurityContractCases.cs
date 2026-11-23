using System.Security.Cryptography.X509Certificates;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmSecurityContractCases
{
    internal static async Task RunAsync(string api, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        if (api == "VmProcessorService.GetHostMaxVirtualCpuCountAsync()")
        {
            var limit = await c.CallAsync(() => VmProcessorService.GetHostMaxVirtualCpuCountAsync(vm.Id));
            var maxima = SystemBaseline.Query("SELECT VirtualQuantity FROM Msvm_ProcessorSettingData WHERE InstanceID LIKE 'Microsoft:Definition%Maximum'");
            int? expected = maxima.Count == 1 && Convert.ToInt32(maxima[0]["VirtualQuantity"]) > 0
                ? Convert.ToInt32(maxima[0]["VirtualQuantity"]) : null;
            c.Equal(expected, limit, "二代机上限与唯一提供者记录相符");
            return;
        }
        if (api == "VmSerialPortService.SetPortSettingsAsync()")
        {
            var before = await VmSerialPortService.GetSerialPortsAsync(vm.Id);
            c.Require(before.HasData, before.Error);
            var port = before.Data.Single(p => p.PortNumber == 1);
            string pipe = $@"\\.\pipe\ExHyperV-{vm.Id:N}";
            Require(await c.CallAsync(() => VmSerialPortService.SetPortSettingsAsync(vm.Id, 1, pipe, null)));
            var after = await VmSerialPortService.GetSerialPortsAsync(vm.Id);
            c.Require(after.HasData, after.Error);
            var saved = after.Data.Single(p => p.PortNumber == 1);
            c.Equal(pipe, saved.PipePath, "管道写后回读");
            c.Equal(port.DebuggerMode, saved.DebuggerMode, "未指定的调试模式保留");
            return;
        }

        async Task<VmVtpmInfo> Snapshot()
        {
            var result = await VmSecurityService.GetVtpmStateAsync(vm.Id);
            c.Require(result.HasData, result.Error);
            return result.Data;
        }
        void Require(ApiResponse result) => VmSettingsApiCases.Success(c, result);
        Task<string?> RawKey() => ReadKeyAsync(vm.Id);
        var initial = api == "VmSecurityService.GetVtpmStateAsync()" ? await c.CallAsync(Snapshot) : await Snapshot();
        if (api == "VmSecurityService.GetVtpmStateAsync()")
        {
            var native = SystemBaseline.Security(vm.Id);
            c.Equal(Convert.ToBoolean(native["TpmEnabled"]), initial.TpmEnabled, "TPM 状态与当前系统配置一致");
            c.Equal(Convert.ToBoolean(native["ShieldingRequested"]), initial.Shielded, "屏蔽配置与系统一致");
            return;
        }
        if (api == "VmSecurityService.SetVtpmPolicyAsync()")
        {
            if (initial.VirtualizationBasedSecurityOptOut is not bool original)
                throw new ApiCheckSkippedException("宿主未提供来宾 VBS 策略。");
            Require(await c.CallAsync(() => VmSecurityService.SetVtpmPolicyAsync(vm.Id, VtpmPolicy.VirtualizationBasedSecurityOptOut, !original)));
            c.Equal(!original, Convert.ToBoolean(SystemBaseline.Security(vm.Id)["VirtualizationBasedSecurityOptOut"]), "原生 VBS 策略回读");
            return;
        }
        if (string.IsNullOrEmpty(initial.LocalSigningThumbprint))
            throw new ApiCheckSkippedException("本机缺少可复用的 Guardian，本检查需要已登记的签名和解密证书。");
        string certificatePath = Path.Combine(c.DirectoryPath, "guardian.cer");
        if (api == "VmSecurityService.ExportLocalGuardianAsync()")
        {
            Require(await c.CallAsync(() => VmSecurityService.ExportLocalGuardianAsync(vm.Id, certificatePath)));
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            c.Equal(initial.LocalSigningThumbprint, certificate.Thumbprint, "导出证书与选中 Guardian 签名身份相符");
            c.Require(!certificate.HasPrivateKey, "导出仅含公钥");
            return;
        }
        if (api == "VmSecurityService.ReissueLocalKeyProtectorAsync()")
        {
            Require(await c.CallAsync(() => VmSecurityService.ReissueLocalKeyProtectorAsync(vm.Id)));
            c.Require(!string.IsNullOrEmpty(await RawKey()) && (await Snapshot()).HasLocalKey, "系统保护器及本机授权已核实");
            return;
        }
        Require(await VmSecurityService.ReissueLocalKeyProtectorAsync(vm.Id));
        string? baseline = await RawKey();
        c.Require(!string.IsNullOrEmpty(baseline), "准备了本机可解锁的密钥保护器");
        switch (api)
        {
            case "VmSecurityService.RestorePreviousKeyProtectorAsync()":
                Require(await VmSecurityService.ReissueLocalKeyProtectorAsync(vm.Id));
                Require(await c.CallAsync(() => VmSecurityService.RestorePreviousKeyProtectorAsync(vm.Id)));
                c.Equal(baseline, await RawKey(), "恢复的保护器字节与原版一致");
                break;
            case "VmSecurityService.GrantHostAccessAsync()":
                Require(await VmSecurityService.ExportLocalGuardianAsync(vm.Id, certificatePath));
                Require(await c.CallAsync(() => VmSecurityService.GrantHostAccessAsync(vm.Id, certificatePath)));
                c.Equal(baseline, await RawKey(), "重复授权当前宿主保留原保护器");
                break;
            case "VmSecurityService.RevokeHostAccessAsync()":
                var owner = (await Snapshot()).AuthorizedHosts.Single(h => h.IsOwner);
                var denied = await c.CallAsync(() => VmSecurityService.RevokeHostAccessAsync(vm.Id, owner.SigningThumbprint, owner.EncryptionThumbprint));
                c.RequireSettled(denied.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, denied.Error);
                c.Require(!denied.Success, "所有者授权受到保护");
                c.Equal(baseline, await RawKey(), "撤销被拒绝后保护器字节一致");
                break;
            default: throw new InvalidDataException(api);
        }
    }

    private static async Task<string?> ReadKeyAsync(Guid id)
    {
        var value = await WindowsTestCommand.RunAsync("$vm=Get-VM -Id ([guid]$a.id); [Convert]::ToBase64String([byte[]](Get-VMKeyProtector -VM $vm))|ConvertTo-Json", new { id });
        return value.GetString();
    }
}
