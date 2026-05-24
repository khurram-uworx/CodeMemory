using CodeMemory.Storage;
using System.Reflection;

namespace CodeMemory.Mcp.SqlQuery;

public sealed class TableSchemaProvider
{
    public sealed record ColumnInfo(string Name, string Type, bool IsNullable, bool IsKey, bool IsVector, string? StorageName);

    public sealed record JoinKeyInfo(
        string SourceTable,
        string SourceColumn,
        string TargetTable,
        string TargetColumn,
        string Description);

    static readonly List<JoinKeyInfo> JoinKeys = new()
    {
        new("RelationshipRecord", "SourceSymbolId", "SymbolRecord", "Id",
            "Outgoing relationship: what symbol depends on another"),
        new("RelationshipRecord", "TargetSymbolId", "SymbolRecord", "Id",
            "Incoming relationship: what symbol is depended upon"),
        new("ChunkRecord", "SymbolId", "SymbolRecord", "Id",
            "Chunk belongs to a symbol (if SymbolId is set)"),
        new("RelationshipWithNames", "SourceSymbolId", "SymbolRecord", "Id",
            "Outgoing relationship (denormalized view already includes SourceName)"),
        new("RelationshipWithNames", "TargetSymbolId", "SymbolRecord", "Id",
            "Incoming relationship (denormalized view already includes TargetName)"),
        new("SymbolReferenceStats", "SymbolId", "SymbolRecord", "Id",
            "Reference stats for a symbol (denormalized view already includes Name/Kind)"),
    };

    public List<ColumnInfo> GetColumns<T>()
        => GetColumns(typeof(T));

    public List<ColumnInfo> GetColumns(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead)
        .Select(p =>
        {
            var isKey = p.GetCustomAttributes().Any(a => a.GetType().Name == "VectorStoreKeyAttribute");
            var isData = p.GetCustomAttributes().Any(a => a.GetType().Name == "VectorStoreDataAttribute");
            var isVector = p.GetCustomAttributes().Any(a => a.GetType().Name == "VectorStoreVectorAttribute");
            var storageAttr = p.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().GetProperty("StorageName") is not null);
            var storageName = storageAttr
                ?.GetType().GetProperty("StorageName")?.GetValue(storageAttr) as string;

            var typeName = p.PropertyType switch
            {
                Type t when t == typeof(string) => "string",
                Type t when t == typeof(int) => "int",
                Type t when t == typeof(long) => "long",
                Type t when t == typeof(double) => "double",
                Type t when t == typeof(bool) => "bool",
                Type t when t == typeof(ReadOnlyMemory<float>?) => "vector(float)",
                Type t when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>) =>
                    Nullable.GetUnderlyingType(t)?.Name ?? "?",
                _ => p.PropertyType.Name
            };

            return new ColumnInfo(p.Name, typeName,
                Nullable.GetUnderlyingType(p.PropertyType) is not null || !p.PropertyType.IsValueType,
                isKey, isVector, storageName);
        })
        .ToList();

    public IReadOnlyList<JoinKeyInfo> GetJoinKeys() => JoinKeys;

    public Dictionary<string, List<ColumnInfo>> GetAll()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["SymbolRecord"] = GetColumns<SymbolRecord>(),
            ["ChunkRecord"] = GetColumns<ChunkRecord>(),
            ["RelationshipRecord"] = GetColumns<RelationshipRecord>(),
            ["RelationshipWithNames"] = GetColumns<RelationshipWithNamesRecord>(),
            ["SymbolReferenceStats"] = GetColumns<SymbolReferenceStatsRecord>(),
        };

    public string DescribeAll()
    {
        var sb = new System.Text.StringBuilder();

        foreach (var (table, columns) in GetAll())
        {
            sb.AppendLine($"  - {table}:");

            foreach (var c in columns)
            {
                var tags = new List<string>();
                if (c.IsKey) tags.Add("key");
                if (c.IsVector) tags.Add("vector");
                var tagStr = tags.Count > 0 ? $" [{string.Join(", ", tags)}]" : "";
                sb.AppendLine($"    - {c.Name}: {c.Type}{tagStr}");
            }
        }

        return sb.ToString();
    }

    public string DescribeJoinKeys()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("  Join relationships:");

        foreach (var key in JoinKeys)
        {
            sb.AppendLine(
                $"    - {key.SourceTable}.{key.SourceColumn} → {key.TargetTable}.{key.TargetColumn}");
            sb.AppendLine($"      ({key.Description})");
        }

        return sb.ToString();
    }

    public string DescribeVirtualTables()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("  Virtual Tables (denormalized for convenience):");
        sb.AppendLine("  - RelationshipWithNames: Relationships with symbol names resolved");
        sb.AppendLine("    - SourceName, SourceKind, SourceFullName, SourceFilePath");
        sb.AppendLine("    - TargetName, TargetKind, TargetFullName, TargetFilePath");
        sb.AppendLine("    - No JOIN needed - query directly by TargetKind='Class' etc.");
        sb.AppendLine("  - SymbolReferenceStats: Pre-aggregated reference counts per symbol");
        sb.AppendLine("    - IncomingReferences, OutgoingReferences (total counts)");
        sb.AppendLine("    - IncomingCalls, OutgoingCalls (calls only)");
        sb.AppendLine("    - IncomingInherits, IncomingImplements (inheritance/implementation)");
        sb.AppendLine("    - Name, Kind, FullName, FilePath included directly");

        return sb.ToString();
    }
}
