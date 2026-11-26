using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ExHyperV.App.Localization;
using ExHyperV.Services;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

// Use the native clipboard formats to preserve the packed DIB (including masks and palette)
// and CF_HTML byte offsets. WinRT supplies the change notification on the desktop.
internal sealed partial class ConsoleClipboardBridge : IDisposable
{
    private const int FormatLimit = 16 * 1024 * 1024, TotalLimit = 32 * 1024 * 1024;
    private static readonly uint Html = RegisterClipboardFormatW("HTML Format");
    private readonly IConsoleClipboard channel;
    private readonly DispatcherQueue dispatcher;
    private readonly Guid vmId;
    private readonly nint hwnd;
    private bool CanTransfer(bool toHost) => channel.IsAvailable && (channel is not IConsoleClipboardProvider provider ||
        provider.ProviderInfo is { IsReady: true } info && info.VmId == vmId && (toHost ? info.CanRead : info.CanWrite));
    private readonly Func<bool> focused;
    private readonly Action<string> notify;
    private bool disposed;
    private long revision;
    private uint observedSequence;
    private byte[]? fingerprint;

    internal ConsoleClipboardBridge(IConsoleClipboard channel, DispatcherQueue dispatcher, nint hwnd, Guid vmId,
        Func<bool> focused, Action<string> notify)
    {
        this.channel = channel;
        this.dispatcher = dispatcher;
        this.vmId = vmId;
        this.hwnd = hwnd;
        this.focused = focused;
        this.notify = notify;
        Clipboard.ContentChanged += OnHostChanged;
        channel.ClipboardAvailabilityChanged += OnAvailability;
        channel.ClipboardReceived += OnGuestChanged;
    }

    internal void Activated() => Schedule(null);
    private void OnHostChanged(object? sender, object args) => Schedule(null);
    private void OnAvailability(bool available) => dispatcher.TryEnqueue(() =>
    {
        if (disposed) return;
        revision++;
        observedSequence = 0;
        fingerprint = null;
        if (available) Schedule(null);
    });
    private void OnGuestChanged(ConsoleClipboardData data) => Schedule(data);

    private void Schedule(ConsoleClipboardData? guest) => dispatcher.TryEnqueue(() =>
    {
        if (!disposed && focused() && CanTransfer(guest != null))
            _ = SynchronizeAsync(guest, ++revision);
    });

    private async Task SynchronizeAsync(ConsoleClipboardData? guest, long version)
    {
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (disposed || version != revision || !focused() || !CanTransfer(guest != null)) return;
                uint sequence = GetClipboardSequenceNumber();
                if (guest == null && sequence != 0 && sequence == observedSequence) return;
                if (OpenClipboard(hwnd) != 0)
                {
                    try
                    {
                        var data = guest ?? Read();
                        if (data == null) return;
                        byte[] hash = Hash(data);
                        if (fingerprint == null || !hash.AsSpan().SequenceEqual(fingerprint))
                        {
                            if (guest != null) Write(guest);
                            else if (!channel.SetClipboard(data)) return;
                            fingerprint = hash;
                        }
                        observedSequence = GetClipboardSequenceNumber();
                    }
                    finally { CloseClipboard(); }
                    return;
                }
                await Task.Delay(25 * (attempt + 1));
            }
            if (!disposed && focused()) notify(L10n.Get("Console_ClipboardBusy"));
        }
        catch (Exception) when (!disposed) { notify(L10n.Get("Console_ClipboardFailed")); }
    }

    private static byte[] Hash(ConsoleClipboardData data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[8];
        foreach (var format in data.Formats.OrderBy(f => f.Name == null ? f.Id : uint.MaxValue))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header, format.Name == null ? format.Id : uint.MaxValue);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], format.Data.Length);
            hash.AppendData(header);
            hash.AppendData(format.Data.Span);
        }
        return hash.GetHashAndReset();
    }

    private static ConsoleClipboardData? Read()
    {
        var formats = new List<ConsoleClipboardFormat>();
        int total = 4;
        foreach (uint id in (uint[])[13, 8, Html])
        {
            if (id == 0 || IsClipboardFormatAvailable(id) == 0) continue;
            nint memory = GetClipboardData(id);
            nuint size = GlobalSize(memory);
            if (size is 0 or > FormatLimit) continue;
            nint ptr = GlobalLock(memory);
            if (ptr == 0) continue;
            try
            {
                byte[] bytes = new byte[(int)size];
                Marshal.Copy(ptr, bytes, 0, bytes.Length);
                int length = bytes.Length;
                if (id == 13)
                {
                    int end = MemoryMarshal.Cast<byte, ushort>(bytes).IndexOf((ushort)0);
                    if (end < 0) continue;
                    length = (end + 1) * 2;
                }
                total += length + 12 + (id == Html ? 11 : 0);
                if (total > TotalLimit) break;
                formats.Add(new(id, id == Html ? "HTML Format" : null, bytes.AsMemory(0, length)));
            }
            finally { GlobalUnlock(memory); }
        }
        return formats.Count != 0 || CountClipboardFormats() == 0 ? new(formats) : null;
    }

    private static unsafe void Write(ConsoleClipboardData data)
    {
        var blocks = new List<(uint Id, nint Handle)>();
        try
        {
            foreach (var format in data.Formats)
            {
                uint id = format.Name == "HTML Format" ? Html : format.Id;
                if (id == 0 || (id != Html && id is not (8 or 13))) continue;
                nint block = GlobalAlloc(0x42, (nuint)Math.Max(1, format.Data.Length));
                if (block == 0) throw new OutOfMemoryException();
                blocks.Add((id, block));
                nint ptr = GlobalLock(block);
                if (ptr == 0) throw new IOException();
                try { format.Data.Span.CopyTo(new Span<byte>((void*)ptr, format.Data.Length)); }
                finally { GlobalUnlock(block); }
            }
            if (EmptyClipboard() == 0) throw new IOException();
            for (int i = 0; i < blocks.Count; i++)
            {
                var (id, handle) = blocks[i];
                if (SetClipboardData(id, handle) == 0) throw new IOException();
                blocks[i] = (id, 0); // Windows owns this allocation now.
            }
        }
        finally { foreach (var block in blocks) if (block.Handle != 0) GlobalFree(block.Handle); }
    }

    public void Dispose()
    {
        disposed = true;
        Clipboard.ContentChanged -= OnHostChanged;
        channel.ClipboardAvailabilityChanged -= OnAvailability;
        channel.ClipboardReceived -= OnGuestChanged;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("user32.dll")] private static partial int OpenClipboard(nint window);
    [LibraryImport("user32.dll")] private static partial int CloseClipboard();
    [LibraryImport("user32.dll")] private static partial int EmptyClipboard();
    [LibraryImport("user32.dll")] private static partial int CountClipboardFormats();
    [LibraryImport("user32.dll")] private static partial int IsClipboardFormatAvailable(uint format);
    [LibraryImport("user32.dll")] private static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] private static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll")] private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("kernel32.dll")] private static partial nuint GlobalSize(nint memory);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")] private static partial int GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalFree(nint memory);
}
