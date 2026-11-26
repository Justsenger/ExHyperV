using System.Runtime.InteropServices;
using ExHyperV.App.Localization;
using ExHyperV.Services;
using Microsoft.UI.Dispatching;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

internal sealed unsafe partial class ConsoleCapture : IDisposable
{
    private const int BufferCount = 4;
    private const int MaxAudioPacketBytes = 1024 * 1024;
    private const uint WaveMapper = uint.MaxValue;
    private const uint CallbackEvent = 0x00050000;
    private const uint WhdrDone = 0x00000001;

    private readonly IConsoleAudioInput input;
    private readonly DispatcherQueue dispatcher;
    private readonly Action changed;
    private readonly Action<string> notify;
    private readonly Lock gate = new();

    private ActiveCapture? active;
    private bool disposed;

    internal ConsoleCapture(IConsoleAudioInput input, DispatcherQueue dispatcher, Action changed, Action<string> notify)
    {
        this.input = input;
        this.dispatcher = dispatcher;
        this.changed = changed;
        this.notify = notify;
        input.AvailabilityChanged += OnAvailability;
    }

    internal bool IsAvailable => !disposed && input.IsAvailable && input.Format != null;

    internal bool IsCapturing
    {
        get { lock (gate) return active != null && !disposed; }
    }

    internal void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            Stop();
            return;
        }

        ActiveCapture? previous = null;
        string? warning = null;
        lock (gate)
        {
            if (disposed || active != null) return;
            var format = input.Format;
            if (!input.IsAvailable || format == null)
            {
                warning = L10n.Get("Console_MicWaitingGuest");
            }
            else if (waveInGetNumDevs() == 0)
            {
                warning = L10n.Get("Console_MicUnavailable");
            }
            else
            {
                if (!TryStartCapture(format, out active))
                {
                    warning = L10n.Get("Console_MicFailed");
                }
            }
            previous = null;
        }
        previous?.Dispose();
        if (warning != null) notify(warning);
        changed();
    }

    internal void Stop()
    {
        ActiveCapture? existing;
        lock (gate)
        {
            existing = active;
            active = null;
        }
        existing?.Dispose();
        if (!disposed) dispatcher.TryEnqueue(() => { if (!disposed) changed(); });
    }

    private void OnAvailability(bool available)
    {
        ActiveCapture? toDispose = null;
        if (!available)
        {
            lock (gate)
            {
                toDispose = active;
                active = null;
            }
        }
        else
        {
            var currentFormat = input.Format;
            lock (gate)
            {
                if (active != null && currentFormat != null && active.Format != currentFormat)
                {
                    toDispose = active;
                    active = null;
                }
            }
        }
        toDispose?.Dispose();
        dispatcher.TryEnqueue(() => { if (!disposed) changed(); });
    }

    private bool TryStartCapture(ConsoleAudioFormat format, out ActiveCapture? started)
    {
        started = null;
        int sampleBytes = format.SampleFormat == ConsoleAudioSampleFormat.Float32LittleEndian ? 4 : 2;
        int align = format.Channels * sampleBytes;
        int framesPerBuffer = Math.Max(160, format.SampleRate / 50);
        int bufferBytes = Math.Min(MaxAudioPacketBytes, framesPerBuffer * align);
        bufferBytes -= bufferBytes % align;
        if (bufferBytes <= 0) return false;

        var bufferEvent = new AutoResetEvent(false);
        var stopEvent = new ManualResetEventSlim(false);
        nint device = 0;
        var headers = new nint[BufferCount];

        try
        {
            var descriptor = new WaveFormat
            {
                Tag = (ushort)(format.SampleFormat == ConsoleAudioSampleFormat.Float32LittleEndian ? 3 : 1),
                Channels = (ushort)format.Channels,
                Rate = (uint)format.SampleRate,
                BytesPerSecond = (uint)(format.SampleRate * align),
                Alignment = (ushort)align,
                Bits = (ushort)(sampleBytes * 8),
                ExtraSize = 0
            };

            if (waveInOpen(out device, WaveMapper, &descriptor,
                (nuint)bufferEvent.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent) != 0 || device == 0)
            {
                return false;
            }

            for (int i = 0; i < BufferCount; i++)
            {
                var header = (WaveHeader*)NativeMemory.AllocZeroed((nuint)sizeof(WaveHeader) + (nuint)bufferBytes);
                header->Data = (nint)(header + 1);
                header->Length = (uint)bufferBytes;
                headers[i] = (nint)header;

                if (waveInPrepareHeader(device, header, (uint)sizeof(WaveHeader)) != 0 ||
                    waveInAddBuffer(device, header, (uint)sizeof(WaveHeader)) != 0)
                {
                    return false;
                }
            }

            if (waveInStart(device) != 0) return false;

            var instance = new ActiveCapture(this, format, align, device, headers, bufferEvent, stopEvent);
            instance.StartThread();
            started = instance;
            return true;
        }
        finally
        {
            if (started == null)
            {
                if (device != 0)
                {
                    waveInReset(device);
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (headers[i] != 0)
                        {
                            waveInUnprepareHeader(device, (WaveHeader*)headers[i], (uint)sizeof(WaveHeader));
                            NativeMemory.Free((void*)headers[i]);
                        }
                    }
                    waveInClose(device);
                }
                else
                {
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (headers[i] != 0) NativeMemory.Free((void*)headers[i]);
                    }
                }
                stopEvent.Dispose();
                bufferEvent.Dispose();
            }
        }
    }

    public void Dispose()
    {
        ActiveCapture? existing;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            existing = active;
            active = null;
        }
        input.AvailabilityChanged -= OnAvailability;
        existing?.Dispose();
    }

    private sealed class ActiveCapture : IDisposable
    {
        private readonly ConsoleCapture owner;
        internal readonly ConsoleAudioFormat Format;
        private readonly int alignment;
        private readonly nint device;
        private readonly nint[] headers;
        private readonly AutoResetEvent bufferEvent;
        private readonly ManualResetEventSlim stopEvent;
        private Thread? worker;
        private int disposedFlag;

        internal ActiveCapture(ConsoleCapture owner, ConsoleAudioFormat format, int alignment, nint device,
            nint[] headers, AutoResetEvent bufferEvent, ManualResetEventSlim stopEvent)
        {
            this.owner = owner;
            Format = format;
            this.alignment = alignment;
            this.device = device;
            this.headers = headers;
            this.bufferEvent = bufferEvent;
            this.stopEvent = stopEvent;
        }

        internal void StartThread()
        {
            worker = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "ExHyperV Console Mic"
            };
            worker.Start();
        }

        private void RunLoop()
        {
            WaitHandle[] handles = [stopEvent.WaitHandle, bufferEvent];
            while (!stopEvent.IsSet)
            {
                int index = WaitHandle.WaitAny(handles);
                if (index == 0 || stopEvent.IsSet) break;

                for (int i = 0; i < headers.Length && !stopEvent.IsSet; i++)
                {
                    var header = (WaveHeader*)headers[i];
                    if (header == null || (header->Flags & WhdrDone) == 0) continue;

                    int recorded = (int)header->Recorded;
                    int validBytes = recorded - (recorded % alignment);
                    if (validBytes > 0 && owner.input.IsAvailable && owner.input.Format == Format)
                    {
                        owner.input.SendAudio(Format, new ReadOnlySpan<byte>((void*)header->Data, validBytes));
                    }

                    header->Recorded = 0;
                    if (!stopEvent.IsSet)
                    {
                        if (waveInAddBuffer(device, header, (uint)sizeof(WaveHeader)) != 0)
                        {
                            owner.dispatcher.TryEnqueue(() =>
                            {
                                owner.Stop();
                                if (!owner.disposed) owner.notify(L10n.Get("Console_MicFailed"));
                            });
                            return;
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposedFlag, 1) != 0) return;
            stopEvent.Set();
            waveInReset(device);
            worker?.Join(2000);
            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[i] != 0)
                {
                    waveInUnprepareHeader(device, (WaveHeader*)headers[i], (uint)sizeof(WaveHeader));
                    NativeMemory.Free((void*)headers[i]);
                    headers[i] = 0;
                }
            }
            waveInClose(device);
            stopEvent.Dispose();
            bufferEvent.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort Alignment, Bits, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint Length, Recorded;
        public nuint User;
        public uint Flags, Loops;
        public nint Next;
        public nuint Reserved;
    }

    [LibraryImport("winmm.dll")] private static partial uint waveInGetNumDevs();
    [LibraryImport("winmm.dll")] private static partial uint waveInOpen(out nint device, uint id, WaveFormat* format, nuint callback, nuint instance, uint flags);
    [LibraryImport("winmm.dll")] private static partial uint waveInPrepareHeader(nint device, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll")] private static partial uint waveInAddBuffer(nint device, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll")] private static partial uint waveInStart(nint device);
    [LibraryImport("winmm.dll")] private static partial uint waveInReset(nint device);
    [LibraryImport("winmm.dll")] private static partial uint waveInUnprepareHeader(nint device, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll")] private static partial uint waveInClose(nint device);
}
