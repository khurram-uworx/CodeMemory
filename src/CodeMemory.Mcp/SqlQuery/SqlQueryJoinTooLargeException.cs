namespace CodeMemory.Mcp.SqlQuery;

/// <summary>
/// Thrown when a join can only be evaluated as an unbounded nested loop whose estimated pair
/// count exceeds <see cref="SqlQueryService.MaxNestedLoopPairs"/> — the O(n·m) shape that made
/// #126/#137 queries appear to hang. Converted into a fail-fast diagnostic by
/// <see cref="SqlQueryService.ExecuteAsync"/>, not an engine error.
/// </summary>
public sealed class SqlQueryJoinTooLargeException : InvalidOperationException
{
    /// <summary>
    /// Creates the exception for an unbounded nested-loop join whose cardinality exceeds the cap.
    /// </summary>
    /// <param name="joinShape">The join type being evaluated (e.g. <c>FullOuter</c>).</param>
    /// <param name="estimatedPairs">Estimated row-pair cardinality of the nested loop.</param>
    /// <param name="limit">The configured pair cap (<see cref="SqlQueryService.MaxNestedLoopPairs"/>).</param>
    public SqlQueryJoinTooLargeException(string joinShape, long estimatedPairs, long limit)
        : base($"Join ({joinShape}) would evaluate {estimatedPairs:N0} row pairs, exceeding the " +
               $"nested-loop limit of {limit:N0}. Rewrite it as an equi-join (ON a.x = b.y), add " +
               $"a LIMIT, or narrow the inputs with a WHERE filter.")
    {
        JoinShape = joinShape;
        EstimatedPairs = estimatedPairs;
    }

    /// <summary>The join type being evaluated.</summary>
    public string JoinShape { get; }

    /// <summary>Estimated row-pair cardinality of the nested loop.</summary>
    public long EstimatedPairs { get; }
}