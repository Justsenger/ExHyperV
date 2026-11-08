using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services;

/// <summary>协调使用同一资源键的 Core 调用者；不阻止未遵循此协议的外部工具。</summary>
internal static class HostResourceLock
{
    private const uint SynchronizeAndModify = 0x00100001;
    private static readonly AsyncLocal<HeldScope?> CurrentScope = new();

    internal static Task<T> RunAsync<T>(string resource, Func<Task<T>> action,
        CancellationToken cancellationToken = default) =>
        RunAsync(new[] { resource }, action, cancellationToken);

    /// <summary>按资源键顺序取得全部锁，持有到异步业务及其恢复完成。</summary>
    /// <remarks>资源键必须由调用方规范化。嵌套取得新资源只能按升序进行；同资源重入明确失败，不能凭继承的执行上下文放行并行子任务。</remarks>
    internal static Task<T> RunAsync<T>(IEnumerable<string> resources, Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(action);
        string[] keys = resources.Select(key =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return key;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) throw new ArgumentException("至少需要一个宿主资源键。", nameof(resources));
        ValidateOrder(keys);

        // Windows mutex 归属线程。等待、持有及释放均在同一专用线程完成，
        // 异步 action 可以换线程，但不能把 ReleaseMutex 放到其 continuation 上。
        return Task.Factory.StartNew(() => RunOwned(keys, action, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static T RunOwned<T>(string[] keys, Func<Task<T>> action, CancellationToken token)
    {
        var handles = new List<(NativeMutex Mutex, bool Owned)>();
        var previous = CurrentScope.Value;
        HeldScope? scope = null;
        Exception? failure = null;
        List<Exception>? cleanupFailures = null;
        T result = default!;
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateOrder(keys);
            foreach (string key in keys)
            {
                var mutex = OpenMutex(key);
                handles.Add((mutex, false));
                try
                {
                    if (token.CanBeCanceled)
                    {
                        if (WaitHandle.WaitAny(new WaitHandle[] { mutex, token.WaitHandle }) != 0)
                            token.ThrowIfCancellationRequested();
                    }
                    else mutex.WaitOne();
                    handles[^1] = (mutex, true);
                }
                catch (AbandonedMutexException error)
                {
                    // Wait 已经转移所有权，仍需在本线程释放；本次不继续修改资源。
                    handles[^1] = (mutex, true);
                    throw new AbandonedMutexException("前一资源操作异常结束，无法确认其恢复状态；请核对后再执行。", error);
                }
                token.ThrowIfCancellationRequested();
            }

            scope = new HeldScope(keys, previous);
            CurrentScope.Value = scope;
            result = action().GetAwaiter().GetResult();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (scope != null) Volatile.Write(ref scope.Active, false);
            CurrentScope.Value = previous;
            for (int i = handles.Count - 1; i >= 0; i--)
            {
                var entry = handles[i];
                try
                {
                    if (entry.Owned && !ReleaseMutex(entry.Mutex.SafeWaitHandle))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "释放宿主资源互斥量失败。");
                }
                catch (Exception error) { (cleanupFailures ??= new()).Add(error); }
                finally { entry.Mutex.Dispose(); }
            }
        }

        if (cleanupFailures != null)
        {
            if (failure != null) cleanupFailures.Insert(0, failure);
            throw new AggregateException("宿主资源操作或互斥量释放失败。", cleanupFailures);
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static void ValidateOrder(string[] keys)
    {
        for (HeldScope? scope = CurrentScope.Value; scope != null; scope = scope.Parent)
        {
            if (!Volatile.Read(ref scope.Active)) continue;
            if (StringComparer.Ordinal.Compare(keys[0], scope.Keys[^1]) <= 0)
                throw new InvalidOperationException("宿主资源锁不能重复或逆序嵌套；应预先一次取得资源集合，或调用已持锁的内部实现。");
        }
    }

    internal static string GetMutexName(string resource) =>
        "Global\\ExHyperV.Core.Resource.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)));

    /// <summary>供锁内实现检查当前逻辑操作仍持有指定资源；不会创建或重入任何锁。</summary>
    internal static void AssertHeld(string resource)
    {
        for (HeldScope? scope = CurrentScope.Value; scope != null; scope = scope.Parent)
            if (Volatile.Read(ref scope.Active) && Array.BinarySearch(scope.Keys, resource, StringComparer.Ordinal) >= 0)
                return;
        throw new InvalidOperationException("此内部操作要求先取得相应宿主资源锁。");
    }

    private static NativeMutex OpenMutex(string resource)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        string sid = identity.User?.Value ?? throw new UnauthorizedAccessException("无法读取当前 Windows 用户身份。");
        // Global 名称跨登录会话一致；系统服务、管理员及创建用户可等待和释放。
        // 不把用户 SID、PID 或程序集版本加入名称，否则不同调用者会拿到不同的锁。
        string sddl = $"D:P(A;;0x00100001;;;SY)(A;;0x00100001;;;BA)(A;;0x00100001;;;{sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out nint descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建宿主资源互斥量的权限描述失败。");
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor
            };
            SafeWaitHandle handle = CreateMutexExW(ref attributes, GetMutexName(resource), 0, SynchronizeAndModify);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "无法取得宿主共享资源锁；未退回进程内锁。");
            }
            return new NativeMutex(handle);
        }
        finally { LocalFree(descriptor); }
    }

    private sealed class NativeMutex : WaitHandle
    {
        // 不创建额外的匿名 mutex；由 SafeWaitHandle 独占关闭这个原生句柄。
        internal NativeMutex(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private sealed class HeldScope(string[] keys, HeldScope? parent)
    {
        internal readonly string[] Keys = keys;
        internal readonly HeldScope? Parent = parent;
        internal bool Active = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal nint SecurityDescriptor;
        internal int InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor,
        uint revision, out nint securityDescriptor, out uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexExW(ref SecurityAttributes attributes, string name,
        uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(SafeWaitHandle handle);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
