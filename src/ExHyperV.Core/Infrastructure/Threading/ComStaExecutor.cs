using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

// One STA and its native message pump, without a WPF dispatcher.
// ComApi creates it on first use and owns it for the process lifetime. Individual
// operations release their COM objects; finishing one must not stop the shared queue.
internal sealed class ComStaExecutor
{
    private const uint WorkMessage = 0x8001;
    private readonly ConcurrentQueue<Action> work = new();
    private readonly TaskCompletionSource<uint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;

    public ComStaExecutor()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "ExHyperV COM STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public T Invoke<T>(Func<T> action)
    {
        if (Thread.CurrentThread == thread) return action();
        uint id = ready.Task.GetAwaiter().GetResult();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        int state = 0;
        work.Enqueue(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { result.TrySetResult(action()); }
            catch (Exception error) { result.TrySetException(error); }
        });
        if (!PostThreadMessage(id, WorkMessage, 0, 0) && Interlocked.CompareExchange(ref state, 2, 0) == 0)
            result.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
        return result.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        // Force creation of the thread message queue before accepting work.
        PeekMessage(out _, 0, 0, 0, 0);
        ready.SetResult(GetCurrentThreadId());
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (message.Id == WorkMessage)
            {
                while (work.TryDequeue(out var action)) action();
            }
            else
            {
                TranslateMessage(in message);
                DispatchMessage(in message);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint id, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(in Message message);
}
