using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using Npgsql;
using Pgvector;

namespace CodeMemory.AspNet.Storage.PgVector;

public sealed class VectorStoreException : InvalidOperationException
{
    public VectorStoreException(string message) : base(message) { }
}

sealed record ColumnInfo(
    string PropertyName,
    string ColumnName,
    Type ClrType,
    bool IsKey,
    bool IsVector,
    int? VectorDimensions)
{
    public bool IsNullable { get; init; }
}

sealed class PropertyAccess
{
    readonly Func<object, object?> getter;
    readonly Action<object, object?> setter;

    public PropertyAccess(PropertyInfo prop)
    {
        getter = prop.CanRead ? compileGetter(prop) : _ => null;
        setter = prop.CanWrite ? compileSetter(prop) : (_, _) => { };
    }

    public object? GetValue(object record) => getter(record);
    public void SetValue(object record, object? value) => setter(record, value);

    static Func<object, object?> compileGetter(PropertyInfo prop)
    {
        var param = Expression.Parameter(typeof(object), "o");
        var cast = Expression.Convert(param, prop.DeclaringType!);
        var propAccess = Expression.Property(cast, prop);
        var box = Expression.Convert(propAccess, typeof(object));
        return Expression.Lambda<Func<object, object?>>(box, param).Compile();
    }

    static Action<object, object?> compileSetter(PropertyInfo prop)
    {
        var targetParam = Expression.Parameter(typeof(object), "target");
        var valueParam = Expression.Parameter(typeof(object), "value");
        var castTarget = Expression.Convert(targetParam, prop.DeclaringType!);
        var castValue = Expression.Convert(valueParam, prop.PropertyType);
        var propAccess = Expression.Property(castTarget, prop);
        var assign = Expression.Assign(propAccess, castValue);
        return Expression.Lambda<Action<object, object?>>(assign, targetParam, valueParam).Compile();
    }
}

sealed class PgVectorCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    // Static — computed once per closed generic type (attribute-based fallback)
    static readonly Func<TRecord> RecordFactory;
    static readonly IReadOnlyList<ColumnInfo> DefaultColumns;
    static readonly PropertyAccess DefaultKeyAccess;
    static readonly IReadOnlyList<(ColumnInfo, PropertyAccess)> DefaultDataColumns;
    static readonly (ColumnInfo, PropertyAccess)? DefaultVectorColumn;

    static PgVectorCollection()
    {
        var (cols, keyAcc, dataAccs, vecAcc) = buildAttributeMetadata();
        DefaultColumns = cols;
        DefaultKeyAccess = keyAcc;
        DefaultDataColumns = dataAccs;
        DefaultVectorColumn = vecAcc;
        RecordFactory = compileFactory();
    }

    // Instance — resolved once per collection creation
    readonly NpgsqlDataSource dataSource;
    readonly PgVectorOptions options;
    readonly string tableName;
    readonly string qualifiedName;
    readonly int? vectorDimensions;

    readonly IReadOnlyList<ColumnInfo> columns;
    readonly PropertyAccess keyAccess;
    readonly IReadOnlyList<(ColumnInfo Info, PropertyAccess Access)> dataColumns;
    readonly (ColumnInfo Info, PropertyAccess Access)? vectorColumn;
    readonly string ddlSql;
    readonly string selectSql;
    readonly string selectAllSql;
    readonly string insertSql;
    readonly string deleteSql;
    readonly string dropSql;

    volatile bool deleted;

    public PgVectorCollection(
        string name,
        NpgsqlDataSource dataSource,
        VectorStoreCollectionDefinition? definition,
        PgVectorOptions options)
    {
        tableName = name;
        this.dataSource = dataSource;
        this.options = options;
        qualifiedName = $"\"{options.Schema}\".\"{name}\"";

        if (definition is not null)
            (columns, keyAccess, dataColumns, vectorColumn) = buildDefinitionMetadata(definition);
        else
            (columns, keyAccess, dataColumns, vectorColumn) = (DefaultColumns, DefaultKeyAccess, DefaultDataColumns, DefaultVectorColumn);

        var vecProp = columns.FirstOrDefault(c => c.IsVector);
        vectorDimensions = vecProp?.VectorDimensions;

        // Build SQL templates
        var sorted = columns.OrderBy(c => c.IsKey ? 0 : 1).ThenBy(c => c.ColumnName).ToList();
        var colNames = string.Join(", ", sorted.Select(c => $"\"{c.ColumnName}\""));
        var colDefs = string.Join(", ", columns.Select(c =>
        {
            var def = $"\"{c.ColumnName}\" {pgType(c)}";
            if (c.IsKey) def += " PRIMARY KEY";
            return def;
        }));

        ddlSql = $"CREATE TABLE IF NOT EXISTS {qualifiedName} (\n  {colDefs}\n)";
        selectSql = $"SELECT {colNames} FROM {qualifiedName} WHERE \"Id\" = @key";
        selectAllSql = $"SELECT {colNames} FROM {qualifiedName}";
        var insertCols = string.Join(", ", sorted.Select(c => $"\"{c.ColumnName}\""));
        var insertParams = string.Join(", ", sorted.Select(c => $"@{c.ColumnName}"));
        var updates = string.Join(", ", sorted.Where(c => !c.IsKey).Select(c => $"\"{c.ColumnName}\" = @{c.ColumnName}"));
        insertSql = $"INSERT INTO {qualifiedName} ({insertCols}) VALUES ({insertParams}) ON CONFLICT (\"Id\") DO UPDATE SET {updates}";
        deleteSql = $"DELETE FROM {qualifiedName} WHERE \"Id\" = @key";
        dropSql = $"DROP TABLE IF EXISTS {qualifiedName}";
    }

    public override string Name => tableName;

    public override async Task<bool> CollectionExistsAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_tables WHERE schemaname = @s AND tablename = @t)", conn);
        cmd.Parameters.AddWithValue("s", options.Schema);
        cmd.Parameters.AddWithValue("t", tableName);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    public override async Task EnsureCollectionExistsAsync(CancellationToken ct = default)
    {
        if (deleted)
            throw new ObjectDisposedException(GetType().Name);

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Ensure pgvector extension exists and refresh Npgsql type cache
        // so that the 'vector' type is resolved for the CREATE TABLE below.
        await using (var extCmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conn))
            await extCmd.ExecuteNonQueryAsync(ct);
        conn.ReloadTypes();

        // Create schema if it doesn't exist (e.g. "test" schema during integration tests).
        // Catch duplicate-key race from concurrent connections.
        try
        {
            await using (var schemaCmd = new NpgsqlCommand(
                $"CREATE SCHEMA IF NOT EXISTS \"{options.Schema}\"", conn))
                await schemaCmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // schema already exists — race with concurrent connection is harmless
        }

        await using (var ddl = new NpgsqlCommand(ddlSql, conn))
            await ddl.ExecuteNonQueryAsync(ct);

        if (vectorColumn is not null && options.Index is { } idxOpts)
            await createVectorIndexAsync(conn, idxOpts, ct);
    }

    public override async Task EnsureCollectionDeletedAsync(CancellationToken ct = default)
    {
        deleted = true;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(dropSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public override async Task<TRecord?> GetAsync(
        TKey key, RecordRetrievalOptions? options = null, CancellationToken ct = default)
    {
        if (deleted) return null;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(selectSql, conn);
        cmd.Parameters.AddWithValue("key", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return readRecord(reader, options?.IncludeVectors ?? false);
        return null;
    }

    public override IAsyncEnumerable<TRecord> GetAsync(
        IEnumerable<TKey> keys, RecordRetrievalOptions? options = null,
        CancellationToken ct = default)
    {
        if (deleted) return EmptyAsync();
        return getByKeysAsync(keys, options?.IncludeVectors ?? false, ct);
    }

    public override async IAsyncEnumerable<TRecord> GetAsync(
        Expression<Func<TRecord, bool>> filter, int top,
        FilteredRecordRetrievalOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (deleted) yield break;

        var predicate = filter.Compile();
        var skip = options?.Skip ?? 0;
        var count = 0;
        var skipped = 0;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(selectAllSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var record = readRecord(reader);
            if (!predicate(record))
                continue;

            if (skipped < skip) { skipped++; continue; }

            yield return record;
            count++;
            if (count >= top) yield break;
        }
    }

    public override async Task DeleteAsync(TKey key, CancellationToken ct = default)
    {
        if (deleted) return;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(deleteSql, conn);
        cmd.Parameters.AddWithValue("key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public override async Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken ct = default)
    {
        if (deleted) return;
        var keyArray = keys.Select(k => k?.ToString()).ToArray();
        if (keyArray.Length == 0) return;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {qualifiedName} WHERE \"Id\" = ANY(@keys)", conn);
        cmd.Parameters.AddWithValue("keys", keyArray);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public override async Task UpsertAsync(TRecord record, CancellationToken ct = default)
    {
        if (deleted) throw new ObjectDisposedException(GetType().Name);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(insertSql, conn);
        setParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken ct = default)
    {
        if (deleted) throw new ObjectDisposedException(GetType().Name);
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            await using var cmd = new NpgsqlCommand(insertSql, conn);
            setParameters(cmd, record);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(
        TInput searchValue, int top, VectorSearchOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (deleted) yield break;

        if (searchValue is not ReadOnlyMemory<float> queryVector)
            throw new VectorStoreException(
                $"Vector search with input type '{typeof(TInput).Name}' is not supported. Use ReadOnlyMemory<float>.");

        if (vectorColumn is null)
            throw new VectorStoreException("Record type has no vector property for search.");

        var filter = options?.Filter?.Compile();
        var skip = options?.Skip ?? 0;
        var threshold = options?.ScoreThreshold;
        var includeVectors = options?.IncludeVectors ?? false;
        var vecCol = vectorColumn.Value.Info.ColumnName;
        var colList = new List<string> { "\"Id\"" };
        colList.AddRange(dataColumns.Select(c => $"\"{c.Info.ColumnName}\""));
        var cols = string.Join(", ", colList);

        var searchSql = $"SELECT {cols}, \"{vecCol}\" <=> @query AS __score " +
                        $"FROM {qualifiedName} ORDER BY \"{vecCol}\" <=> @query LIMIT @res_top";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(searchSql, conn);
        cmd.Parameters.AddWithValue("query", new Vector(queryVector));
        cmd.Parameters.AddWithValue("res_top", top + skip);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var count = 0;
        var skipped = 0;

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var record = readRecord(reader, includeVectors);

            // Cosine distance → cosine similarity (matches InMemoryVectorStore behavior)
            var score = 1.0 - reader.GetDouble(reader.FieldCount - 1);

            if (threshold.HasValue && score < threshold.Value) continue;
            if (filter is not null && !filter(record)) continue;
            if (skipped < skip) { skipped++; continue; }

            yield return new VectorSearchResult<TRecord>(record, score);
            count++;
            if (count >= top) yield break;
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey) => null;

    // ── Private ──────────────────────────────────────────────────────

    TRecord readRecord(NpgsqlDataReader reader, bool includeVector = false)
    {
        var record = RecordFactory();

        // Read key column
        var keyCol = columns.First(c => c.IsKey);
        {
            var ordinal = reader.GetOrdinal(keyCol.ColumnName);
            if (!reader.IsDBNull(ordinal))
            {
                var keyValue = keyCol.ClrType switch
                {
                    not null when keyCol.ClrType == typeof(string) => reader.GetString(ordinal),
                    not null when keyCol.ClrType == typeof(int) => reader.GetInt32(ordinal),
                    not null when keyCol.ClrType == typeof(long) => reader.GetInt64(ordinal),
                    not null when keyCol.ClrType == typeof(Guid) => reader.GetGuid(ordinal),
                    _ => reader.GetValue(ordinal)
                };
                keyAccess.SetValue(record, keyValue);
            }
        }

        foreach (var (info, access) in dataColumns)
        {
            var ordinal = reader.GetOrdinal(info.ColumnName);
            if (reader.IsDBNull(ordinal))
            {
                access.SetValue(record, null);
                continue;
            }

            var value = info.ClrType switch
            {
                not null when info.ClrType == typeof(string) => reader.GetString(ordinal),
                not null when info.ClrType == typeof(int) => reader.GetInt32(ordinal),
                not null when info.ClrType == typeof(double) => reader.GetDouble(ordinal),
                not null when info.ClrType == typeof(float) => reader.GetFloat(ordinal),
                not null when info.ClrType == typeof(bool) => reader.GetBoolean(ordinal),
                not null when info.ClrType == typeof(long) => reader.GetInt64(ordinal),
                not null when info.ClrType == typeof(short) => reader.GetInt16(ordinal),
                not null when info.ClrType == typeof(DateTime) => reader.GetDateTime(ordinal),
                not null when info.ClrType == typeof(DateTimeOffset) => reader.GetDateTime(ordinal),
                _ => reader.GetValue(ordinal)
            };
            access.SetValue(record, value);
        }

        if (vectorColumn is { } vc && includeVector)
        {
            var ordinal = reader.GetOrdinal(vc.Info.ColumnName);
            if (!reader.IsDBNull(ordinal))
            {
                var pgVec = reader.GetFieldValue<Vector>(ordinal);
                vc.Access.SetValue(record, pgVec.Memory);
            }
        }

        return record;
    }

    void setParameters(NpgsqlCommand cmd, TRecord record)
    {
        foreach (var (info, access) in columns.Select(c => (c, resolveAccess(c))))
        {
            var value = access.GetValue(record);

            if (info.IsVector)
            {
                cmd.Parameters.AddWithValue(info.ColumnName,
                    value is ReadOnlyMemory<float> mem ? new Vector(mem) : DBNull.Value);
            }
            else
            {
                cmd.Parameters.AddWithValue(info.ColumnName, value ?? DBNull.Value);
            }
        }
    }

    PropertyAccess resolveAccess(ColumnInfo col)
    {
        if (col.IsKey) return keyAccess;
        var match = dataColumns.FirstOrDefault(d => d.Info.PropertyName == col.PropertyName);
        if (match.Info is not null) return match.Access;

        return new PropertyAccess(
            typeof(TRecord).GetProperty(col.PropertyName, BindingFlags.Public | BindingFlags.Instance)!);
    }

    async Task createVectorIndexAsync(NpgsqlConnection conn, VectorIndexOptions idx, CancellationToken ct)
    {
        var idxName = $"idx_{tableName}_embedding";
        var vecCol = vectorColumn!.Value.Info.ColumnName;

        if (string.Equals(idx.Method, "hnsw", StringComparison.OrdinalIgnoreCase))
        {
            var m = idx.M ?? 16;
            var ef = idx.EfConstruction ?? 64;
            await using var cmd = new NpgsqlCommand(
                $"CREATE INDEX IF NOT EXISTS \"{idxName}\" ON {qualifiedName} " +
                $"USING hnsw (\"{vecCol}\" {idx.DistanceFunction}) " +
                $"WITH (m = {m}, ef_construction = {ef})", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else if (string.Equals(idx.Method, "ivfflat", StringComparison.OrdinalIgnoreCase))
        {
            var lists = idx.Lists ?? 100;
            await using var cmd = new NpgsqlCommand(
                $"CREATE INDEX IF NOT EXISTS \"{idxName}\" ON {qualifiedName} " +
                $"USING ivfflat (\"{vecCol}\" {idx.DistanceFunction}) " +
                $"WITH (lists = {lists})", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    async IAsyncEnumerable<TRecord> getByKeysAsync(
        IEnumerable<TKey> keys, bool includeVector, [EnumeratorCancellation] CancellationToken ct)
    {
        var keyList = keys.ToList();
        if (keyList.Count == 0) yield break;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"{selectAllSql} WHERE \"Id\" = ANY(@keys)", conn);
        cmd.Parameters.AddWithValue("keys", keyList.Select(k => k?.ToString()).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            yield return readRecord(reader, includeVector);
    }

    static string pgType(ColumnInfo col)
    {
        if (col.IsVector)
            return $"vector({col.VectorDimensions ?? 1536})";

        var t = Nullable.GetUnderlyingType(col.ClrType) ?? col.ClrType;
        return t switch
        {
            not null when t == typeof(string) => "TEXT",
            not null when t == typeof(int) || t == typeof(short) => "INTEGER",
            not null when t == typeof(long) => "BIGINT",
            not null when t == typeof(double) => "DOUBLE PRECISION",
            not null when t == typeof(float) => "REAL",
            not null when t == typeof(bool) => "BOOLEAN",
            not null when t == typeof(DateTime) || t == typeof(DateTimeOffset) => "TIMESTAMPTZ",
            not null when t == typeof(decimal) => "NUMERIC",
            not null when t == typeof(Guid) => "UUID",
            _ => "TEXT"
        };
    }

    static (IReadOnlyList<ColumnInfo> cols, PropertyAccess keyAccess,
        IReadOnlyList<(ColumnInfo, PropertyAccess)> dataAccessors,
        (ColumnInfo, PropertyAccess)? vectorAccessor)
        buildAttributeMetadata()
    {
        var props = typeof(TRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var cols = new List<ColumnInfo>();
        PropertyAccess? keyAccess = null;
        var dataAccessors = new List<(ColumnInfo, PropertyAccess)>();
        (ColumnInfo, PropertyAccess)? vectorAccessor = null;

        foreach (var prop in props)
        {
            var keyAttr = prop.GetCustomAttribute<VectorStoreKeyAttribute>();
            if (keyAttr is not null)
            {
                var ci = new ColumnInfo(prop.Name, keyAttr.StorageName ?? prop.Name, prop.PropertyType,
                    IsKey: true, IsVector: false, VectorDimensions: null);
                var acc = new PropertyAccess(prop);
                cols.Add(ci);
                keyAccess = acc;
                continue;
            }

            var vecAttr = prop.GetCustomAttribute<VectorStoreVectorAttribute>();
            if (vecAttr is not null)
            {
                var ci = new ColumnInfo(prop.Name, vecAttr.StorageName ?? prop.Name,
                    typeof(ReadOnlyMemory<float>),
                    IsKey: false, IsVector: true, VectorDimensions: vecAttr.Dimensions);
                var acc = new PropertyAccess(prop);
                cols.Add(ci);
                vectorAccessor = (ci, acc);
                continue;
            }

            var dataAttr = prop.GetCustomAttribute<VectorStoreDataAttribute>();
            if (dataAttr is not null)
            {
                var ci = new ColumnInfo(prop.Name, dataAttr.StorageName ?? prop.Name, prop.PropertyType,
                    IsKey: false, IsVector: false, VectorDimensions: null)
                { IsNullable = Nullable.GetUnderlyingType(prop.PropertyType) is not null };
                var acc = new PropertyAccess(prop);
                cols.Add(ci);
                dataAccessors.Add((ci, acc));
            }
        }

        // Fallback: no attributes at all — treat every property as data
        if (cols.Count == 0)
        {
            foreach (var prop in props)
            {
                var isKey = string.Equals(prop.Name, "Id", StringComparison.OrdinalIgnoreCase);
                var ci = new ColumnInfo(prop.Name, prop.Name, prop.PropertyType,
                    IsKey: isKey, IsVector: false, VectorDimensions: null);
                var acc = new PropertyAccess(prop);
                cols.Add(ci);
                if (isKey)
                    keyAccess = acc;
                else
                    dataAccessors.Add((ci, acc));
            }
        }

        keyAccess ??= new PropertyAccess(props.First(p =>
            string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase)));

        return (cols, keyAccess!, dataAccessors, vectorAccessor);
    }

    static (IReadOnlyList<ColumnInfo>, PropertyAccess,
        IReadOnlyList<(ColumnInfo, PropertyAccess)>,
        (ColumnInfo, PropertyAccess)?)
        buildDefinitionMetadata(VectorStoreCollectionDefinition def)
    {
        var cols = new List<ColumnInfo>();
        PropertyAccess? keyAccess = null;
        var dataAccessors = new List<(ColumnInfo, PropertyAccess)>();
        (ColumnInfo, PropertyAccess)? vectorAccessor = null;

        foreach (var prop in def.Properties)
        {
            PropertyInfo? clrProp = typeof(TRecord)
                .GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);

            switch (prop)
            {
                case VectorStoreKeyProperty kp:
                    var kci = new ColumnInfo(kp.Name, kp.StorageName ?? kp.Name, kp.Type ?? typeof(string),
                        IsKey: true, IsVector: false, VectorDimensions: null);
                    cols.Add(kci);
                    keyAccess = new PropertyAccess(clrProp ?? throw new InvalidOperationException(
                        $"Property '{kp.Name}' not found on {typeof(TRecord).Name}"));
                    break;

                case VectorStoreVectorProperty vp:
                    var vci = new ColumnInfo(vp.Name, vp.StorageName ?? vp.Name,
                        typeof(ReadOnlyMemory<float>),
                        IsKey: false, IsVector: true, VectorDimensions: vp.Dimensions);
                    cols.Add(vci);
                    vectorAccessor = (vci, new PropertyAccess(
                        clrProp ?? throw new InvalidOperationException(
                            $"Property '{vp.Name}' not found on {typeof(TRecord).Name}")));
                    break;

                case VectorStoreDataProperty dp:
                    var dci = new ColumnInfo(dp.Name, dp.StorageName ?? dp.Name, dp.Type ?? typeof(string),
                        IsKey: false, IsVector: false, VectorDimensions: null)
                    { IsNullable = Nullable.GetUnderlyingType(dp.Type ?? typeof(string)) is not null };
                    cols.Add(dci);
                    dataAccessors.Add((dci, new PropertyAccess(
                        clrProp ?? throw new InvalidOperationException(
                            $"Property '{dp.Name}' not found on {typeof(TRecord).Name}"))));
                    break;
            }
        }

        return (cols, keyAccess!, dataAccessors, vectorAccessor);
    }

    static Func<TRecord> compileFactory()
    {
        var ctor = typeof(TRecord).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 0);

        if (ctor is not null)
            return Expression.Lambda<Func<TRecord>>(Expression.New(ctor)).Compile();

        return () => (TRecord)Activator.CreateInstance(typeof(TRecord), nonPublic: true)!;
    }

    static IAsyncEnumerable<TRecord> EmptyAsync()
    {
        return EmptyAsyncEnumerable<TRecord>.Instance;
    }

    sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>
    {
        public static readonly IAsyncEnumerable<T> Instance = new EmptyAsyncEnumerable<T>();
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
            => new Enumerator();
        sealed class Enumerator : IAsyncEnumerator<T>
        {
            public T Current => throw new InvalidOperationException();
            public ValueTask<bool> MoveNextAsync() => new(false);
            public ValueTask DisposeAsync() => default;
        }
    }
}
