using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

// Public disconnect categories for the local connector. FreeRDP codes are interpreted separately.
internal enum ConsoleBackendFailure : uint
{
    Unknown, BackendUnavailable = 0xEC000001, ProtocolIncompatible, ViewDenied
}

internal sealed class ConsoleSessionState
{
    internal ConsoleSessionMode Preference { get; set; }
    internal ConsoleSessionMode? Fallback { get; private set; }
    internal string? ActiveBackend { get; private set; }
    internal ConsoleSessionMode Target => Fallback ?? Preference;
    internal ConsoleCapabilities Policy { get; private set; }
    internal ConsoleCapabilities Capabilities { get; private set; }
    internal ConsoleControlState Control { get; private set; } = new(false, false, 0);
    internal ConsoleTransportKind Transport { get; private set; }
    private bool accepting;

    internal void Activate(string backend, ConsoleCapabilities policy, ConsoleCapabilities supported, ConsoleControlState control)
    {
        ActiveBackend = backend;
        Policy = policy;
        Capabilities = supported;
        Control = control;
        Transport = backend switch
        {
            "RdpEnhanced" => ConsoleTransportKind.RdpEnhanced, "RdpBasic" => ConsoleTransportKind.RdpBasic, _ => ConsoleTransportKind.Unknown
        };
        accepting = true;
    }

    internal bool Allows(ConsoleCapabilities capability) => accepting && Control.CanView &&
        (Policy & Capabilities & capability) == capability;
    internal bool CanInput => Control.HasControl && Allows(ConsoleCapabilities.Input);
    internal void UpdateGrant(ConsoleExtensionSnapshot s)
    {
        Control = s.Control;
        Capabilities = ConsoleExtensionCatalog.Ready(s) ? s.Granted & s.Supported : ConsoleCapabilities.None;
    }
    internal void SetTransport(ConsoleTransportKind path) => Transport = path;
    internal bool StopInputs()
    {
        bool release = CanInput;
        accepting = false;
        return release;
    }
    internal void Clear()
    {
        accepting = false;
        ActiveBackend = null;
        Control = new(false, false, 0);
        Policy = Capabilities = ConsoleCapabilities.None;
        Transport = ConsoleTransportKind.Unknown;
    }
    internal ConsoleCapabilities FallbackLimit { get; private set; } = ~ConsoleCapabilities.None;

    internal void ResetFallback()
    {
        Fallback = null;
        FallbackLimit = ~ConsoleCapabilities.None;
    }

    internal static ConsoleBackendFailure Classify(string backend, uint code) => backend switch
    {
        ConsoleExtensionCatalog.External when Enum.IsDefined((ConsoleBackendFailure)code) => (ConsoleBackendFailure)code,
        // FreeRDP CONNECT_TRANSPORT_FAILED and the Core handshake timeout.
        "RdpEnhanced" when code is 0x0002000D or 0xFFFFFFFE => ConsoleBackendFailure.BackendUnavailable,
        _ => ConsoleBackendFailure.Unknown
    };

    internal bool TryFallback(ConsoleBackendFailure failure, bool allowed)
    {
        if (!allowed || !CanInput ||
            failure is not (ConsoleBackendFailure.BackendUnavailable or ConsoleBackendFailure.ProtocolIncompatible)) return false;
        Fallback = ActiveBackend switch
        {
            ConsoleExtensionCatalog.External => ConsoleSessionMode.RdpEnhanced,
            "RdpEnhanced" => ConsoleSessionMode.RdpBasic,
            _ => null
        };
        if (Fallback.HasValue) FallbackLimit &= Policy & Capabilities;
        return Fallback.HasValue;
    }
}
