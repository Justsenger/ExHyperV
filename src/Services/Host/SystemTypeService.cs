using System.IO;
using ExHyperV.Tools;
using Microsoft.Win32;

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
        // 易失键：替换成功后写入、重启自动消失——生命周期与内核挂起的 hive 替换一致，作挂起状态的可查真相源
        private const string PendingKeyPath = @"SOFTWARE\ExHyperV\PendingSystemTypeSwitch";

        /// <summary>
        /// 应用系统类型切换。
        /// </summary>
        /// <param name="toServer">true=切到 ServerNT；false=切到 WinNT</param>
        /// <returns>"SUCCESS"=已写入待生效（需重启）；"PENDING"=已有挂起任务、本次未做任何事；其他字符串为本地化错误信息。</returns>
        public static string ApplySwitch(bool toServer)
        {
            try
            {
                if (!Directory.Exists(TempDir)) Directory.CreateDirectory(TempDir);

                // 删旧工作 hive。删不掉=被占用（挂起替换持有或安全软件锁定）——挂起态已由调用方
                // GetPendingTarget 拦截，走到这里的占用是真异常，如实报错而不再伪装成功。
                try { if (File.Exists(HiveFile)) File.Delete(HiveFile); }
                catch { return Properties.Resources.SystemType_ErrWorkFileLocked; }
                try { if (File.Exists(BackupFile)) File.Delete(BackupFile); } catch { }

                if (!Win32Api.EnablePrivilege("SeBackupPrivilege").Success ||
                    !Win32Api.EnablePrivilege("SeRestorePrivilege").Success)
                    return Properties.Resources.SystemType_ErrInsufficientPermissions;

                var saveResp = Win32Api.SaveHive("SYSTEM", HiveFile);
                if (!saveResp.Success)
                    return string.Format(Properties.Resources.SystemType_ErrExportFailed, saveResp.Code);

                string targetType = toServer ? "ServerNT" : "WinNT";
                if (!PatchHiveOffline(HiveFile, targetType))
                    return Properties.Resources.SystemType_ErrOfflineModFailed;

                var replaceResp = Win32Api.ReplaceHive("SYSTEM", HiveFile, BackupFile);
                if (replaceResp.Success)
                {
                    MarkPending(targetType);
                    return "SUCCESS";
                }
                // ERROR_ACCESS_DENIED(5)=已有挂起的替换任务，本次替换未生效（初版即按 PENDING 处理，重构时语义曾丢失）
                if (replaceResp.Code == 5) return "PENDING";
                return string.Format(Properties.Resources.SystemType_ErrReplaceFailed, replaceResp.Code);
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// 查询挂起的切换任务：null=无；"ServerNT"/"WinNT"=有且已知目标；""=有但方向未知（外部工具所为或标记写入失败）。
        /// </summary>
        public static string? GetPendingTarget()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PendingKeyPath);
                if (key?.GetValue("Target") is string t && t.Length > 0) return t;
            }
            catch { }

            // 兜底：备份文件被内核锁定=有挂起替换。试独占打开、无副作用——
            // 旧的"试删"探针会把上次重启后遗留的备份尸体真删掉。
            if (!File.Exists(BackupFile)) return null;
            try
            {
                using var fs = new FileStream(BackupFile, FileMode.Open, FileAccess.Read, FileShare.None);
                return null;   // 能独占打开=遗留尸体而非挂起（尸体由下次 ApplySwitch 开头清理）
            }
            catch { return ""; }
        }

        /// <summary>
        /// 检查是否有未完成的系统类型切换任务（重启后自动消失）。
        /// </summary>
        public static bool HasPendingTask() => GetPendingTarget() != null;

        // 替换成功后立刻打易失标记（REG_OPTION_VOLATILE，重启自动蒸发）；写失败不致命，兜底走文件锁探测
        private static void MarkPending(string targetType)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    PendingKeyPath, true, RegistryOptions.Volatile);
                key?.SetValue("Target", targetType);
            }
            catch { }
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
