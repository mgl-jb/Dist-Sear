namespace DistSear.Index.Query;

/// <summary>
/// Raised when a query string cannot be parsed. Carries the offset so callers can point at the
/// offending character rather than rejecting the whole query with no explanation.
/// </summary>
public sealed class QueryParseException : Exception
{
    public QueryParseException(string message, int position, string query)
        : base($"{message} (at position {position} in \"{query}\")")
    {
        Position = position;
        Query = query;
    }

    public int Position { get; }

    public string Query { get; }
}
