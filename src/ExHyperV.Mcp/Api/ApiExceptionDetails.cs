using System.Reflection;

namespace ExHyperV.Mcp.Api;

internal static class ApiExceptionDetails
{
    // Keep Core's public diagnostics (archive reason, limits and retained paths),
    // while leaving native Exception internals out of protocol serialization.
    public static Dictionary<string, object?> Capture(Exception error)
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = error.GetType().Name, ["message"] = error.Message, ["hResult"] = error.HResult
        };
        for (Type? type = error.GetType(); type != null && type.Assembly == ApiCatalog.Core; type = type.BaseType)
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0)
                    result[System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.GetValue(error);
        return result;
    }
}
