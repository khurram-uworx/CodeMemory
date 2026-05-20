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
    //readonly TableSchemaProvider schemaProvider;
    readonly ILogger<SqlQueryTool> logger;

    public SqlQueryTool(
        IStorageService storageService,
        SqlQueryService sqlQueryService,
        TableSchemaProvider schemaProvider,
        ILogger<SqlQueryTool> logger)
    {
        this.storageService = storageService;
        this.sqlQueryService = sqlQueryService;
        //this.schemaProvider = schemaProvider;
        this.logger = logger;
    }

    [McpServerTool, Description(@"
Execute SQL queries against the indexed repository data.

TABLES:
  - SymbolRecord: Id(key), Name(string), Kind(string), FilePath(string), LineStart(int), LineEnd(int), FullName(string), Modifiers(string?), Documentation(string?)
  - ChunkRecord: Id(key), SymbolId(string), FilePath(string), Content(string), Language(string), LineStart(int), LineEnd(int), MetadataJson(string?), Embedding(vector)
  - RelationshipRecord: Id(key), SourceSymbolId(string), TargetSymbolId(string), RelationshipType(string)

SYNTAX:
  [WITH cte_name AS (SELECT ...) [, ...]]
  SELECT [DISTINCT] columns|*|aggregates FROM table|(subquery) AS alias
    [WHERE condition [AND|OR ...]]
    [GROUP BY col] [HAVING condition]
    [ORDER BY col|position [ASC|DESC]]
    [LIMIT n]
  Strings MUST use single quotes: 'text' (not double quotes)
  Derived tables (subqueries in FROM clause) use the same syntax as CTE bodies

OPERATORS: =, <>, <, >, <=, >=, LIKE, ILIKE (case-insensitive), IN(...), IS NULL, IS NOT NULL, BETWEEN

AGGREGATES:
  COUNT(*|column) AS alias — row count (incl. null-free column count)
  SUM(column), AVG(column), MIN(column), MAX(column) AS alias

VECTOR SEARCH (ChunkRecord only):
  SELECT ... FROM ChunkRecord WHERE Content LIKE '%text%' ORDER BY Similarity DESC
  Each result row includes __score (0-1) for similarity ranking.

BEHAVIOR:
  - Explicit column list in SELECT returns only those columns
  - LIMIT clause overrides the maxResults tool parameter
   - ORDER BY supports column names, aliases, numeric positions (1-based), and multiple expressions (e.g. ORDER BY Kind, Name)
  - GROUP BY, DISTINCT, HAVING, and aggregates are applied client-side after fetching all matching rows
   - HAVING supports aggregate function references (COUNT(*) > 1) and column aliases (cnt > 1)
   - SELECT supports arithmetic expressions: +, -, *, / (e.g. SELECT LineEnd - LineStart AS Length FROM SymbolRecord)
   - String concatenation with || operator supported (e.g. SELECT Name || ':' || Kind AS combined)
   - Parenthesized WHERE conditions supported: WHERE (Kind = 'Class' OR Kind = 'Interface') AND Name LIKE '%Helper%'
   - CTEs (WITH ... AS ...) supported: non-recursive, chained CTEs work; CTE name shadows collection names
   - Derived tables (FROM (subquery) AS alias) supported: can reference CTEs, support nesting
    - ORDER BY Similarity DESC works on direct ChunkRecord queries and CTE/derived-table outer queries (re-ranks CTE results by cosine similarity against the store)
    - Only InMemoryVectorStore backend supported; other backends return an error

EXAMPLES:
  SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10
  SELECT DISTINCT Kind FROM SymbolRecord
  SELECT Name, Kind FROM SymbolRecord WHERE Kind IN ('Method', 'Function') ORDER BY 1
  SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord GROUP BY FilePath ORDER BY 2 DESC LIMIT 20
  SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord GROUP BY FilePath HAVING cnt > 1
  SELECT Kind, SUM(LineStart) AS total FROM SymbolRecord GROUP BY Kind
  SELECT Name, LineEnd - LineStart AS Length FROM SymbolRecord ORDER BY Length DESC LIMIT 10
  SELECT Kind, Name FROM SymbolRecord ORDER BY Kind, Name
  SELECT * FROM ChunkRecord WHERE Content ILIKE '%auth%' ORDER BY Similarity DESC LIMIT 5
  SELECT * FROM RelationshipRecord WHERE RelationshipType = 'Calls'
  WITH public_classes AS (SELECT * FROM SymbolRecord WHERE Modifiers LIKE '%public%') SELECT Name FROM public_classes ORDER BY Name
  WITH counts AS (SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord GROUP BY FilePath) SELECT * FROM counts ORDER BY cnt DESC LIMIT 10
  WITH csharp_chunks AS (SELECT * FROM ChunkRecord WHERE Language = 'CSharp') SELECT FilePath FROM csharp_chunks WHERE Content LIKE '%auth%' ORDER BY Similarity DESC LIMIT 3
  SELECT Name, Kind FROM (SELECT * FROM SymbolRecord) AS sub WHERE sub.Kind = 'Method' ORDER BY Name
  SELECT d.Kind, COUNT(*) AS cnt FROM (SELECT Kind FROM SymbolRecord) AS d GROUP BY d.Kind HAVING cnt > 1

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
