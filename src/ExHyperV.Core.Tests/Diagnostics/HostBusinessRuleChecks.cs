using System.Globalization;
using ExHyperV.Services;
using R = ExHyperV.Properties.Resources;

internal static class HostBusinessRuleChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(!HostGpuService.IsGpuPartitioningVersionSupported(17133)
            && HostGpuService.IsGpuPartitioningVersionSupported(17134),
            "Host rules: GPU-PV indicator retains the original build boundary");
        check(!HostNvmeService.IsNativeNvmeSupported(26099)
            && HostNvmeService.IsNativeNvmeSupported(26100),
            "Host rules: native NVMe availability retains the original build boundary");
        bool nvmeRejected = false;
        try { HostNvmeService.EnsureNativeNvmeSupported(26099); }
        catch (PlatformNotSupportedException) { nvmeRejected = true; }
        check(nvmeRejected, "Host rules: unsupported native NVMe requests fail before registry access");
        HostNvmeService.EnsureNativeNvmeSupported(26100);
        check(HostProductTypeService.CanApplySwitch(null)
            && !HostProductTypeService.CanApplySwitch(new("", false, []))
            && !HostProductTypeService.CanApplySwitch(new("ServerNT", true, ["recovery"]))
            && !HostProductTypeService.CanApplySwitch(new("WinNT", true, ["recovery"])),
            "Host rules: both known and unknown pending replacements prevent another switch");

        var originalCulture = R.Culture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN" })
            {
                R.Culture = CultureInfo.GetCultureInfo(culture);
                void Verify(bool passed, string name) => check(passed, "Host rules: " + name + " (" + culture + ")");
                var range = HostNetworkService.ValidateDynamicMacAddressRange(" 00:15:5d:00:00:0a ", "00-15-5D-00-00-FF");
                Verify(range.Error == null && range.Minimum == "00155D00000A" && range.Maximum == "00155D0000FF",
                    "MAC range accepts existing separator/case/whitespace forms and returns canonical values");
                Verify(HostNetworkService.NormalizeDynamicMacAddress("00:15-5d:00-00:0a") == "00155D00000A",
                    "mixed MAC separators retain the existing accepted behavior");
                Verify(HostNetworkService.ValidateDynamicMacAddressRange("000000000000", "000000000000").Error == null,
                    "equal MAC endpoints retain the existing accepted behavior");
                Verify(HostNetworkService.ValidateDynamicMacAddressRange("00155D0000FF", "00155D00000A").Error
                    == R.Error_Host_DynamicMacRangeOrder, "reversed MAC range keeps the original message");
                foreach (string? invalid in new string?[] { null, " ", "00155D0000", "00:15:5D:00:00:GG", "00 15 5D 00 00 00", "00155D00000000" })
                    Verify(HostNetworkService.ValidateDynamicMacAddressRange(invalid, "00155D000000").Error
                        == R.Error_Host_DynamicMacRangeInvalid, "malformed MAC endpoint is rejected before writing: " + (invalid ?? "<null>"));
                Verify(SwitchConfigurationRules.GetDeletionError("{C08CB7B8-9B3C-408E-8E30-5E16A3AEB444}")
                    == R.Warning_CannotModifyDefaultSwitch, "default switch deletion uses identity and keeps the existing warning");
                Verify(SwitchConfigurationRules.GetDeletionError("c08cb7b8-9b3c-408e-8e30-5e16a3aeb444")
                    == R.Warning_CannotModifyDefaultSwitch, "default switch deletion also handles unbraced IDs");
                Verify(SwitchConfigurationRules.GetDeletionError("10000000-0000-0000-0000-000000000001") == null,
                    "custom switch deletion remains available");
            }
        }
        finally { R.Culture = originalCulture; }
    }
}
