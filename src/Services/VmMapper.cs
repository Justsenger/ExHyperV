using System;
using System.Text.RegularExpressions;

//映射工具

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
                2 => "运行中",       // Enabled
                3 => "已关机",       // Disabled
                6 => "已保存",       // Enabled but Offline
                9 => "已暂停",       // Quiesce
                32768 => "已暂停",   // Paused
                32769 => "已保存",   // Saved
                32770 => "正在启动",
                32771 => "正在快照",
                32773 => "正在保存",
                32774 => "正在停止",
                32776 => "正在暂停",
                32777 => "正在恢复",
                _ => $"未知状态({code})"
            };
        }
    }
}