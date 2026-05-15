using CodeMemory.Storage;
using System.Reflection;

namespace CodeMemory.AspNet.SqlQuery;

public sealed class TableSchemaProvider
{
    public record ColumnInfo(string Name, string Type, bool IsNullable, bool IsKey, bool IsVector, string? StorageName);

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

    public Dictionary<string, List<ColumnInfo>> GetAll()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["SymbolRecord"] = GetColumns<SymbolRecord>(),
            ["ChunkRecord"] = GetColumns<ChunkRecord>(),
            ["RelationshipRecord"] = GetColumns<RelationshipRecord>(),
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
}
