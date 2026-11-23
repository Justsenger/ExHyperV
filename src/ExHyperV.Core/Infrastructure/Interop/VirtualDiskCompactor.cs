using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using ExHyperV.Models;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Tools;

/// <summary>通过 VirtDisk 原生接口执行两阶段虚拟磁盘压缩（静默只读挂载感知文件系统 + 脱机全零块扫描）。</summary>
internal static class VirtualDiskCompactor
{
    private const uint VirtualDiskAccessAttachRo = 0x00010000;
    private const uint VirtualDiskAccessDetach = 0x00040000;
    private const uint VirtualDiskAccessGetInfo = 0x00080000;
    private const uint VirtualDiskAccessMetaOps = 0x00200000;

    private const uint AttachReadOnly = 0x00000001;
    private const uint AttachNoDriveLetter = 0x00000002;

    internal const uint ErrorSuccess = 0;
    internal const uint ErrorFileNotFound = 2;
    internal const uint ErrorPathNotFound = 3;
    internal const uint ErrorAccessDenied = 5;
    internal const uint ErrorSharingViolation = 32;
    internal const uint ErrorLockViolation = 33;
    internal const uint ErrorIoPending = 997;

    private static readonly int[] SharingRetryDelaysMs = [80, 150, 250, 400, 600];
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    internal readonly record struct StageOutcome(bool Completed, bool WasAlreadyAttached, uint ErrorCode, string StageName)
    {
        internal static StageOutcome Ok(bool wasAlreadyAttached = false) => new(true, wasAlreadyAttached, ErrorSuccess, string.Empty);
        internal static StageOutcome Fail(uint errorCode, string stageName) => new(false, false, errorCode, stageName);
    }

    /// <summary>对指定虚拟磁盘执行两阶段空间压缩；调用方应先校验路径存在与电源状态。</summary>
    internal static ApiResponse Compact(string vhdPath)
    {
        if (string.IsNullOrWhiteSpace(vhdPath))
            return ApiResponse.Fail("请提供虚拟磁盘文件路径。", kind: ApiErrorKind.InvalidArgument);

        string fullPath;
        try { fullPath = Path.GetFullPath(vhdPath); }
        catch (Exception ex)
        {
            return ApiResponse.Fail("虚拟磁盘路径无效：" + ex.Message, exception: ex, kind: ApiErrorKind.InvalidArgument);
        }

        return ExecuteCompactionFlow(
            () => RunFileSystemAwarePass(fullPath),
            waitForDetach => RunOfflinePassWithRetry(fullPath, waitForDetach));
    }

    internal static ApiResponse ExecuteCompactionFlow(
        Func<StageOutcome> runFileSystemAwarePass,
        Func<bool, StageOutcome> runOfflinePass)
    {
        var stage1 = runFileSystemAwarePass();
        if (stage1.ErrorCode == ErrorIoPending)
            return WmiJobWaiter.Unknown("压缩阶段仍在进行。", (int)ErrorIoPending, ApiErrorSource.Win32);
        if (stage1.StageName is "Probe" or "Detach")
            return ApiResponse.Fail($"虚拟磁盘 {stage1.StageName} 未确认完成（{stage1.ErrorCode}）。",
                unchecked((int)stage1.ErrorCode), ApiErrorSource.Win32,
                kind: stage1.StageName == "Detach" ? ApiErrorKind.CleanupRequired : ApiErrorKind.OperationFailed);
        if (IsFatalOpenLockOrMissing(stage1.ErrorCode) && string.Equals(stage1.StageName, "Open", StringComparison.Ordinal))
            return NativeFailure(stage1.ErrorCode, "打开虚拟磁盘失败");

        // 若宿主此前已自行挂载，Stage 1 直接在已挂载状态下完成了感知压缩，
        // 避免脱机 Stage 2 与宿主现有挂载产生共享冲突。
        if (stage1.WasAlreadyAttached)
        {
            return stage1.Completed
                ? ApiResponse.Ok()
                : NativeFailure(stage1.ErrorCode, "压缩已挂载虚拟磁盘失败");
        }

        // Stage 1（只读静默挂载 + FS 感知）在加密卷、Linux 分区或无 NTFS 卷上可能无法挂载/感知，
        // 只要磁盘未锁定，一律平滑降级由 Stage 2 脱机全零块扫描继续压缩。
        var stage2 = runOfflinePass(stage1.Completed || stage1.StageName == "Compact");
        if (stage2.ErrorCode == ErrorIoPending)
            return WmiJobWaiter.Unknown("脱机压缩尚未结束。", (int)ErrorIoPending, ApiErrorSource.Win32);
        if (stage2.Completed)
            return ApiResponse.Ok();

        // 若 Stage 2 返回成功则已放行；两者均失败时优先报告更具体的阶段错误。
        return NativeFailure(stage2.ErrorCode, "压缩虚拟磁盘失败");
    }

    internal static bool IsFatalOpenLockOrMissing(uint code)
        => code is ErrorFileNotFound or ErrorPathNotFound or ErrorAccessDenied or ErrorSharingViolation or ErrorLockViolation;

    private static StageOutcome RunFileSystemAwarePass(string path)
    {
        uint openCode = OpenForCompact(
            path,
            VirtualDiskAccessMetaOps | VirtualDiskAccessAttachRo | VirtualDiskAccessDetach | VirtualDiskAccessGetInfo,
            out SafeFileHandle? handle);
        if (openCode != ErrorSuccess || handle == null)
            return StageOutcome.Fail(openCode == ErrorSuccess ? ErrorAccessDenied : openCode, "Open");

        using (handle)
        {
            var info = new AttachmentInfo { Version = 13 }; // GET_VIRTUAL_DISK_INFO_IS_LOADED
            uint size = (uint)Marshal.SizeOf<AttachmentInfo>();
            uint probe = Native.GetVirtualDiskInformation(handle, ref size, ref info, out _);
            if (probe != ErrorSuccess) return StageOutcome.Fail(probe, "Probe");
            bool attachedBefore = info.Loaded != 0;
            if (!attachedBefore)
            {
                var attachParams = new AttachParameters { Version = 1 };
                uint attachCode = Native.AttachVirtualDisk(
                    handle,
                    nint.Zero,
                    AttachReadOnly | AttachNoDriveLetter,
                    0,
                    ref attachParams,
                    nint.Zero);
                if (attachCode != ErrorSuccess)
                    return StageOutcome.Fail(attachCode, "Attach");
            }

            uint detachCode = ErrorSuccess;
            uint compactCode = ErrorIoPending;
            try
            {
                var compactParams = new CompactParameters { Version = 1 };
                compactCode = Native.CompactVirtualDisk(handle, 0, ref compactParams, nint.Zero);
            }
            finally
            {
                if (!attachedBefore && compactCode != ErrorIoPending) detachCode = Native.DetachVirtualDisk(handle, 0, nint.Zero);
            }
            if (detachCode != ErrorSuccess) return StageOutcome.Fail(detachCode, "Detach");
            return compactCode == ErrorSuccess ? StageOutcome.Ok(attachedBefore)
                : new StageOutcome(false, attachedBefore, compactCode, "Compact");
        }
    }

    private static StageOutcome RunOfflinePassWithRetry(string path, bool retrySharingAfterDetach)
    {
        int maxRetries = retrySharingAfterDetach ? SharingRetryDelaysMs.Length : 0;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            uint openCode = OpenForCompact(path, VirtualDiskAccessMetaOps, out SafeFileHandle? handle);
            if (openCode != ErrorSuccess || handle == null)
            {
                uint err = openCode == ErrorSuccess ? ErrorAccessDenied : openCode;
                if (attempt < maxRetries && err is ErrorSharingViolation or ErrorLockViolation)
                {
                    Thread.Sleep(SharingRetryDelaysMs[attempt]);
                    continue;
                }
                return StageOutcome.Fail(err, "Open");
            }

            using (handle)
            {
                var compactParams = new CompactParameters { Version = 1 };
                uint compactCode = Native.CompactVirtualDisk(handle, 0, ref compactParams, nint.Zero);
                if (compactCode == ErrorSuccess)
                    return StageOutcome.Ok();
                if (attempt < maxRetries && compactCode is ErrorSharingViolation or ErrorLockViolation)
                {
                    Thread.Sleep(SharingRetryDelaysMs[attempt]);
                    continue;
                }
                return StageOutcome.Fail(compactCode, "Compact");
            }
        }
        return StageOutcome.Fail(ErrorSharingViolation, "Compact");
    }

    private static uint OpenForCompact(string path, uint accessMask, out SafeFileHandle? handle)
    {
        var storage = new VirtualStorageType { VendorId = MicrosoftVendor };
        var parameters = new OpenParameters { Version = 1, RWDepth = 1 };
        uint code = Native.OpenVirtualDisk(ref storage, path, accessMask, 0, ref parameters, out nint rawHandle);
        if (code != ErrorSuccess)
        {
            handle = null;
            return code;
        }

        var safe = new SafeFileHandle(rawHandle, ownsHandle: true);
        if (safe.IsInvalid)
        {
            safe.Dispose();
            handle = null;
            return ErrorAccessDenied;
        }

        handle = safe;
        return ErrorSuccess;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct AttachmentInfo
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(8)] internal int Loaded;
    }

    private static ApiResponse NativeFailure(uint code, string operation)
    {
        int signed = unchecked((int)code);
        var kind = code switch
        {
            ErrorFileNotFound or ErrorPathNotFound => ApiErrorKind.NotFound,
            ErrorSharingViolation or ErrorLockViolation => ApiErrorKind.InvalidState,
            ErrorAccessDenied => ApiErrorKind.InvalidState,
            _ => ApiErrorKind.OperationFailed
        };
        return ApiResponse.Fail(
            $"{operation}（0x{code:X8}）：{new Win32Exception(signed).Message}",
            signed,
            ApiErrorSource.Win32,
            kind: kind);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        internal uint DeviceId;
        internal Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenParameters
    {
        internal uint Version;
        internal uint RWDepth;
        internal int Reserved;
        internal Guid ResiliencyGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttachParameters
    {
        internal uint Version;
        internal uint Reserved;
        internal ulong RestrictedOffset;
        internal ulong RestrictedLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompactParameters
    {
        internal uint Version;
        internal uint Reserved;
    }

    private static class Native
    {
        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint OpenVirtualDisk(
            ref VirtualStorageType storageType,
            string path,
            uint accessMask,
            uint flags,
            ref OpenParameters parameters,
            out nint handle);

        [DllImport("virtdisk.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint AttachVirtualDisk(
            SafeFileHandle handle,
            nint securityDescriptor,
            uint flags,
            uint providerSpecificFlags,
            ref AttachParameters parameters,
            nint overlapped);

        [DllImport("virtdisk.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint CompactVirtualDisk(
            SafeFileHandle handle,
            uint flags,
            ref CompactParameters parameters,
            nint overlapped);

        [DllImport("virtdisk.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint DetachVirtualDisk(
            SafeFileHandle handle,
            uint flags,
            nint providerSpecificFlags);

        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetVirtualDiskInformation(SafeFileHandle handle, ref uint size,
            ref AttachmentInfo info, out uint sizeUsed);
    }
}
