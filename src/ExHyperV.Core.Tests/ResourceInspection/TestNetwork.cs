using System.Net;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static class TestNetwork
{
    // Call before HttpClient is initialized. Changes apply only to this process and its children.
    internal static void Initialize(bool direct = false)
    {
        foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
            Environment.SetEnvironmentVariable(key, null);
        if (direct || Environment.GetEnvironmentVariable("EXHYPERV_TEST_DIRECT_NETWORK") == "1")
        {
            Environment.SetEnvironmentVariable("EXHYPERV_TEST_DIRECT_NETWORK", "1");
            HttpClient.DefaultProxy = new WebProxy();
        }
        else _ = HttpClient.DefaultProxy; // Windows supplies manual/PAC proxy settings.
    }
}
