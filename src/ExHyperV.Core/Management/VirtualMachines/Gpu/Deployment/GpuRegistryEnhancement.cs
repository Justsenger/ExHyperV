using System.IO;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Services;

// 来宾注册表仅用于 GPU 环境增强；所有失败通过警告报告，不中断驱动主流程。
internal static class GpuRegistryEnhancement
{
    internal static void Apply(string hivePath, string mountKey,
        Action<RegistryKey> edit, Action<string> reportWarning)
    {
        void Warn(string stage, string details)
        {
            try
            {
                reportWarning($"[GPU 注册表增强警告] {stage}；临时键：HKLM\\{mountKey}；文件：{hivePath}；{details}");
            }
            catch { /* 警告回调不能阻断部署或本次 hive 的清理。 */ }
        }

        try
        {
            if (!File.Exists(hivePath))
            {
                Warn("未加载", "离线 SYSTEM 注册表文件不存在。");
                return;
            }

            // TemporaryPrivileges 不传递 AsyncLocal；写入材料和警告接收者由调用方显式捕获。
            TemporaryPrivileges.Run(["SeBackupPrivilege", "SeRestorePrivilege"], () =>
            {
                bool loaded = false;
                string stage = "加载失败";
                try
                {
                    var load = Win32Api.LoadHive(mountKey, hivePath);
                    if (!load.Success)
                    {
                        Warn(stage, $"{load.Error}（{load.ErrorSource}：{load.Code}）");
                        return 0;
                    }
                    loaded = true;
                    stage = "打开失败";
                    using (var root = Registry.LocalMachine.OpenSubKey(mountKey, writable: true)
                        ?? throw new IOException("无法打开已加载的离线注册表。"))
                    {
                        stage = "写入失败";
                        try { edit(root); }
                        catch (Exception error) { Warn(stage, error.Message); }
                        stage = "键句柄关闭失败";
                    }
                }
                catch (Exception error)
                {
                    Warn(stage, error.Message);
                }
                finally
                {
                    if (loaded)
                    {
                        try
                        {
                            var unload = Win32Api.UnloadHive(mountKey);
                            if (!unload.Success)
                                Warn("卸载失败，可能仍有残留占用", $"{unload.Error}（{unload.ErrorSource}：{unload.Code}）");
                        }
                        catch (Exception error)
                        {
                            Warn("卸载失败，可能仍有残留占用", error.Message);
                        }
                    }
                }
                return 0;
            });
        }
        catch (Exception error)
        {
            Warn("临时操作执行或收尾失败", error.Message);
        }
    }
}
