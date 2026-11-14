using ExHyperV.Services;
using System.Xml.Linq;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static class ConfigurationAvailability
{
    internal static ResourceResult Inspect(Func<XDocument>? read = null)
    {
        try
        {
            var document = (read ?? AppConfigurationStore.Load)();
            if (document.Root is null) throw new InvalidDataException("配置文件缺少根元素。");
            return new(InspectionStatus.Ready, "可读取；测试仅恢复涉及的字段");
        }
        catch (Exception error)
        {
            return new(InspectionStatus.Unavailable, "配置读取失败", [error.Message]);
        }
    }
}
