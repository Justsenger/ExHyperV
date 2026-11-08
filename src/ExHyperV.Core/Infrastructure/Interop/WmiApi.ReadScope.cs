namespace ExHyperV.Tools;

internal static partial class WmiApi
{
    // A scoped provider outcome permits deterministic read-failure tests through the public
    // services. It is internal, flows with the caller, and never intercepts native mutations.
    internal sealed record QuerySubstitution(ApiFailure? Failure = null);
    private static readonly AsyncLocal<Func<string, string, QuerySubstitution?>?> QuerySource = new();

    internal static IDisposable UseQuerySource(Func<string, string, QuerySubstitution?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var previous = QuerySource.Value; QuerySource.Value = source;
        return new QuerySourceScope(previous);
    }

    private sealed class QuerySourceScope(Func<string, string, QuerySubstitution?>? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (disposed) return; QuerySource.Value = previous; disposed = true; }
    }
}
