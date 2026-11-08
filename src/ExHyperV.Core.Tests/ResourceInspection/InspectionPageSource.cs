using System.Security.Cryptography;
using System.Text;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal sealed record InspectionPageTemplate(string Html, string Revision, string? Error = null);

internal sealed class InspectionPageSource(string? path)
{
    private readonly object gate = new();
    private InspectionPageTemplate current = Create(InspectionPage.ReadTemplate());

    internal InspectionPageTemplate Read()
    {
        lock (gate)
        {
            if (path is null) return current;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var html = reader.ReadToEnd();
                if (!html.Contains("</html>", StringComparison.OrdinalIgnoreCase)
                    || new[] { "__NONCE__", "__REPORT__", "__TEST_REPORT__", "__SESSION__" }.Any(marker => !html.Contains(marker, StringComparison.Ordinal)))
                    throw new InvalidDataException("页面文件正在写入。");
                current = Create(html);
                return current;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return current with { Error = "页面更新待重试，当前保留已加载版本。" };
            }
        }
    }

    private static InspectionPageTemplate Create(string html) => new(html, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))));
}
