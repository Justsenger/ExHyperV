using System.Text.Json.Serialization;

namespace ExHyperV.Server.Hosting;

[Flags]
internal enum ConsoleRights
{
    None = 0, Watch = 1, Control = 2, AudioOutput = 4, AudioInput = 8, Clipboard = 16, DisplayMode = 32,
    All = Watch | Control | AudioOutput | AudioInput | Clipboard | DisplayMode
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TicketScope(Guid? VmId, string? Channel, string? BackendId, string[]? RequestedRights, string? ViewerContextId)
{
    internal bool Empty => VmId == null && Channel == null && BackendId == null && RequestedRights == null && ViewerContextId == null;

    internal ConsoleRights Validate()
    {
        if (VmId is null || VmId == Guid.Empty || Channel != "console" || string.IsNullOrWhiteSpace(BackendId)
            || BackendId.Length > 64 || ViewerContextId is { Length: > 128 } || RequestedRights is not { Length: >= 1 and <= 6 })
            throw new ArgumentException("需要有效的虚拟机、控制台通道、后端与观看范围。");
        ConsoleRights rights = ConsoleRights.None;
        foreach (string name in RequestedRights)
        {
            if (!Enum.TryParse<ConsoleRights>(name, false, out var right) || !Enum.IsDefined(right) || right.ToString() != name
                || !System.Numerics.BitOperations.IsPow2((uint)right) || (rights & right) != 0)
                throw new ArgumentException("控制台权限名称无效或重复。");
            rights |= right;
        }
        if (!rights.HasFlag(ConsoleRights.Watch)) throw new ArgumentException("控制台需要观看权限。");
        return rights;
    }
}

internal sealed record SocketAuthorization(Guid? SessionId, string Subject, Guid? VmId, string? Channel, string? BackendId,
    ConsoleRights Rights, long Version, string? ViewerContextId, DateTimeOffset? LoginExpiresAt, ulong? ExtensionVersion = null)
{
    internal bool Scoped => Channel != null;
}

internal sealed class ActiveSocket(Action abort) : IDisposable
{
    private readonly object gate = new();
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool revoked;
    internal long DeliveredFrames { get; private set; }

    // Admission and handoff to the transport are one operation relative to revocation.
    internal ValueTask Deliver(Func<ValueTask> write, bool video)
    {
        lock (gate)
        {
            if (revoked) throw new OperationCanceledException();
            var result = write();
            if (video) DeliveredFrames++;
            return result;
        }
    }

    internal Task Revoke()
    {
        lock (gate)
        {
            if (!revoked) { revoked = true; abort(); }
            return finished.Task;
        }
    }

    public void Dispose()
    {
        lock (gate) { revoked = true; finished.TrySetResult(); }
    }
}
