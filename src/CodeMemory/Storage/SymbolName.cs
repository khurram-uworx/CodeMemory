namespace CodeMemory.Storage;

/// <summary>
/// Pure helpers for resolving and suggesting symbol names across storage backends.
/// </summary>
public static class SymbolName
{
    /// <summary>
    /// Returns the full name with any parameter list stripped and a trailing '(' appended,
    /// so a bare method path ("Ns.Util.M") also matches the stored "Ns.Util.M(int, string)".
    /// </summary>
    public static string SignaturePrefix(string fullName)
    {
        var idx = fullName.IndexOf('(');
        if (idx >= 0)
            fullName = fullName[..idx];
        return fullName.TrimEnd() + "(";
    }

    /// <summary>
    /// Returns the final identifier segment of a symbol path: the text after the last '.'
    /// with any parameter list removed ("Ns.Util.getLikeMethod(string pattern)" → "getLikeMethod").
    /// </summary>
    public static string LastSegment(string query)
    {
        var idx = query.IndexOf('(');
        if (idx >= 0)
            query = query[..idx];
        var dot = query.LastIndexOf('.');
        if (dot >= 0 && dot < query.Length - 1)
            query = query[(dot + 1)..];
        return query;
    }
}