namespace ExHyperV.Tools
{
    /// <summary>把 WMI 状态值转换为 Core 使用的文本和状态分类。</summary>
    internal static class VmMapper
    {
        internal static Models.VmState MapState(ushort code) => code switch
        {
            1 => Models.VmState.Other,
            2 => Models.VmState.Running,
            3 => Models.VmState.PoweredOff,
            4 => Models.VmState.ShuttingDown,
            5 => Models.VmState.NotApplicable,
            6 or 32769 => Models.VmState.Saved,
            7 => Models.VmState.InTest,
            8 => Models.VmState.Deferred,
            9 or 32768 => Models.VmState.Suspended,
            10 or 32770 => Models.VmState.Starting,
            32771 => Models.VmState.WaitingToStart,
            32772 => Models.VmState.MergingDisks,
            32773 => Models.VmState.Saving,
            32774 => Models.VmState.Stopping,
            32775 => Models.VmState.Processing,
            32776 => Models.VmState.Suspending,
            32777 => Models.VmState.Resuming,
            32779 => Models.VmState.FastSaved,
            32780 => Models.VmState.FastSaving,
            32781 => Models.VmState.ForceShuttingDown,
            32782 => Models.VmState.ForceRestarting,
            32783 => Models.VmState.Hibernated,
            _ => Models.VmState.Unknown
        };

        internal static ushort ToStateCode(Models.VmState state) => state switch
        {
            Models.VmState.Unknown => 0,
            Models.VmState.Other => 1,
            Models.VmState.Running => 2,
            Models.VmState.PoweredOff => 3,
            Models.VmState.ShuttingDown => 4,
            Models.VmState.NotApplicable => 5,
            Models.VmState.Saved => 6,
            Models.VmState.InTest => 7,
            Models.VmState.Deferred => 8,
            Models.VmState.Suspended => 32768,
            Models.VmState.Starting => 32770,
            Models.VmState.WaitingToStart => 32771,
            Models.VmState.MergingDisks => 32772,
            Models.VmState.Saving => 32773,
            Models.VmState.Stopping => 32774,
            Models.VmState.Processing => 32775,
            Models.VmState.Suspending => 32776,
            Models.VmState.Resuming => 32777,
            Models.VmState.FastSaved => 32779,
            Models.VmState.FastSaving => 32780,
            Models.VmState.ForceShuttingDown => 32781,
            Models.VmState.ForceRestarting => 32782,
            Models.VmState.Hibernated => 32783,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };

        /// <summary>只判断 WMI EnabledState 是否等于 Running。</summary>
        /// <returns>状态码为 2 时为 true。</returns>
        /// <param name="code">WMI 虚拟机 EnabledState 代码。</param>
        public static bool IsRunning(ushort code) => code == 2;

        // VM 是否处于"活动"状态：已开机、CPU 仍在执行（含启动/关闭/保存/挂起/恢复等过渡态）。
        // 用于 UI 判定亮监控/计时、Service 过滤可取性能数据的 VM。
        // 关键：合并磁盘(32772)/等待启动(32771)/已休眠(32783)/未知(0) 等均非活动——避免关机态被误判为运行。
        /// <summary>判断 VM 是否运行或处于本应用认可的运行相关过渡态。</summary>
        /// <returns>属于运行、启动、停止、保存、挂起/恢复等选定过渡态时为 true；不等同 IsRunning。</returns>
        /// <param name="code">WMI 虚拟机 EnabledState 代码。</param>
        public static bool IsActiveState(ushort code) => code switch
        {
            2 or 4 or 10 or 32770 or 32773 or 32774 or 32776 or 32777 or 32780 or 32781 or 32782 => true,
            _ => false
        };

        /// <summary>把 WMI 虚拟机状态码转换为当前资源语言的显示文本。</summary>
        /// <returns>本地化状态说明，未知码保留其数值；不应用显示文本反推业务状态。</returns>
        /// <param name="code">WMI 虚拟机 EnabledState 代码。</param>
        public static string MapStateCodeToText(ushort code)
        {
            return code switch
            {
                0 => Properties.Resources.Status_Unknown,
                1 => Properties.Resources.Status_Other,
                2 => Properties.Resources.Status_Running,
                3 => Properties.Resources.Status_Off,
                4 => Properties.Resources.Status_ShuttingDown,
                5 => Properties.Resources.Status_NotApplicable,
                6 => Properties.Resources.Status_Saved,
                7 => Properties.Resources.Status_InTest,
                8 => Properties.Resources.Status_Deferred,
                9 => Properties.Resources.Status_Suspended,
                10 => Properties.Resources.Status_Starting,
                32768 => Properties.Resources.Status_Suspended,
                32769 => Properties.Resources.Status_Saved,
                32770 => Properties.Resources.Status_Starting,
                32771 => Properties.Resources.Status_WaitingToStart,
                32772 => Properties.Resources.Status_MergingDisks,
                32773 => Properties.Resources.Status_Saving,
                32774 => Properties.Resources.Status_Stopping,
                32775 => Properties.Resources.Status_Processing,
                32776 => Properties.Resources.Status_Suspending,
                32777 => Properties.Resources.Status_Resuming,
                32779 => Properties.Resources.Status_FastSaved,
                32780 => Properties.Resources.Status_FastSaving,
                32781 => Properties.Resources.Status_ForceShutdown,
                32782 => Properties.Resources.Status_ForceReboot,
                32783 => Properties.Resources.Status_Hibernated,
                _ => string.Format(Properties.Resources.Status_UnknownCode, code)
            };
        }

    }
}
