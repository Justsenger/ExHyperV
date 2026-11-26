using System.Runtime.InteropServices;
using ExHyperV.App.Localization;
using ExHyperV.Services;
using Microsoft.UI.Dispatching;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

// CALLBACK_NULL keeps WinMM from calling managed code. Prepared WAVEHDRs and their PCM
// storage stay at stable addresses until the driver returns them.
internal sealed unsafe partial class ConsolePlayback : IDisposable
{
    private readonly IConsoleAudioOutput output;
    private readonly DispatcherQueue dispatcher;
    private readonly Action changed;
    private readonly Action<string> notify;
    private readonly Queue<nint> playing = new();
    private ConsoleAudioFormat? format;
    private nint device;
    private int enqueued, playingBytes;
    private bool muted, failed, disposed;

    internal ConsolePlayback(IConsoleAudioOutput output, DispatcherQueue dispatcher, bool muted,
        Action changed, Action<string> notify)
    {
        this.output = output;
        this.dispatcher = dispatcher;
        this.muted = muted;
        this.changed = changed;
        this.notify = notify;
        output.AvailabilityChanged += OnAvailability;
        output.AudioReceived += Receive;
    }

    internal bool Available => output.IsAvailable;

    private void OnAvailability(bool available) => dispatcher.TryEnqueue(() =>
    {
        if (disposed) return;
        if (!available) Reset();
        failed = false;
        changed();
    });

    private void Receive(ConsoleAudioPacket packet)
    {
        // Bound even the dispatcher backlog when the UI thread is occupied.
        if (Interlocked.Increment(ref enqueued) > 12) { Interlocked.Decrement(ref enqueued); return; }
        if (!dispatcher.TryEnqueue(() =>
        {
            Interlocked.Decrement(ref enqueued);
            if (disposed || muted || failed || !Available) return;
            try { Play(packet); }
            catch (Exception)
            {
                Reset();
                failed = true;
                notify(L10n.Get("Console_AudioFailed"));
            }
        })) Interlocked.Decrement(ref enqueued);
    }

    internal void SetMuted(bool value)
    {
        muted = value;
        failed = false;
        if (muted) Reset();
    }

    private void Play(ConsoleAudioPacket packet)
    {
        if (format != packet.Format)
        {
            Reset();
            bool floating = packet.Format.SampleFormat == ConsoleAudioSampleFormat.Float32LittleEndian;
            ushort align = checked((ushort)(packet.Format.Channels * (floating ? 4 : 2)));
            var descriptor = new WaveFormat
            {
                Tag = (ushort)(floating ? 3 : 1), Channels = (ushort)packet.Format.Channels,
                Rate = (uint)packet.Format.SampleRate, BytesPerSecond = (uint)(packet.Format.SampleRate * align),
                Alignment = align, Bits = (ushort)(floating ? 32 : 16)
            };
            Check(waveOutOpen(out device, uint.MaxValue, &descriptor, 0, 0, 0));
            format = packet.Format;
        }
        Reclaim();
        int length = packet.Samples.Length;
        int quarterSecond = format.SampleRate * format.Channels * (format.SampleFormat == ConsoleAudioSampleFormat.Float32LittleEndian ? 4 : 2) / 4;
        if (length == 0 || playingBytes > quarterSecond) return;
        var header = (WaveHeader*)NativeMemory.AllocZeroed((nuint)(sizeof(WaveHeader) + length));
        header->Data = (nint)(header + 1);
        header->Length = (uint)length;
        packet.Samples.CopyTo(new Span<byte>(header + 1, length));
        uint result = waveOutPrepareHeader(device, header, (uint)sizeof(WaveHeader));
        if (result != 0) { NativeMemory.Free(header); Check(result); }
        playing.Enqueue((nint)header);
        playingBytes += length;
        Check(waveOutWrite(device, header, (uint)sizeof(WaveHeader)));
    }

    private void Reclaim()
    {
        while (playing.TryPeek(out var address))
        {
            var header = (WaveHeader*)address;
            // WAVERR_STILLPLAYING means the device still owns this header.
            if (waveOutUnprepareHeader(device, header, (uint)sizeof(WaveHeader)) != 0) break;
            playing.Dequeue();
            playingBytes -= (int)header->Length;
            NativeMemory.Free(header);
        }
    }

    private void Reset()
    {
        if (device == 0) return;
        waveOutReset(device);
        Reclaim();
        if (waveOutClose(device) == 0) { device = 0; format = null; }
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new IOException(result.ToString());
    }

    public void Dispose()
    {
        disposed = true;
        output.AudioReceived -= Receive;
        output.AvailabilityChanged -= OnAvailability;
        Reset();
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

    [LibraryImport("winmm.dll")] private static partial uint waveOutOpen(out nint device, uint id, WaveFormat* format, nuint callback, nuint instance, uint flags);
    [LibraryImport("winmm.dll")] private static partial uint waveOutPrepareHeader(nint device, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll")] private static partial uint waveOutWrite(nint device, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll")] private static partial uint waveOutReset(nint device);
    [LibraryImport("winmm.dll")] private static partial uint waveOutUnprepareHeader(nint device, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll")] private static partial uint waveOutClose(nint device);
}
