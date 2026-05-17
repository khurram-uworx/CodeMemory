namespace CodeMemory.AspNet.Storage.PgVector;

public sealed record VectorIndexOptions
{
    public string Method { get; init; } = "hnsw";
    public string DistanceFunction { get; init; } = "vector_cosine_ops";
    public int? M { get; init; }
    public int? EfConstruction { get; init; }
    public int? Lists { get; init; }
}

public sealed record PgVectorOptions
{
    public string ConnectionString { get; init; } = string.Empty;
    public string Schema { get; init; } = "public";
    public VectorIndexOptions? Index { get; init; }
}
