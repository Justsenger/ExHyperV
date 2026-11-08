using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

internal static class DeviceWorkflowChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await CheckPcieAsync(check);
        await CheckVmPcieAsync(check);
    }

    private const string TargetId = "11111111-1111-1111-1111-111111111111";
    private const string SourceId = "22222222-2222-2222-2222-222222222222";
    private const string OtherId = "33333333-3333-3333-3333-333333333333";

    private static PcieDeviceInfo Device(string id, string type = "Display", string? status = null, string? path = null) => new()
    {
        InstanceId = id, FriendlyName = id, ClassType = type,
        Status = status ?? HostPcieService.HostKey, Path = path ?? "PCIROOT(0)#PCI(0600)#PCI(0000)"
    };

    private static async Task CheckPcieAsync(Action<bool, string> check)
    {
        var gpu = Device("GPU");
        var audio = Device("Audio", "MEDIA", path: "PCIROOT(0)#PCI(0600)#PCI(0001)");
        var backend = new PcieBackend { Mmio = MmioCheckResultType.NeedsExpansion };
        var session = new PcieAssignmentSession(gpu, [gpu, audio], TargetId, backend.Create());
        var state = await session.AdvanceAsync();
        check(state.Step == PcieAssignmentStep.ConfirmMmio && backend.Calls.SequenceEqual(["mmio"]),
            "MMIO expansion waits for a decision before changing VM state");
        var mmioDecision = new PcieAssignmentDecision(state.RequestId!.Value, true);
        state = await session.AdvanceAsync(mmioDecision);
        check(state.Step == PcieAssignmentStep.ConfirmLastGpu && backend.Calls.SequenceEqual(["mmio", "expand"]),
            "Approved MMIO expansion precedes last-GPU confirmation");
        try { await session.AdvanceAsync(mmioDecision); check(false, "A stale PCIe decision was accepted"); }
        catch (InvalidOperationException) { check(session.State == state && backend.Calls.Count == 2, "An earlier decision cannot approve a later step"); }
        check(await session.AdvanceAsync() == state && backend.Calls.Count == 2,
            "PCIe waits for a decision without repeating the completed preparation");
        state = await session.AdvanceAsync(new(session.State.RequestId!.Value, false));
        await session.AdvanceAsync();
        check(state.Step == PcieAssignmentStep.Cancelled && backend.Calls.Count == 2,
            "Declining the last GPU stops the workflow and cannot later execute it");

        foreach (bool queryFails in new[] { true, false })
        {
            backend = new PcieBackend { FailMmio = queryFails, Mmio = MmioCheckResultType.NeedsExpansion, Expand = false };
            session = new(gpu, [gpu], TargetId, backend.Create());
            state = await session.AdvanceAsync();
            if (state.RequiresDecision) state = await session.AdvanceAsync(new(state.RequestId!.Value, true));
            check(state.Error == (queryFails ? PcieAssignmentError.MmioCheck : PcieAssignmentError.MmioUpdate) &&
                state.RefreshRequired == !queryFails && !backend.Calls.Any(c => c.StartsWith("execute")),
                "MMIO query and expansion failures remain distinct with accurate refresh requirements");
        }

        var storage = Device("Controller", "SCSIAdapter");
        backend = new PcieBackend { Disks = [new(0, "system", true, false, false), new(1, "data", false, false, false)] };
        state = await new PcieAssignmentSession(storage, [storage], TargetId, backend.Create()).AdvanceAsync();
        check(state.Error == PcieAssignmentError.SystemDisk && backend.Calls.SequenceEqual(["mmio", "disks"]),
            "PCIe refuses a controller with a system disk before offlining any data disk");

        backend = new PcieBackend { Disks = [new(1, "data", false, false, false)] };
        session = new(storage, [storage], TargetId, backend.Create());
        state = await session.AdvanceAsync();
        check(state.Step == PcieAssignmentStep.ConfirmOfflineDisks && !backend.Calls.Contains("disk:1:True"),
            "PCIe online disks require an explicit decision before offlining");
        await session.AdvanceAsync(new(session.State.RequestId!.Value, false));
        check(!backend.Calls.Any(c => c.StartsWith("disk:") || c.StartsWith("execute:")),
            "PCIe declining disk offlining leaves the disk and controller untouched");

        backend = new PcieBackend { Disks = [new(1, "one", false, false, false), new(2, "two", false, false, true)] };
        backend.Failures.Add("Controller");
        session = new(storage, [storage], TargetId, backend.Create());
        await session.AdvanceAsync();
        state = await session.AdvanceAsync(new(session.State.RequestId!.Value, true));
        check(state.Error == PcieAssignmentError.Execution && state.RefreshRequired &&
            backend.Calls.SequenceEqual(["mmio", "disks", "disk:1:True", "execute:Controller", "disk:1:False"]),
            "PCIe assignment failure restores only disks offlined by this operation");
        int calls = backend.Calls.Count;
        await session.AdvanceAsync();
        check(backend.Calls.Count == calls, "A completed PCIe failure cannot replay device writes or compensation");

        gpu = Device("GPU", status: SourceId);
        audio = Device("Audio", "MEDIA", SourceId, "PCIROOT(0)#PCI(0600)#PCI(0001)");
        var other = Device("Other", "MEDIA", OtherId, "PCIROOT(0)#PCI(0600)#PCI(0002)");
        backend = new PcieBackend();
        session = new(gpu, [gpu, audio, other], TargetId, backend.Create());
        state = await session.AdvanceAsync();
        check(state.Step == PcieAssignmentStep.ConfirmCompanions && state.Message.Contains("Audio") && !state.Message.Contains("Other"),
            "PCIe companion candidates must belong to the same card and current owner");
        state = await session.AdvanceAsync(new(session.State.RequestId!.Value, false));
        check(state.Step == PcieAssignmentStep.Completed && backend.Calls.SequenceEqual(["mmio", "execute:GPU"]),
            "Declining companion devices still assigns the main PCIe device");

        backend = new PcieBackend(); backend.Failures.Add("GPU");
        session = new(gpu, [gpu, audio], TargetId, backend.Create());
        await session.AdvanceAsync(); await session.AdvanceAsync(new(session.State.RequestId!.Value, true));
        check(!backend.Calls.Contains("execute:Audio"), "PCIe main-device failure stops companion assignment");
        var extra = Device("Extra", "MEDIA", SourceId, "PCIROOT(0)#PCI(0600)#PCI(0002)");
        backend = new PcieBackend(); backend.Failures.Add("Audio");
        session = new(gpu, [gpu, audio, extra], HostPcieService.HostKey, backend.Create());
        await session.AdvanceAsync(); state = await session.AdvanceAsync(new(session.State.RequestId!.Value, true));
        check(state.Error == PcieAssignmentError.Execution && state.Message.Contains("Audio") &&
            backend.Calls.SequenceEqual(["execute:GPU", "execute:Audio", "execute:Extra"]),
            "PCIe return preserves companion order and continues after an individual companion failure");

        storage = Device("Controller", "HDC", TargetId);
        backend = new PcieBackend { EmptyDiskReads = 1, Disks = [new(1, "data", false, false, true), new(0, "boot", false, true, true)] };
        state = await new PcieAssignmentSession(storage, [storage], HostPcieService.HostKey, backend.Create()).AdvanceAsync();
        check(state.Step == PcieAssignmentStep.Completed && backend.Calls.SequenceEqual([
            "execute:Controller", "delay:1500", "disks", "delay:1500", "disks", "disk:1:False"]),
            "PCIe storage return waits for enumeration and onlines only noncritical offline disks");
        backend = new PcieBackend { EmptyDiskReads = 10 };
        await new PcieAssignmentSession(storage, [storage], HostPcieService.HostKey, backend.Create()).AdvanceAsync();
        check(backend.Calls.Count(c => c == "disks") == 4, "PCIe storage return preserves the four-poll limit");
    }

    private static async Task CheckVmPcieAsync(Action<bool, string> check)
    {
        var calls = new List<string>();
        var device = new VmPcieDeviceSetting { WmiInstanceId = "device", GuestModeAvailable = true };
        var backend = new VmPcieChangeBackend(
            _ => Task.FromResult(ApiResponse<VmPcieSettings>.Ok(new() { SystemSettingsAvailable = true, Devices = [device] })),
            (_, enabled, topology) => { calls.Add($"system:{enabled}:{topology}"); return Task.FromResult(ApiResponse.Ok()); },
            (_, _, mode) => { calls.Add("device:" + mode); return Task.FromResult(ApiResponse.Ok()); });
        VmPcieChangeSession Session(VmPcieChangeKind kind, bool enabled = false, ushort topology = 0,
            VmPcieGuestMode mode = VmPcieGuestMode.Emulated, VmPcieChangeBackend? custom = null) =>
            new(kind, Guid.Parse(TargetId), enabled, topology, device, mode, custom ?? backend);

        var session = Session(VmPcieChangeKind.Topology, topology: 1);
        var result = await session.ApplyAsync(false);
        check(session.RequiresConfirmation && result.Cancelled && calls.Count == 0,
            "Host PCIe topology requires permanent-emulation confirmation in Core");
        await session.ApplyAsync(true);
        check(calls.Count == 0, "A declined VM PCIe operation cannot be replayed with a later approval");
        session = Session(VmPcieChangeKind.Topology);
        result = await session.ApplyAsync();
        check(!session.RequiresConfirmation && result.Success && calls.SequenceEqual(["system:False:0"]),
            "Default PCIe topology retains the existing emulation state without a new confirmation");
        calls.Clear();
        session = Session(VmPcieChangeKind.DeviceMode);
        result = await session.ApplyAsync(true);
        await session.ApplyAsync(true);
        check(result.Success && session.EmulationEnabled && calls.SequenceEqual(["system:True:0", "device:Emulated"]),
            "Emulated PCIe device mode enables system emulation first and executes each step once");

        calls.Clear();
        var failSystem = backend with { SetSystem = (_, _, _) => { calls.Add("system-failed"); return Task.FromResult(ApiResponse.Fail("system error")); } };
        session = Session(VmPcieChangeKind.DeviceMode, custom: failSystem);
        result = await session.ApplyAsync(true);
        check(result.Error == "system error" && !session.EmulationEnabled && calls.SequenceEqual(["system-failed"]),
            "PCIe system failure returns the first error and prevents the device write");

        calls.Clear();
        var failDevice = backend with { SetDevice = (_, _, _) => Task.FromResult(ApiResponse.Fail("device error")) };
        session = Session(VmPcieChangeKind.DeviceMode, custom: failDevice);
        result = await session.ApplyAsync(true);
        check(result.Error == "device error" && session.EmulationEnabled && calls.SequenceEqual(["system:True:0"]),
            "PCIe partial failure retains the successful permanent-emulation change");
        var throwDevice = backend with { SetDevice = (_, _, _) => Task.FromException<ApiResponse>(new InvalidOperationException("device threw")) };
        session = Session(VmPcieChangeKind.DeviceMode, custom: throwDevice);
        try { await session.ApplyAsync(true); } catch (InvalidOperationException) { }
        check(session.EmulationEnabled, "PCIe device exceptions also preserve the successful first-step state");

        calls.Clear();
        session = Session(VmPcieChangeKind.DeviceMode, mode: VmPcieGuestMode.Paravirtualized);
        await session.ApplyAsync();
        check(!session.RequiresConfirmation && calls.SequenceEqual(["device:Paravirtualized"]),
            "Paravirtualized PCIe mode does not enable emulation");
    }

    internal sealed class PcieBackend
    {
        internal readonly List<string> Calls = [];
        internal readonly HashSet<string> Failures = [];
        internal MmioCheckResultType Mmio = MmioCheckResultType.Ok;
        internal bool Expand = true, FailMmio;
        internal List<ControllerDisk> Disks = [];
        internal int EmptyDiskReads;
        internal PcieAssignmentBackend Create() => new(
            _ => { Calls.Add("mmio"); return Task.FromResult(FailMmio ? ApiResponse<MmioCheckResultType>.Fail("mmio failed") : ApiResponse<MmioCheckResultType>.Ok(Mmio)); },
            _ => { Calls.Add("expand"); return Task.FromResult(Expand ? ApiResponse.Ok() : ApiResponse.Fail("expand failed")); },
            _ => { Calls.Add("disks"); return Task.FromResult(ApiResponse<List<ControllerDisk>>.Ok(EmptyDiskReads-- > 0 ? [] : Disks)); },
            (disk, offline) => { Calls.Add($"disk:{disk}:{offline}"); return Task.FromResult(ApiResponse.Ok()); },
            (_, _, id, _) => { Calls.Add("execute:" + id); return Task.FromResult(Failures.Contains(id) ? ApiResponse.Fail("failed") : ApiResponse.Ok()); },
            delay => { Calls.Add("delay:" + delay); return Task.CompletedTask; });
    }

}
