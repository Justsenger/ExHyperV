namespace ExHyperV.Models;

public sealed class VmAdvancedBehaviorSettings
{
    public bool AllowFullScsiCommandSetAvailable { get; init; }
    public bool AllowFullScsiCommandSet { get; init; }
    public bool LockOnDisconnectAvailable { get; init; }
    public bool LockOnDisconnect { get; init; }
    public bool TurnOffOnGuestRestartAvailable { get; init; }
    public bool TurnOffOnGuestRestart { get; init; }
    public bool EnableHibernationAvailable { get; init; }
    public bool EnableHibernation { get; init; }
}

public enum VmAdvancedBehavior
{
    AllowFullScsiCommandSet,
    LockOnDisconnect,
    TurnOffOnGuestRestart,
    EnableHibernation,
}
