using System.IO;
using System.Xml.Linq;
namespace ExHyperV.Services;

/// <summary>在当前用户共享 Config.xml 中保存和读取宿主 MMIO 上限检测结果。</summary>
internal static class MmioSettingsStore
{
    /// <summary>读取持久化 MMIO 上限，不触发重新检测。</summary>
    /// <returns>以 MiB（1024×1024 字节）计的上限；未保存、格式无效或读取失败时为 null。</returns>
    public static ulong? GetMmioCeilingMb()
        {
            try
            {
                XDocument configDoc = AppConfigurationStore.Load();
                var v = configDoc.Root?.Element("MmioCeilingMb")?.Value;
                return ulong.TryParse(v, out ulong mb) && mb > 0 ? mb : null;
            }
            catch { return null; }
        }
    /// <summary>尝试将 MMIO 上限写入当前用户共享配置，保留其他配置项。</summary>
    /// <remarks>保存失败时忽略异常并返回。</remarks>
    /// <param name="ceilingMb">以 MiB（1024×1024 字节）计的检测上限。</param>
    public static void SaveMmioCeilingMb(ulong ceilingMb)
        {
            try
            {
                AppConfigurationStore.Update(root => root.SetElementValue("MmioCeilingMb", ceilingMb.ToString()));
            }
            catch { }
        }
}
