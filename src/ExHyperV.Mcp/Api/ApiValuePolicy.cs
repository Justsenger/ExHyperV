using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Mcp.Api;

// Value snapshots travel as JSON. Objects with behavior or a Core-owned identity keep handles.
internal static class ApiValuePolicy
{
    private static readonly ConcurrentDictionary<Type, bool> identities = new();

    internal static bool IsHelper(MemberInfo member) => member is MethodInfo method &&
        (method.IsDefined(typeof(CompilerGeneratedAttribute), false) || method.Name is "<Clone>$") &&
        method.Name is "Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "<Clone>$" or "op_Equality" or "op_Inequality";

    internal static bool HasIdentity(Type type) => identities.GetOrAdd(type, static candidate =>
        typeof(IDisposable).IsAssignableFrom(candidate) || typeof(IAsyncDisposable).IsAssignableFrom(candidate)
        || typeof(Delegate).IsAssignableFrom(candidate) || candidate == typeof(VmImportPreview)
        || candidate == typeof(VmScreenshotImage)
        || candidate.Name.EndsWith("Session", StringComparison.Ordinal) || candidate.Name.EndsWith("Service", StringComparison.Ordinal)
        || candidate.GetInterfaces().Any(contract => contract.Assembly == ApiCatalog.Core
            && contract.GetMethods().Any(method => !method.IsSpecialName)));
}
