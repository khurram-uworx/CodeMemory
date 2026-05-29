using CodeMemory.Mcp.SqlQuery;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CodeMemory.Mcp;

/// <summary>MCP resources that expose the SQL table schemas for agent-side discovery.</summary>
[McpServerResourceType]
public sealed class SchemaResources
{
    readonly TableSchemaProvider schemaProvider;
    readonly ILogger<SchemaResources> logger;

    /// <summary>Initializes a new instance of <see cref="SchemaResources"/>.</summary>
    public SchemaResources(TableSchemaProvider schemaProvider, ILogger<SchemaResources> logger)
    {
        this.schemaProvider = schemaProvider;
        this.logger = logger;
    }

    /// <summary>Lists all queryable SQL tables with column counts and descriptions.</summary>
    [McpServerResource(UriTemplate = "codememory://schema/tables", Name = "SQL Tables", MimeType = "application/json")]
    [Description("Lists all queryable SQL tables with column counts and descriptions.")]
    public Task<string> GetTablesAsync(CancellationToken ct = default)
    {
        var allSchemas = schemaProvider.GetAll();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SymbolRecord"] = "Code symbols extracted from the repository (classes, methods, interfaces, properties, etc.)",
            ["ChunkRecord"] = "Code and documentation text chunks with optional vector embeddings for similarity search",
            ["RelationshipRecord"] = "Dependency relationships between symbols (calls, references, inheritance, implements)",
        };

        var tables = allSchemas.Select(kvp => new
        {
            name = kvp.Key,
            columnCount = kvp.Value.Count,
            description = descriptions.GetValueOrDefault(kvp.Key, "")
        }).ToList();

        return Task.FromResult(JsonSerializer.Serialize(new { tables }));
    }

    /// <summary>Column schema for the SymbolRecord table — code symbols (classes, methods, etc.)</summary>
    [McpServerResource(UriTemplate = "codememory://schema/SymbolRecord", Name = "SymbolRecord Schema", MimeType = "application/json")]
    [Description("Column schema for the SymbolRecord table — code symbols (classes, methods, etc.)")]
    public Task<string> GetSymbolRecordSchemaAsync(CancellationToken ct = default)
        => Task.FromResult(serializeTable("SymbolRecord"));

    /// <summary>Column schema for the ChunkRecord table — code/document chunks with embeddings</summary>
    [McpServerResource(UriTemplate = "codememory://schema/ChunkRecord", Name = "ChunkRecord Schema", MimeType = "application/json")]
    [Description("Column schema for the ChunkRecord table — code/document chunks with embeddings")]
    public Task<string> GetChunkRecordSchemaAsync(CancellationToken ct = default)
        => Task.FromResult(serializeTable("ChunkRecord"));

    /// <summary>Column schema for the RelationshipRecord table — dependency relationships between symbols</summary>
    [McpServerResource(UriTemplate = "codememory://schema/RelationshipRecord", Name = "RelationshipRecord Schema", MimeType = "application/json")]
    [Description("Column schema for the RelationshipRecord table — dependency relationships between symbols")]
    public Task<string> GetRelationshipRecordSchemaAsync(CancellationToken ct = default)
        => Task.FromResult(serializeTable("RelationshipRecord"));

    string serializeTable(string tableName)
    {
        var allSchemas = schemaProvider.GetAll();
        if (!allSchemas.TryGetValue(tableName, out var columns))
        {
            logger.LogWarning("Unknown table '{Table}' requested from schema resource", tableName);
            return JsonSerializer.Serialize(new { error = $"Unknown table '{tableName}'" });
        }

        var result = new
        {
            tableName,
            columns = columns.Select(c => new
            {
                name = c.Name,
                type = c.Type,
                isKey = c.IsKey,
                isNullable = c.IsNullable,
                isVector = c.IsVector,
                storageName = c.StorageName
            })
        };

        return JsonSerializer.Serialize(result);
    }
}
