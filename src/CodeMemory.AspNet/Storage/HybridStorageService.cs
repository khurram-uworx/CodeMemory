using CodeMemory.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace CodeMemory.AspNet.Storage;

public sealed class HybridStorageService : IStorageService, IDisposable
{
    const int BatchSize = 200;
    readonly ILogger logger;
    readonly string repoRoot;
    readonly VectorStore vectorStore;
    readonly Func<CodeMemoryDbContext> createDbContext;
    readonly IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator;
    readonly int configuredDimension;
    int actualDimension;
    VectorStoreCollection<string, ChunkRecord>? chunks;
    bool initialized;

    public HybridStorageService(
        string repoRoot,
        ILogger logger,
        VectorStore vectorStore,
        Func<CodeMemoryDbContext> createDbContext,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        int configuredDimension = 1536)
    {
        this.repoRoot = repoRoot;
        this.logger = logger;
        this.vectorStore = vectorStore;
        this.createDbContext = createDbContext;
        this.embeddingGenerator = embeddingGenerator;
        this.configuredDimension = configuredDimension;
    }

    public string RepoRoot => repoRoot;

    public VectorStore? Store => vectorStore;

    public CodeMemoryDbContext CreateDbContext()
        => createDbContext();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dimension = configuredDimension;
        if (embeddingGenerator?.GetService(typeof(EmbeddingGeneratorMetadata)) is EmbeddingGeneratorMetadata meta
            && meta.DefaultModelDimensions.HasValue)
            dimension = meta.DefaultModelDimensions.Value;

        actualDimension = dimension;
        chunks = vectorStore.GetCollection<string, ChunkRecord>("chunks",
            VectorSchema.CreateChunkDefinition(dimension));

        await chunks.EnsureCollectionExistsAsync(ct);

        await using var db = createDbContext();
        await db.Database.EnsureCreatedAsync(ct);
        await EnsureRelationalTablesAsync(db, ct);

        initialized = true;
    }

    public async Task StoreSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        logger.LogInformation("Storing {Count} symbols into relational storage", symbols.Count);

        for (var i = 0; i < symbols.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = symbols
                .Skip(i)
                .Take(BatchSize)
                .GroupBy(symbol => symbol.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();

            await using var db = createDbContext();
            var ids = batch.Select(symbol => symbol.Id).ToArray();
            var existing = await db.Symbols
                .Where(symbol => ids.Contains(symbol.Id))
                .ToListAsync(ct);
            var existingById = existing.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);

            foreach (var symbol in batch)
            {
                if (existingById.TryGetValue(symbol.Id, out var entity))
                    Apply(symbol, entity);
                else
                    await db.Symbols.AddAsync(symbol.ToEntity(), ct);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task StoreChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        foreach (var chunk in chunks)
        {
            if (chunk.Embedding.HasValue && chunk.Embedding.Value.Length != actualDimension)
                throw new InvalidOperationException(
                    $"Chunk '{chunk.Id}' has embedding dimension {chunk.Embedding.Value.Length}, " +
                    $"but the collection was created with dimension {actualDimension}. " +
                    "The embedding generator dimension must match the storage schema dimension.");
        }

        await this.chunks!.UpsertAsync(chunks, ct);
    }

    public async Task StoreRelationshipsAsync(IReadOnlyList<RelationshipRecord> relationships, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        logger.LogInformation("Storing {Count} relationships into relational storage", relationships.Count);

        for (var i = 0; i < relationships.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = relationships
                .Skip(i)
                .Take(BatchSize)
                .GroupBy(relationship => relationship.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();

            await using var db = createDbContext();
            var ids = batch.Select(relationship => relationship.Id).ToArray();
            var existing = await db.Relationships
                .Where(relationship => ids.Contains(relationship.Id))
                .ToListAsync(ct);
            var existingById = existing.ToDictionary(relationship => relationship.Id, StringComparer.Ordinal);

            foreach (var relationship in batch)
            {
                if (existingById.TryGetValue(relationship.Id, out var entity))
                    Apply(relationship, entity);
                else
                    await db.Relationships.AddAsync(relationship.ToEntity(), ct);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<SymbolRecord?> GetSymbolAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await using var db = createDbContext();
        var entity = await db.Symbols
            .AsNoTracking()
            .FirstOrDefaultAsync(symbol => symbol.Id == id, ct);
        return entity?.ToRecord();
    }

    public async Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        return await chunks!.GetAsync(id, new RecordRetrievalOptions { IncludeVectors = true }, ct);
    }

    public async Task<RelationshipRecord?> GetRelationshipAsync(string id, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await using var db = createDbContext();
        var entity = await db.Relationships
            .AsNoTracking()
            .FirstOrDefaultAsync(relationship => relationship.Id == id, ct);
        return entity?.ToRecord();
    }

    public async Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(
        string filePath, int top = 100, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await using var db = createDbContext();
        return await db.Symbols
            .AsNoTracking()
            .Where(symbol => symbol.FilePath == filePath)
            .Take(top)
            .Select(symbol => symbol.ToRecord())
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SymbolRecord>> GetSymbolsByKindAsync(
        string kind, int top = 100, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await using var db = createDbContext();
        return await db.Symbols
            .AsNoTracking()
            .Where(symbol => symbol.Kind == kind)
            .Take(top)
            .Select(symbol => symbol.ToRecord())
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksBySymbolAsync(
        string symbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        return await chunks!.GetAsync(
            chunk => chunk.SymbolId == symbolId,
            top: 1000,
            new FilteredRecordRetrievalOptions<ChunkRecord> { IncludeVectors = true },
            ct)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsBySourceAsync(
        string sourceSymbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await using var db = createDbContext();
        return await db.Relationships
            .AsNoTracking()
            .Where(relationship => relationship.SourceSymbolId == sourceSymbolId)
            .Take(1000)
            .Select(relationship => relationship.ToRecord())
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RelationshipRecord>> GetRelationshipsByTargetAsync(
        string targetSymbolId, CancellationToken ct = default)
    {
        throwIfNotInitialized();
        await using var db = createDbContext();
        return await db.Relationships
            .AsNoTracking()
            .Where(relationship => relationship.TargetSymbolId == targetSymbolId)
            .Take(1000)
            .Select(relationship => relationship.ToRecord())
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchChunksAsync(
        ReadOnlyMemory<float> embedding,
        int top = 10,
        VectorSearchOptions<ChunkRecord>? options = null,
        CancellationToken ct = default)
    {
        throwIfNotInitialized();
        if (embedding.Length != actualDimension)
            throw new InvalidOperationException(
                $"Query embedding has dimension {embedding.Length}, " +
                $"but the collection was created with dimension {actualDimension}. " +
                "The embedding generator dimension must match the stored vectors.");

        var results = new List<ScoredChunk>();
        await foreach (var result in chunks!.SearchAsync<ReadOnlyMemory<float>>(
            embedding, top, options: options, ct))
        {
            results.Add(new ScoredChunk { Chunk = result.Record, Score = result.Score ?? 0 });
        }

        return results;
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        throwIfNotInitialized();

        if (chunks is not null)
            await chunks.EnsureCollectionDeletedAsync(ct);

        await using var db = createDbContext();
        await DropTableIfExistsAsync(db, "relationships", ct);
        await DropTableIfExistsAsync(db, "symbols", ct);

        chunks = null;
        initialized = false;
    }

    public void Dispose()
    {
        (vectorStore as IDisposable)?.Dispose();
        (chunks as IDisposable)?.Dispose();
    }

    void throwIfNotInitialized()
    {
        if (!initialized)
            throw new InvalidOperationException("Storage service not initialized. Call InitializeAsync first.");
    }

    static void Apply(SymbolRecord record, Models.SymbolEntity entity)
    {
        entity.Name = record.Name;
        entity.Kind = record.Kind;
        entity.FilePath = record.FilePath;
        entity.LineStart = record.LineStart;
        entity.LineEnd = record.LineEnd;
        entity.FullName = record.FullName;
        entity.Modifiers = record.Modifiers;
        entity.Documentation = record.Documentation;
    }

    static void Apply(RelationshipRecord record, Models.RelationshipEntity entity)
    {
        entity.SourceSymbolId = record.SourceSymbolId;
        entity.TargetSymbolId = record.TargetSymbolId;
        entity.RelationshipType = record.RelationshipType;
    }

    static async Task DropTableIfExistsAsync(CodeMemoryDbContext db, string tableName, CancellationToken ct)
    {
        var providerName = db.Database.ProviderName ?? string.Empty;
        var sql = providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? $"DROP TABLE IF EXISTS {QuoteSqlServerIdentifier(db.Schema)}.{QuoteSqlServerIdentifier(tableName)}"
            : providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                ? $"DROP TABLE IF EXISTS {QuoteDoubleQuotedIdentifier(tableName)}"
                : $"DROP TABLE IF EXISTS {QuoteDoubleQuotedIdentifier(db.Schema)}.{QuoteDoubleQuotedIdentifier(tableName)}";

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    static async Task EnsureRelationalTablesAsync(CodeMemoryDbContext db, CancellationToken ct)
    {
        var providerName = db.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureSqlServerTablesAsync(db, ct);
            return;
        }

        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureSqliteTablesAsync(db, ct);
            return;
        }

        await EnsurePostgresTablesAsync(db, ct);
    }

    static async Task EnsureSqliteTablesAsync(CodeMemoryDbContext db, CancellationToken ct)
    {
        await ExecuteRawAsync(db,
            """
            CREATE TABLE IF NOT EXISTS "symbols" (
                "id" TEXT NOT NULL CONSTRAINT "PK_symbols" PRIMARY KEY,
                "name" TEXT NOT NULL,
                "kind" TEXT NOT NULL,
                "file_path" TEXT NOT NULL,
                "line_start" INTEGER NOT NULL,
                "line_end" INTEGER NOT NULL,
                "full_name" TEXT NOT NULL,
                "modifiers" TEXT NULL,
                "documentation" TEXT NULL
            );
            """,
            ct);
        await ExecuteRawAsync(db,
            """
            CREATE TABLE IF NOT EXISTS "relationships" (
                "id" TEXT NOT NULL CONSTRAINT "PK_relationships" PRIMARY KEY,
                "source_symbol_id" TEXT NOT NULL,
                "target_symbol_id" TEXT NOT NULL,
                "relationship_type" TEXT NOT NULL
            );
            """,
            ct);
        await ExecuteRawAsync(db, """CREATE INDEX IF NOT EXISTS "IX_Symbols_FilePath" ON "symbols" ("file_path");""", ct);
        await ExecuteRawAsync(db, """CREATE INDEX IF NOT EXISTS "IX_Symbols_Kind" ON "symbols" ("kind");""", ct);
        await ExecuteRawAsync(db, """CREATE INDEX IF NOT EXISTS "IX_Relationships_Source" ON "relationships" ("source_symbol_id");""", ct);
        await ExecuteRawAsync(db, """CREATE INDEX IF NOT EXISTS "IX_Relationships_Target" ON "relationships" ("target_symbol_id");""", ct);
        await ExecuteRawAsync(db, """CREATE INDEX IF NOT EXISTS "IX_Relationships_Type" ON "relationships" ("relationship_type");""", ct);
    }

    static async Task EnsurePostgresTablesAsync(CodeMemoryDbContext db, CancellationToken ct)
    {
        var schema = QuoteDoubleQuotedIdentifier(db.Schema);
        await ExecuteRawAsync(db, $"CREATE SCHEMA IF NOT EXISTS {schema};", ct);
        await ExecuteRawAsync(db,
            $"""
            CREATE TABLE IF NOT EXISTS {schema}."symbols" (
                "id" text NOT NULL CONSTRAINT "PK_symbols" PRIMARY KEY,
                "name" text NOT NULL,
                "kind" text NOT NULL,
                "file_path" text NOT NULL,
                "line_start" integer NOT NULL,
                "line_end" integer NOT NULL,
                "full_name" text NOT NULL,
                "modifiers" text NULL,
                "documentation" text NULL
            );
            """,
            ct);
        await ExecuteRawAsync(db,
            $"""
            CREATE TABLE IF NOT EXISTS {schema}."relationships" (
                "id" text NOT NULL CONSTRAINT "PK_relationships" PRIMARY KEY,
                "source_symbol_id" text NOT NULL,
                "target_symbol_id" text NOT NULL,
                "relationship_type" text NOT NULL
            );
            """,
            ct);
        await ExecuteRawAsync(db, $"""CREATE INDEX IF NOT EXISTS "IX_Symbols_FilePath" ON {schema}."symbols" ("file_path");""", ct);
        await ExecuteRawAsync(db, $"""CREATE INDEX IF NOT EXISTS "IX_Symbols_Kind" ON {schema}."symbols" ("kind");""", ct);
        await ExecuteRawAsync(db, $"""CREATE INDEX IF NOT EXISTS "IX_Relationships_Source" ON {schema}."relationships" ("source_symbol_id");""", ct);
        await ExecuteRawAsync(db, $"""CREATE INDEX IF NOT EXISTS "IX_Relationships_Target" ON {schema}."relationships" ("target_symbol_id");""", ct);
        await ExecuteRawAsync(db, $"""CREATE INDEX IF NOT EXISTS "IX_Relationships_Type" ON {schema}."relationships" ("relationship_type");""", ct);
    }

    static async Task EnsureSqlServerTablesAsync(CodeMemoryDbContext db, CancellationToken ct)
    {
        var schema = QuoteSqlServerIdentifier(db.Schema);
        await ExecuteRawAsync(db,
            $"""
            IF SCHEMA_ID(N'{EscapeSqlLiteral(db.Schema)}') IS NULL
                EXEC(N'CREATE SCHEMA {schema}');
            """,
            ct);
        await ExecuteRawAsync(db,
            $"""
            IF OBJECT_ID(N'{EscapeSqlLiteral(db.Schema)}.symbols', N'U') IS NULL
            BEGIN
                CREATE TABLE {schema}.[symbols] (
                    [id] nvarchar(450) NOT NULL CONSTRAINT [PK_symbols] PRIMARY KEY,
                    [name] nvarchar(max) NOT NULL,
                    [kind] nvarchar(450) NOT NULL,
                    [file_path] nvarchar(450) NOT NULL,
                    [line_start] int NOT NULL,
                    [line_end] int NOT NULL,
                    [full_name] nvarchar(max) NOT NULL,
                    [modifiers] nvarchar(max) NULL,
                    [documentation] nvarchar(max) NULL
                );
            END;
            """,
            ct);
        await ExecuteRawAsync(db,
            $"""
            IF OBJECT_ID(N'{EscapeSqlLiteral(db.Schema)}.relationships', N'U') IS NULL
            BEGIN
                CREATE TABLE {schema}.[relationships] (
                    [id] nvarchar(450) NOT NULL CONSTRAINT [PK_relationships] PRIMARY KEY,
                    [source_symbol_id] nvarchar(450) NOT NULL,
                    [target_symbol_id] nvarchar(450) NOT NULL,
                    [relationship_type] nvarchar(450) NOT NULL
                );
            END;
            """,
            ct);
        await ExecuteRawAsync(db, $"""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Symbols_FilePath' AND object_id = OBJECT_ID(N'{EscapeSqlLiteral(db.Schema)}.symbols')) CREATE INDEX [IX_Symbols_FilePath] ON {schema}.[symbols] ([file_path]);""", ct);
        await ExecuteRawAsync(db, $"""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Symbols_Kind' AND object_id = OBJECT_ID(N'{EscapeSqlLiteral(db.Schema)}.symbols')) CREATE INDEX [IX_Symbols_Kind] ON {schema}.[symbols] ([kind]);""", ct);
        await ExecuteRawAsync(db, $"""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Relationships_Source' AND object_id = OBJECT_ID(N'{EscapeSqlLiteral(db.Schema)}.relationships')) CREATE INDEX [IX_Relationships_Source] ON {schema}.[relationships] ([source_symbol_id]);""", ct);
        await ExecuteRawAsync(db, $"""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Relationships_Target' AND object_id = OBJECT_ID(N'{EscapeSqlLiteral(db.Schema)}.relationships')) CREATE INDEX [IX_Relationships_Target] ON {schema}.[relationships] ([target_symbol_id]);""", ct);
        await ExecuteRawAsync(db, $"""IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Relationships_Type' AND object_id = OBJECT_ID(N'{EscapeSqlLiteral(db.Schema)}.relationships')) CREATE INDEX [IX_Relationships_Type] ON {schema}.[relationships] ([relationship_type]);""", ct);
    }

    static Task ExecuteRawAsync(CodeMemoryDbContext db, string sql, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(sql, ct);

    static string QuoteSqlServerIdentifier(string identifier)
        => $"[{identifier.Replace("]", "]]")}]";

    static string QuoteDoubleQuotedIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''");
}
