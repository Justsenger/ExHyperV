using System.Text.RegularExpressions;
namespace ExHyperV.Services
{
    /// <summary>
    /// 内部映射器：负责将 WMI/PS 的原始数据转换为 UI 易读数据
    /// </summary>
    internal static class VmMapper
    {
        public static string ParseOsTypeFromNotes(string notes)
        {
            if (string.IsNullOrEmpty(notes)) return "windows";
            var match = Regex.Match(notes, @"\[OSType:([^\]]+)\]", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim().ToLower();
            if (notes.Contains("linux", StringComparison.OrdinalIgnoreCase)) return "linux";
            return "windows";
        }

        public static string ParseNotes(object notesObj)
        {
            if (notesObj is string[] arr) return string.Join("\n", arr);
            return notesObj?.ToString() ?? "";
        }

        public static bool IsRunning(ushort code) => code == 2;

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