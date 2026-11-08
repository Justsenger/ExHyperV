using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Tools;

// Owns an overlapped handle. The caller must drain its operations before Dispose.
// Cancellation only requests native cancellation: buffers stay pinned until the
// kernel has completed the IRP and delivered the completion packet.
internal sealed class OverlappedDevice : IDisposable
{
    private readonly ThreadPoolBoundHandle bound;
    internal SafeFileHandle Handle { get; }

    internal OverlappedDevice(SafeFileHandle handle)
    {
        Handle = handle;
        try { bound = ThreadPoolBoundHandle.BindHandle(handle); }
        catch { handle.Dispose(); throw; }
    }

    internal static OverlappedDevice Open(string path, uint access = 0xc0000000)
    {
        var handle = CreateFileW(path, access, 3, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return new(handle);
    }

    internal Task<int> ReadAsync(byte[] buffer, int offset, int length, CancellationToken token) =>
        RunAsync(0, buffer, offset, length, null, 0, 0, token);
    internal Task<int> WriteAsync(byte[] buffer, int offset, int length, CancellationToken token) =>
        RunAsync(1, buffer, offset, length, null, 0, 0, token);
    internal Task<int> ControlAsync(uint code, byte[]? input, byte[]? output, CancellationToken token) =>
        RunAsync(2, input, 0, input?.Length ?? 0, output, output?.Length ?? 0, code, token);

    internal unsafe delegate bool ConnectOperation(nint handle, NativeOverlapped* overlapped);
    internal Task<int> ConnectAsync(ConnectOperation operation, CancellationToken token) =>
        RunAsync(3, null, 0, 0, null, 0, 0, token, operation);

    private async Task<int> RunAsync(int kind, byte[]? input, int offset, int length,
        byte[]? output, int outputLength, uint code, CancellationToken token, ConnectOperation? connect = null)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || length < 0 || offset > (input?.Length ?? 0) - length)
            throw new ArgumentOutOfRangeException(nameof(length));
        using var operation = new Operation(this, input, output);
        operation.Start(kind, offset, length, outputLength, code, connect);
        using var cancellation = token.UnsafeRegister(static state => ((Operation)state!).Cancel(), operation);
        var result = await operation.Completion.Task.ConfigureAwait(false);
        if (result.Error == 995 && token.IsCancellationRequested) throw new OperationCanceledException(token);
        if (result.Error != 0) throw new Win32Exception(result.Error);
        return result.Count;
    }

    private sealed unsafe class Operation : IDisposable
    {
        private readonly OverlappedDevice owner;
        private readonly NativeOverlapped* overlapped;
        private GCHandle inputPin, outputPin;
        private bool addedReference;
        private uint maximumCompletion;
        internal readonly TaskCompletionSource<(int Error, int Count)> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Operation(OverlappedDevice owner, byte[]? input, byte[]? output)
        {
            this.owner = owner;
            try
            {
                owner.Handle.DangerousAddRef(ref addedReference);
                if (input is { Length: > 0 }) inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
                if (output is { Length: > 0 }) outputPin = GCHandle.Alloc(output, GCHandleType.Pinned);
                overlapped = owner.bound.AllocateNativeOverlapped(static (error, bytes, pointer) =>
                {
                    var state = (Operation)ThreadPoolBoundHandle.GetNativeOverlappedState(pointer)!;
                    state.Completion.TrySetResult(bytes <= state.maximumCompletion
                        ? ((int)error, (int)bytes) : (13, 0));
                }, this, null);
            }
            catch { Dispose(); throw; }
        }

        internal void Start(int kind, int offset, int length, int outputLength, uint code, ConnectOperation? connect)
        {
            maximumCompletion = (uint)(kind == 2 ? outputLength : kind == 3 ? 0 : length);
            byte* input = inputPin.IsAllocated ? (byte*)inputPin.AddrOfPinnedObject() + offset : null;
            void* output = outputPin.IsAllocated ? (void*)outputPin.AddrOfPinnedObject() : null;
            bool succeeded = kind switch
            {
                0 => ReadFile(owner.Handle, input, (uint)length, null, overlapped),
                1 => WriteFile(owner.Handle, input, (uint)length, null, overlapped),
                2 => DeviceIoControl(owner.Handle, code, input, (uint)length, output, (uint)outputLength, null, overlapped),
                3 => connect!(owner.Handle.DangerousGetHandle(), overlapped),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            if (!succeeded)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 997) Completion.TrySetResult((error, 0));
            }
            // Immediate success still posts an IOCP packet (skip-on-success is never set).
        }

        internal void Cancel() => _ = CancelIoEx(owner.Handle, overlapped);

        public void Dispose()
        {
            if (overlapped != null) owner.bound.FreeNativeOverlapped(overlapped);
            if (outputPin.IsAllocated) outputPin.Free();
            if (inputPin.IsAllocated) inputPin.Free();
            if (addedReference) { owner.Handle.DangerousRelease(); addedReference = false; }
        }
    }

    public void Dispose() { bound.Dispose(); Handle.Dispose(); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(SafeFileHandle file, void* data, uint length, uint* count, NativeOverlapped* overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(SafeFileHandle file, void* data, uint length, uint* count, NativeOverlapped* overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle file, uint code, void* input, uint inputLength, void* output, uint outputLength, uint* count, NativeOverlapped* overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);
}
