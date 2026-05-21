using CodeMemory.Mcp.SqlQuery;
using CodeMemory.Storage;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CodeMemory.Mcp.Tools;

[McpServerToolType]
public sealed class SqlQueryTool
{
    static readonly Lock failedQueriesLock = new();

    readonly IStorageService storageService;
    readonly SqlQueryService sqlQueryService;
    readonly TableSchemaProvider schemaProvider;
    readonly ILogger<SqlQueryTool> logger;

    public SqlQueryTool(
        IStorageService storageService,
        SqlQueryService sqlQueryService,
        TableSchemaProvider schemaProvider,
        ILogger<SqlQueryTool> logger)
    {
        this.storageService = storageService;
        this.sqlQueryService = sqlQueryService;
        this.schemaProvider = schemaProvider;
        this.logger = logger;
    }

    [McpServerTool, Description(@"
Execute SELECT-only SQL queries against the indexed repository.

Only SELECT is supported. No INSERT/UPDATE/DELETE/CREATE.

TABLES: SymbolRecord, ChunkRecord (incl. vector search & text files), RelationshipRecord

SYNTAX:
  [WITH cte AS (SELECT ...)] SELECT [DISTINCT] cols|*|aggr FROM t [[AS] a]
    [JOIN t [[AS] a] ON c] [WHERE c [AND|OR ...]] [GROUP BY c]
    [HAVING c] [ORDER BY c [ASC|DESC]] [LIMIT n]
  Strings use single quotes (''). Table aliases required for multi-table queries.
  JOINs (INNER/LEFT/CROSS), self-joins, CTEs (non-recursive, chained),
  derived tables FROM (subquery) AS alias — all supported.

OPERATORS: =, <>, <, >, <=, >=, LIKE, ILIKE, IN(...), IS NULL, IS NOT NULL, BETWEEN

AGGREGATES: COUNT(*|col), SUM, AVG, MIN, MAX — use AS alias

VECTOR SEARCH (ChunkRecord only):
  SELECT ... FROM ChunkRecord WHERE Content LIKE '%text%' ORDER BY Similarity DESC
  Returns __score (0-1). Also works on CTE/derived-table outer queries.
  Not supported with multi-table queries.

BEHAVIOR:
  - ORDER BY: column names, aliases, numeric positions (1-based), multiple expressions
  - GROUP BY, DISTINCT, HAVING, aggregates applied client-side
  - Arithmetic +,-,*,/ and string || supported in SELECT; parenthesized WHERE conditions
  - LIMIT overrides the maxResults parameter
  - .md/.txt files indexed as ChunkRecord with Language = 'Text'
  - Only InMemoryVectorStore backend; other backends return error

EXAMPLES:
  SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10
  SELECT DISTINCT Kind FROM SymbolRecord
  SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord GROUP BY FilePath HAVING cnt > 1 ORDER BY cnt DESC
  SELECT Name, LineEnd - LineStart AS Length FROM SymbolRecord ORDER BY Length DESC LIMIT 10
  SELECT * FROM ChunkRecord WHERE Content ILIKE '%auth%' ORDER BY Similarity DESC LIMIT 5
  SELECT * FROM ChunkRecord WHERE Language = 'Text'
  WITH counts AS (SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord GROUP BY FilePath) SELECT * FROM counts ORDER BY cnt DESC
  SELECT Name, Kind FROM (SELECT * FROM SymbolRecord) AS sub WHERE sub.Kind = 'Method'
  SELECT s.Name, COUNT(*) AS cnt FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.TargetSymbolId GROUP BY s.Name ORDER BY cnt DESC

RETURNS JSON: success, rowCount, executionTimeMs, columns, rows, error
")]
    public async Task<IDictionary<string, object?>> SqlQueryAsync(
        [Description("SQL query string (e.g. SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10)")]
        string query,
        [Description("Maximum number of results to return (1-10000, default 100)")]
        int maxResults = 100)
    {
        var repoRoot = storageService.RepoRoot;
        var vectorStore = storageService?.Store;

        if (vectorStore is null)
        {
            logger.LogWarning("SqlQuery attempted without VectorStore backend");
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = "SQL queries require the InMemoryVectorStore storage provider. Current provider does not expose a VectorStore.",
                ["available"] = false
            };
        }

        Action<Exception?, string?> logFailure = (ex, error) =>
        {
            if (ex is not null)
                logger.LogError(ex, "SQL query execution failed");
            else
                logger.LogError("SQL query execution failed");
            logger.LogError(query);

            string toWrite = string.Format("{1}{0}{2}{0}",
                Environment.NewLine, repoRoot, query);

            if (ex is not null)
                toWrite += $"{ex}{Environment.NewLine}";
            if (!string.IsNullOrEmpty(error))
                toWrite += $"{error}{Environment.NewLine}";

            string path = Path.Combine(AppContext.BaseDirectory, "failed-queries.txt");
            lock (failedQueriesLock)
                File.AppendAllText(path, toWrite);
        };

        try
        {
            var result = await sqlQueryService.ExecuteAsync(vectorStore, query, maxResults);

            if (!result.Success)
                logFailure(null, result.Error);

            return new Dictionary<string, object?>
            {
                ["success"] = result.Success,
                ["rowCount"] = result.RowCount,
                ["executionTimeMs"] = result.ExecutionTimeMs,
                ["columns"] = result.Columns,
                ["rows"] = result.Rows is not null
                    ? result.Rows.Select(r => (object)r).ToList()
                    : null,
                ["error"] = result.Error
            };
        }
        catch (Exception ex)
        {
            logFailure(ex, null);

            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error"] = $"Query execution failed: {ex.Message}",
                ["query"] = query
            };
        }
    }
}
