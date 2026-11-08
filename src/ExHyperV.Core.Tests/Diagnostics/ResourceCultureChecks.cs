using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using R = ExHyperV.Properties.Resources;

internal static class ResourceCultureChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var previousOverride = R.Culture;
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            R.Culture = null!;
            foreach (string language in new[] { "en-US", "zh-CN", "en-US" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                var expected = new[]
                {
                    R.BootOrderItem_FloppyDisk, R.BootOrderItem_OpticalDrive,
                    R.BootOrderItem_IdeHardDisk, R.BootOrderItem_PxeNetworkBoot,
                    R.BootOrderItem_ScsiHardDisk, R.BootOrderItem_UnknownDevice
                };
                var items = Enumerable.Range(0, 6).Select(code => VmBootService.CreateGen1((ushort)code)).ToArray();
                check(items.Select(item => item.Name).SequenceEqual(expected) &&
                    items.Select(item => item.Reference).Cast<int>().SequenceEqual(Enumerable.Range(0, 6)),
                    "Gen1 boot names follow each call's language without changing device identities: " + language);
            }

            var english = Task.Run(async () =>
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                await Task.Yield();
                return VmBootService.CreateGen1(0).Name;
            });
            var chinese = Task.Run(async () =>
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
                await Task.Yield();
                return VmBootService.CreateGen1(0).Name;
            });
            await Task.WhenAll(english, chinese);
            check(english.Result == R.ResourceManager.GetString(nameof(R.BootOrderItem_FloppyDisk), CultureInfo.GetCultureInfo("en-US")) &&
                chinese.Result == R.ResourceManager.GetString(nameof(R.BootOrderItem_FloppyDisk), CultureInfo.GetCultureInfo("zh-CN")),
                "default resource lookup preserves independent asynchronous caller languages");

            R.Culture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            check(VmBootService.CreateGen1(0).Name == R.ResourceManager.GetString(nameof(R.BootOrderItem_FloppyDisk), CultureInfo.GetCultureInfo("zh-CN")),
                "explicit Resources.Culture remains the compatible process-wide override");

            R.Culture = null!;
            await CheckPcieSnapshotsAsync(check);
            CheckNetworkIdentity(check);
            CheckCreationSwitchIdentity(check);
        }
        finally
        {
            R.Culture = previousOverride;
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private static async Task CheckPcieSnapshotsAsync(Action<bool, string> check)
    {
        string hostKey = HostPcieService.HostKey, dismountedKey = HostPcieService.DismountedKey;
        foreach (string originalLanguage in new[] { "en-US", "zh-CN" })
        {
            var originalCulture = CultureInfo.GetCultureInfo(originalLanguage);
            string oldHost = "\u200B" + R.ResourceManager.GetString(nameof(R.Host), originalCulture);
            string oldDismounted = R.ResourceManager.GetString(nameof(R.Status_Dismounted), originalCulture)!;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(originalLanguage == "en-US" ? "zh-CN" : "en-US");
            check(HostPcieService.HostKey == hostKey && HostPcieService.DismountedKey == dismountedKey &&
                HostPcieService.GetAssignmentDisplayName(hostKey) == R.Host &&
                HostPcieService.GetAssignmentDisplayName(dismountedKey) == R.Status_Dismounted,
                "PCIe identities remain stable while their display follows the caller language: " + originalLanguage);

            var executed = new List<(string Target, string Current)>();
            var backend = new PcieAssignmentBackend(
                _ => Task.FromResult(ApiResponse<MmioCheckResultType>.Ok(MmioCheckResultType.Ok)),
                _ => Task.FromResult(ApiResponse.Ok()),
                _ => Task.FromResult(ApiResponse<List<ControllerDisk>>.Ok([])),
                (_, _) => Task.FromResult(ApiResponse.Ok()),
                (target, current, _, _) =>
                {
                    executed.Add((target, current));
                    return Task.FromResult(ApiResponse.Ok());
                },
                _ => Task.CompletedTask);
            var gpu = new PcieDeviceInfo { InstanceId = "gpu", FriendlyName = "GPU", ClassType = "Display", Status = oldHost };
            var state = await new PcieAssignmentSession(gpu, [gpu], "11111111-1111-1111-1111-111111111111", backend).AdvanceAsync();
            check(state.Step == PcieAssignmentStep.ConfirmLastGpu && executed.Count == 0,
                "legacy PCIe host snapshot retains last-GPU confirmation after language changes: " + originalLanguage);

            var detached = new PcieDeviceInfo { InstanceId = "detached", ClassType = "Media", Status = oldDismounted };
            state = await new PcieAssignmentSession(detached, [detached], oldHost, backend).AdvanceAsync();
            check(state.Step == PcieAssignmentStep.Completed && executed.SequenceEqual([(hostKey, dismountedKey)]),
                "legacy PCIe detached snapshot and host target reach execution as stable identities: " + originalLanguage);

            gpu = new PcieDeviceInfo { InstanceId = "gpu", FriendlyName = "GPU", ClassType = "Display", Status = "22222222-2222-2222-2222-222222222222", Path = "PCIROOT(0)#PCI(0600)#PCI(0000)" };
            var companion = new PcieDeviceInfo { InstanceId = "audio", FriendlyName = "Audio", ClassType = "Media", Status = "22222222-2222-2222-2222-222222222222", Path = "PCIROOT(0)#PCI(0600)#PCI(0001)" };
            state = await new PcieAssignmentSession(gpu, [gpu, companion], oldHost, backend).AdvanceAsync();
            check(state.Step == PcieAssignmentStep.ConfirmCompanions &&
                state.Message == string.Format(R.PCIePage_Msg_CompanionDevice, "GPU", "• Audio", R.Host),
                "PCIe companion confirmation displays the localized host name instead of its stable key: " + originalLanguage);
        }
        check(HostPcieService.NormalizeAssignmentTarget("主机") == "主机" &&
            HostPcieService.NormalizeAssignmentTarget("Host") == "Host" &&
            HostPcieService.GetAssignmentDisplayName("ordinary-vm") == "ordinary-vm",
            "ordinary VM names stay distinct from the reserved PCIe host target");
    }

    private static void CheckNetworkIdentity(Action<bool, string> check)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        var adapter = new VmNetworkAdapter();
        var legacyDisconnected = adapter.SwitchName;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        check(adapter.SwitchIdentity == string.Empty && adapter.SwitchName == R.Status_Unconnected,
            "an existing disconnected adapter follows a later caller language without changing its raw identity");

        adapter.SwitchIdentity = null;
        var legacyUnknown = adapter.SwitchName;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        check(adapter.SwitchIdentity == null && adapter.SwitchName == R.Common_UnknownSwitch,
            "unknown switch identity remains absent while its original placeholder is localized");
        adapter = new VmNetworkAdapter { SwitchName = legacyUnknown };
        check(adapter.SwitchIdentity == null,
            "old unknown-switch display values are accepted at the legacy boundary after language changes");
        adapter = new VmNetworkAdapter { SwitchName = legacyDisconnected };
        check(adapter.SwitchIdentity == string.Empty,
            "old disconnected display values are accepted at the legacy boundary after language changes");

        foreach (string switchName in new[] { "Disconnected", "未连接", R.Common_UnknownSwitch })
        {
            adapter = new VmNetworkAdapter { SwitchIdentity = switchName };
            check(adapter.SwitchIdentity == switchName && adapter.SwitchName == switchName,
                "an actual switch name matching display text stays a raw switch identity: " + switchName);
        }

        adapter = new VmNetworkAdapter { SwitchIdentity = "real-switch" };
        adapter.SwitchIdentity = null;
        adapter.SwitchIdentity = string.Empty;
        adapter.SwitchIdentity = "WMI_partial";
        check(adapter.SwitchIdentity == "real-switch" && adapter.SwitchName == "real-switch",
            "incomplete switch refreshes retain the previous valid selection");
        var notifications = new List<string?>();
        adapter.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        adapter.SwitchIdentity = "updated-switch";
        check(notifications.SequenceEqual([nameof(VmNetworkAdapter.SwitchIdentity), nameof(VmNetworkAdapter.SwitchName)]),
            "changing raw switch identity also refreshes the compatible display binding");
    }

    private static void CheckCreationSwitchIdentity(Action<bool, string> check)
    {
        foreach (string originalLanguage in new[] { "en-US", "zh-CN" })
        {
            string legacyNone = R.ResourceManager.GetString(nameof(R.Common_None), CultureInfo.GetCultureInfo(originalLanguage))!;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(originalLanguage == "en-US" ? "zh-CN" : "en-US");
            check(VmCreateService.IsNoSwitchSelection(legacyNone) && VmCreateService.IsNoSwitchSelection(null) &&
                VmCreateService.IsNoSwitchSelection(" \t") && !VmCreateService.IsNoSwitchSelection("Fixture Switch"),
                "creation recognizes old no-switch selections after a caller language change: " + originalLanguage);
        }
    }
}
