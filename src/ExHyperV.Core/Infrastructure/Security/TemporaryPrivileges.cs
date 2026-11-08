using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Tools;

/// <summary>在当前有效身份的独立令牌副本上启用特权，限定于一次同步操作。</summary>
/// <remarks>
/// 不修改调用线程或进程的令牌，不申请新的管理员身份。
/// 操作在不继承执行上下文的一次性线程上完成；即使撤销模拟失败，也等待该线程结束后才返回。
/// 回调必须同步完成全部工作，不得启动逃离此范围的任务或返回尚未完成的异步操作。
/// </remarks>
internal static class TemporaryPrivileges
{
    internal static T Run<T>(IReadOnlyList<string> names, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(action);

        // 先固定输入，避免在工作线程上读取调用方可变的集合。
        var privileges = new string[names.Count];
        for (int i = 0; i < privileges.Length; i++)
        {
            string name = names[i];
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("特权名称不能为空。", nameof(names));
            privileges[i] = name;
        }

        using SafeAccessTokenHandle token = DuplicateEffectiveToken();
        foreach (string name in privileges)
            EnablePrivilege(token, name);

        T result = default!;
        ExceptionDispatchInfo? operationError = null;
        ExceptionDispatchInfo? cleanupError = null;
        var thread = new Thread(() =>
        {
            bool attached = false;
            try
            {
                if (!Native.SetThreadToken(nint.Zero, token))
                    throw NativeError("无法为临时操作设置线程身份");
                attached = true;
                result = action();
            }
            catch (Exception error)
            {
                operationError = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                if (attached)
                {
                    try
                    {
                        if (!Native.RevertToSelf())
                            throw NativeError("临时操作结束后无法撤销线程身份；该专用线程将结束");
                    }
                    catch (Exception error)
                    {
                        cleanupError = ExceptionDispatchInfo.Capture(error);
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "ExHyperV temporary privileges"
        };

        // 不让调用者的模拟身份、AsyncLocal 或其它执行上下文自动进入专用线程。
        // 当前有效身份已经由上面的显式令牌副本保存。
        if (ExecutionContext.IsFlowSuppressed())
            thread.Start();
        else
        {
            using (ExecutionContext.SuppressFlow())
                thread.Start();
        }

        ExceptionDispatchInfo? interrupted = null;
        while (true)
        {
            try
            {
                thread.Join();
                break;
            }
            catch (ThreadInterruptedException error)
            {
                // 不能提前离开 using 并关闭工作线程仍在使用的令牌，也不能把在途业务误作取消。
                interrupted ??= ExceptionDispatchInfo.Capture(error);
            }
        }

        var failures = new List<ExceptionDispatchInfo>(3);
        if (operationError != null) failures.Add(operationError);
        if (cleanupError != null) failures.Add(cleanupError);
        if (interrupted != null) failures.Add(interrupted);
        if (failures.Count == 1) failures[0].Throw();
        if (failures.Count > 1)
        {
            var errors = new Exception[failures.Count];
            for (int i = 0; i < errors.Length; i++) errors[i] = failures[i].SourceException;
            throw new AggregateException("临时特权操作或收尾出现多项错误。", errors);
        }

        return result;
    }

    private static SafeAccessTokenHandle DuplicateEffectiveToken()
    {
        const uint readAndDuplicate = Native.TokenQuery | Native.TokenDuplicate;
        if (!Native.OpenThreadToken(Native.GetCurrentThread(), readAndDuplicate, true, out var source))
        {
            int error = Marshal.GetLastWin32Error();
            source?.Dispose();
            // 已有模拟身份却无权打开时必须失败，不能退回权限可能更高的进程身份。
            if (error != Native.ErrorNoToken)
                throw new Win32Exception(error, "无法读取当前线程身份。");
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), readAndDuplicate, out source))
            {
                error = Marshal.GetLastWin32Error();
                source?.Dispose();
                throw new Win32Exception(error, "无法读取当前进程身份。");
            }
        }

        using (source)
        {
            if (!Native.DuplicateTokenEx(source,
                Native.TokenQuery | Native.TokenAdjustPrivileges | Native.TokenImpersonate,
                nint.Zero, Native.SecurityImpersonation, Native.TokenImpersonation, out var duplicate))
            {
                int error = Marshal.GetLastWin32Error();
                duplicate?.Dispose();
                throw new Win32Exception(error, "无法复制当前有效身份。");
            }
            return duplicate;
        }
    }

    private static void EnablePrivilege(SafeAccessTokenHandle token, string name)
    {
        if (!Native.LookupPrivilegeValue(null, name, out var luid))
            throw NativeError($"无法查找特权 {name}");

        var state = new Native.TokenPrivileges
        {
            Count = 1,
            Luid = luid,
            Attributes = Native.PrivilegeEnabled
        };
        bool adjusted = Native.AdjustTokenPrivileges(token, false, ref state, 0, nint.Zero, nint.Zero);
        int error = Marshal.GetLastWin32Error();
        if (!adjusted || error != 0)
            throw new Win32Exception(error, error == Native.ErrorNotAllAssigned
                ? $"当前有效身份未持有特权 {name}。"
                : $"无法在临时令牌上启用特权 {name}。");
    }

    private static Win32Exception NativeError(string message)
        => new(Marshal.GetLastWin32Error(), message);

    private static class Native
    {
        internal const uint TokenDuplicate = 0x0002;
        internal const uint TokenImpersonate = 0x0004;
        internal const uint TokenQuery = 0x0008;
        internal const uint TokenAdjustPrivileges = 0x0020;
        internal const uint PrivilegeEnabled = 0x0002;
        internal const int SecurityImpersonation = 2;
        internal const int TokenImpersonation = 2;
        internal const int ErrorNoToken = 1008;
        internal const int ErrorNotAllAssigned = 1300;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid
        {
            internal uint LowPart;
            internal int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TokenPrivileges
        {
            internal uint Count;
            internal Luid Luid;
            internal uint Attributes;
        }

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentThread();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenThreadToken(nint thread, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateTokenEx(SafeAccessTokenHandle source, uint access,
            nint attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state,
            uint bufferLength, nint previousState, nint returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetThreadToken(nint thread, SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RevertToSelf();
    }
}
