using System.Collections.Specialized;
using System.Text.Json;

namespace CodeMemory.Storage.LiteGraph;

using LEdge = global::LiteGraph.Edge;
using LNode = global::LiteGraph.Node;
using LVecSearchResult = global::LiteGraph.VectorSearchResult;
using LVectorMetadata = global::LiteGraph.VectorMetadata;

static class MappingHelpers
{
    static string? stripPrefix(string? name)
    {
        if (name is null) return null;
        if (name.StartsWith("s:") || name.StartsWith("c:"))
            return name[2..];

        return name;
    }

    static string serializeData<T>(T record)
        => JsonSerializer.Serialize(record, JsonOptions);

    static T? deserializeData<T>(object? data) where T : class
    {
        if (data is null) return null;

        if (data is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
                return JsonSerializer.Deserialize<T>(je.GetString()!, JsonOptions);

            return JsonSerializer.Deserialize<T>(je.GetRawText(), JsonOptions);
        }

        if (data is string s)
            return JsonSerializer.Deserialize<T>(s, JsonOptions);

        return null;
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static SymbolRecord NodeToSymbolRecord(LNode node)
    {
        var record = deserializeData<SymbolRecord>(node.Data) ?? new SymbolRecord();
        record.Id = stripPrefix(node.Name) ?? record.Id;

        if (node.Labels is { Count: > 0 })
            record.Kind = node.Labels[0];

        if (node.Tags is not null)
        {
            if (node.Tags["name"] is { } n)
                record.Name = n;
            if (node.Tags["filePath"] is { } fp)
                record.FilePath = fp;
            if (node.Tags["fullName"] is { } fn)
                record.FullName = fn;
            if (node.Tags["modifiers"] is { } mod)
                record.Modifiers = mod;
            if (node.Tags["documentation"] is { } doc)
                record.Documentation = doc;
        }

        return record;
    }

    public static RelationshipRecord EdgeToRelationshipRecord(LEdge edge)
    {
        var record = deserializeData<RelationshipRecord>(edge.Data) ?? new RelationshipRecord();
        record.Id = edge.Name ?? record.Id;
        if (edge.Labels is { Count: > 0 })
            record.RelationshipType = edge.Labels[0];

        if (edge.Tags is not null)
        {
            if (edge.Tags["sourceSymbolId"] is { } src)
                record.SourceSymbolId = src;
            if (edge.Tags["targetSymbolId"] is { } tgt)
                record.TargetSymbolId = tgt;
        }

        return record;
    }

    public static ChunkRecord NodeToChunkRecord(LNode node)
    {
        var record = deserializeData<ChunkRecord>(node.Data) ?? new ChunkRecord();
        record.Id = stripPrefix(node.Name) ?? record.Id;

        if (node.Tags is not null)
        {
            if (node.Tags["symbolId"] is { } sid)
                record.SymbolId = sid;
            if (node.Tags["filePath"] is { } fp)
                record.FilePath = fp;
            if (node.Tags["language"] is { } lang)
                record.Language = lang;
            if (node.Tags["lineStart"] is { } ls && int.TryParse(ls, out var lsVal))
                record.LineStart = lsVal;
            if (node.Tags["lineEnd"] is { } le && int.TryParse(le, out var leVal))
                record.LineEnd = leVal;
        }

        if (node.Vectors is { Count: > 0 })
        {
            var vec = node.Vectors[0];
            if (vec.Vectors is { Count: > 0 })
            {
                var arr = new float[vec.Vectors.Count];
                for (int i = 0; i < vec.Vectors.Count; i++)
                    arr[i] = vec.Vectors[i];
                record.Embedding = arr;
            }
        }

        return record;
    }

    public static ScoredChunk VectorSearchResultToScoredChunk(LVecSearchResult result)
    {
        var chunk = result.Node is not null
            ? NodeToChunkRecord(result.Node)
            : new ChunkRecord();

        return new ScoredChunk
        {
            Chunk = chunk,
            Score = result.Score ?? 0
        };
    }

    public static LNode SymbolRecordToNode(SymbolRecord record, Guid tenantGuid, Guid graphGuid)
    {
        var tags = new NameValueCollection();

        if (!string.IsNullOrEmpty(record.Name)) tags["name"] = record.Name;
        if (!string.IsNullOrEmpty(record.FilePath)) tags["filePath"] = record.FilePath;
        if (!string.IsNullOrEmpty(record.FullName)) tags["fullName"] = record.FullName;
        if (!string.IsNullOrEmpty(record.Modifiers)) tags["modifiers"] = record.Modifiers;
        if (!string.IsNullOrEmpty(record.Documentation)) tags["documentation"] = record.Documentation;

        return new LNode
        {
            TenantGUID = tenantGuid,
            GraphGUID = graphGuid,
            Name = "s:" + record.Id,
            Labels = new List<string> { record.Kind },
            Tags = tags,
            Data = serializeData(record)
        };
    }

    public static LNode ChunkRecordToNode(ChunkRecord record, Guid tenantGuid, Guid graphGuid)
    {
        var tags = new NameValueCollection();

        if (!string.IsNullOrEmpty(record.SymbolId)) tags["symbolId"] = record.SymbolId;
        if (!string.IsNullOrEmpty(record.FilePath)) tags["filePath"] = record.FilePath;
        if (!string.IsNullOrEmpty(record.Language)) tags["language"] = record.Language;

        tags["lineStart"] = record.LineStart.ToString();
        tags["lineEnd"] = record.LineEnd.ToString();

        // Strip embedding from stored data to avoid duplication (it's already in Vectors)
        var stripped = new ChunkRecord
        {
            Id = record.Id,
            SymbolId = record.SymbolId,
            FilePath = record.FilePath,
            Content = record.Content,
            Language = record.Language,
            LineStart = record.LineStart,
            LineEnd = record.LineEnd,
            MetadataJson = record.MetadataJson,
            Embedding = null
        };

        var node = new LNode
        {
            TenantGUID = tenantGuid,
            GraphGUID = graphGuid,
            Name = "c:" + record.Id,
            Labels = new List<string> { "Chunk" },
            Tags = tags,
            Data = serializeData(stripped)
        };

        if (record.Embedding.HasValue)
        {
            var span = record.Embedding.Value.Span;
            var floats = new List<float>(span.Length);

            for (int i = 0; i < span.Length; i++)
                floats.Add(span[i]);

            node.Vectors = new List<LVectorMetadata>
            {
                new LVectorMetadata
                {
                    TenantGUID = tenantGuid,
                    GraphGUID = graphGuid,
                    Model = "codememory-embedding",
                    Dimensionality = span.Length,
                    Content = record.Content,
                    Vectors = floats
                }
            };
        }

        return node;
    }

    public static LEdge RelationshipRecordToEdge(
        RelationshipRecord record, Guid tenantGuid, Guid graphGuid,
        Guid fromNodeGuid, Guid toNodeGuid)
    {
        var tags = new NameValueCollection();

        if (!string.IsNullOrEmpty(record.SourceSymbolId)) tags["sourceSymbolId"] = record.SourceSymbolId;
        if (!string.IsNullOrEmpty(record.TargetSymbolId)) tags["targetSymbolId"] = record.TargetSymbolId;

        return new LEdge
        {
            TenantGUID = tenantGuid,
            GraphGUID = graphGuid,
            Name = record.Id,
            Labels = new List<string> { record.RelationshipType },
            Tags = tags,
            From = fromNodeGuid,
            To = toNodeGuid,
            Data = serializeData(record)
        };
    }
}
