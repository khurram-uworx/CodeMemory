```
  "ConnectionStrings": {
    "PgVector": ""
  },
  "PgVector": {
    "Schema": "public",
    "Index": {
      "Enabled": true,
      "Method": "hnsw",
      "DistanceFunction": "vector_cosine_ops",
      "M": 16,
      "EfConstruction": 64
    }
```

Reading config

```csharp
var usePgVector = string.Equals(provider, "pgvector", StringComparison.OrdinalIgnoreCase);

if (useSqlite || usePgVector)
    provider = useSqlite ? "sqlite" : "pgvector";
else
    provider = "inmemory";

if (!useSqlite && !usePgVector)
{
    // SQL query services (InMemoryVectorStore backend)
    builder.Services.AddSingleton<CodeMemory.SqlQuery.CollectionRegistry>();
    builder.Services.AddSingleton<CodeMemory.SqlQuery.SqlQueryService>();
    builder.Services.AddSingleton<CodeMemory.SqlQuery.TableSchemaProvider>();
}
```

and then registering storage services
```csharp
    if (usePgVector)
    {
        var connString = builder.Configuration.GetConnectionString("PgVector")
            ?? throw new InvalidOperationException("PgVector: Connection string 'PgVector' is required when Storage:Provider is 'pgvector'.");
        var pgOptions = builder.Configuration.GetSection("PgVector").Get<PgVectorOptions>() ?? new();
        var store = new PgVectorStore(connString, pgOptions with { ConnectionString = connString });
        var storageService = new StorageService(repoRoot, logger, store, embeddingGenerator);
        storageRegistry.Register(name, storageService);
        repoInfos.Add((name, repoRoot, connString));
    }
```
