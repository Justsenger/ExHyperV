using System.IO;
using ExHyperV.Api;

namespace ExHyperV.Services
{
    /// <summary>
    /// 切换系统产品类型（WinNT 工作站 / ServerNT 服务器）。
    /// 实现方式：离线修改 SYSTEM 注册表 Hive 中的 ProductType 值，需重启生效。
    /// 编排顺序：privilege → save → load offline → edit → unload → replace。
    /// </summary>
    public static class SystemTypeService
    {
        private const string TempDir = @"C:\temp";
        private const string HiveFile = @"C:\temp\sys_mod_exec.hiv";
        private const string BackupFile = @"C:\temp\sys_bak_exec.hiv";
        private const string TempKeyName = "TEMP_OFFLINE_SYS_MOD";

        /// <summary>
        /// 应用系统类型切换。
        /// </summary>
        /// <param name="toServer">true=切到 ServerNT；false=切到 WinNT</param>
        /// <returns>"SUCCESS" 表示已写入待生效（需重启）；其他字符串为本地化错误信息。</returns>
        public static string ApplySwitch(bool toServer)
        {
            try
            {
                if (!Directory.Exists(TempDir)) Directory.CreateDirectory(TempDir);

                // 删旧的工作 hive；删不掉视为上次替换尚未完成，按成功返回（调用方此前应已经过 HasPendingTask 拦截）
                try { if (File.Exists(HiveFile)) File.Delete(HiveFile); }
                catch { return "SUCCESS"; }
                try { if (File.Exists(BackupFile)) File.Delete(BackupFile); } catch { }

                if (!Win32Api.EnablePrivilege("SeBackupPrivilege").Success ||
                    !Win32Api.EnablePrivilege("SeRestorePrivilege").Success)
                    return Properties.Resources.SystemSwitcher_ErrInsufficientPermissions;

                var saveResp = Win32Api.SaveHive("SYSTEM", HiveFile);
                if (!saveResp.Success)
                    return string.Format(Properties.Resources.SystemSwitcher_ErrExportFailed, saveResp.Code);

                string targetType = toServer ? "ServerNT" : "WinNT";
                if (!PatchHiveOffline(HiveFile, targetType))
                    return Properties.Resources.SystemSwitcher_ErrOfflineModFailed;

                var replaceResp = Win32Api.ReplaceHive("SYSTEM", HiveFile, BackupFile);
                return replaceResp.Success
                    ? "SUCCESS"
                    : string.Format(Properties.Resources.SystemSwitcher_ErrReplaceFailed, replaceResp.Code);
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// 检查是否有未完成的系统类型切换任务（备份文件被系统占用 = 待重启生效）。
        /// </summary>
        public static bool HasPendingTask()
        {
            if (!File.Exists(BackupFile)) return false;
            try { File.Delete(BackupFile); return false; }
            catch { return true; }
        }

        // ----------------------------------------------------------------------------------
        // 离线 Hive 编辑：把 hivePath 文件加载到 HKLM 临时键下，改 ProductType，再卸载
        // ----------------------------------------------------------------------------------

        private static bool PatchHiveOffline(string hivePath, string targetType)
        {
            if (!Win32Api.LoadHive(TempKeyName, hivePath).Success) return false;

            try
            {
                // 1. 读 Select\Current（指示当前 ControlSet 编号；读失败回退 1）
                int currentSet = 1;
                var selectResp = Win32Api.GetHiveDwordValue($"{TempKeyName}\\Select", "Current");
                if (selectResp.HasData) currentSet = selectResp.Data;

                // 2. 优先写当前 ControlSet 下的 ProductType；不行则回退到 ControlSet001
                string setPath = $"{TempKeyName}\\ControlSet{currentSet:D3}\\Control\\ProductOptions";
                var setResp = Win32Api.SetHiveStringValue(setPath, "ProductType", targetType);
                if (!setResp.Success)
                {
                    setPath = $"{TempKeyName}\\ControlSet001\\Control\\ProductOptions";
                    setResp = Win32Api.SetHiveStringValue(setPath, "ProductType", targetType);
                }

                return setResp.Success;
            }
            finally
            {
                Win32Api.UnloadHive(TempKeyName);
            }
        }
    }
}
