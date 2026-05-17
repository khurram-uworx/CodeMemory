using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using Npgsql;

namespace CodeMemory.AspNet.Storage.PgVector;

public sealed class PgVectorStore : VectorStore
{
    static readonly string[] KnownCollections = ["symbols", "chunks", "relationships"];

    readonly ConcurrentDictionary<string, object> collections = new();
    readonly NpgsqlDataSource dataSource;
    readonly PgVectorOptions options;
    readonly object gate = new();

    public PgVectorStore(string connectionString, PgVectorOptions? options = null)
    {
        this.options = options ?? new PgVectorOptions();
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        dataSource = builder.Build();
    }

    public NpgsqlDataSource DataSource => dataSource;
    public PgVectorOptions Options => options;
    public string Schema => options.Schema;

    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name, VectorStoreCollectionDefinition? definition = null)
    {
        lock (gate)
        {
            if (collections.TryGetValue(name, out var existing))
            {
                if (existing is VectorStoreCollection<TKey, TRecord> typed)
                    return typed;

                var actualType = existing.GetType();
                throw new InvalidOperationException(
                    $"Collection '{name}' already exists with type " +
                    $"{actualType.GenericTypeArguments[1].Name}, " +
                    $"cannot create with {typeof(TRecord).Name}.");
            }

            var collection = new PgVectorCollection<TKey, TRecord>(
                name, dataSource, definition, options);
            collections[name] = collection;
            return collection;
        }
    }

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(
        string name, VectorStoreCollectionDefinition definition)
    {
        throw new NotSupportedException("Dynamic collections are not supported by PgVectorStore.");
    }

    public override async IAsyncEnumerable<string> ListCollectionNamesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var name in listExistingCollectionsAsync(cancellationToken))
            yield return name;

        foreach (var name in KnownCollections)
            yield return name;
    }

    public override async Task<bool> CollectionExistsAsync(
        string name, CancellationToken cancellationToken = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_tables WHERE schemaname = @schema AND tablename = @name)", conn);
        cmd.Parameters.AddWithValue("schema", options.Schema);
        cmd.Parameters.AddWithValue("name", name);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public override async Task EnsureCollectionDeletedAsync(
        string name, CancellationToken cancellationToken = default)
    {
        if (collections.TryRemove(name, out var existing) && existing is IDisposable disposable)
            disposable.Dispose();

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"DROP TABLE IF EXISTS \"{options.Schema}\".\"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    async IAsyncEnumerable<string> listExistingCollectionsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = @schema", conn);
        cmd.Parameters.AddWithValue("schema", options.Schema);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return reader.GetString(0);
    }

    public override object? GetService(Type serviceType, object? serviceKey) => null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var entry in collections)
                if (entry.Value is IDisposable d)
                    d.Dispose();
            collections.Clear();
            dataSource.Dispose();
        }
        base.Dispose(disposing);
    }
}
