using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Storage;
using CodeMemory.Storage;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodeMemory.AspNet.Tools;

public sealed record AspNetSqlQueryResult(
    bool Success,
    long RowCount,
    long ExecutionTimeMs,
    List<string>? Columns,
    List<Dictionary<string, object?>>? Rows,
    string? Error
);

[McpServerToolType]
public sealed class AspNetSqlQueryTool
{
    static bool isIdentifierStart(char ch)
        => char.IsLetter(ch) || ch == '_';

    static bool isIdentifierPart(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_';

    static string quoteSqlServerIdentifier(string identifier)
        => $"[{identifier.Replace("]", "]]")}]";

    static string quoteDoubleQuotedIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    static string? extractTableName(TableFactor? factor)
        => factor is TableFactor.Table table
            ? table.Name.Values.Last().Value
            : null;

    static string quoteColumnIdentifier(string providerName, string columnName)
        => providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? quoteSqlServerIdentifier(columnName)
            : quoteDoubleQuotedIdentifier(columnName);

    static string translateLogicalTableNames(string sql, CodeMemoryDbContext db)
    {
        var providerName = db.Database.ProviderName ?? string.Empty;
        var symbolTable = qualifiedTableName(providerName, db.Schema, "symbols");
        var relationshipTable = qualifiedTableName(providerName, db.Schema, "relationships");
        var columnNames = logicalColumnNames(providerName);

        return replaceIdentifiers(sql, identifier => identifier switch
        {
            var name when name.Equals("SymbolRecord", StringComparison.OrdinalIgnoreCase) => symbolTable,
            var name when name.Equals("RelationshipRecord", StringComparison.OrdinalIgnoreCase) => relationshipTable,
            var name when columnNames.TryGetValue(name, out var columnName) => columnName,
            _ => null
        });
    }

    static Dictionary<string, string> logicalColumnNames(string providerName)
    {
        var columnNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = "id",
            ["Name"] = "name",
            ["Kind"] = "kind",
            ["FilePath"] = "file_path",
            ["LineStart"] = "line_start",
            ["LineEnd"] = "line_end",
            ["FullName"] = "full_name",
            ["Modifiers"] = "modifiers",
            ["Documentation"] = "documentation",
            ["SourceSymbolId"] = "source_symbol_id",
            ["TargetSymbolId"] = "target_symbol_id",
            ["RelationshipType"] = "relationship_type"
        };

        return columnNames.ToDictionary(
            pair => pair.Key,
            pair => quoteColumnIdentifier(providerName, pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    static string qualifiedTableName(string providerName, string schema, string tableName)
    {
        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            return $"{quoteSqlServerIdentifier(schema)}.{quoteSqlServerIdentifier(tableName)}";

        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return quoteDoubleQuotedIdentifier(tableName);

        return $"{quoteDoubleQuotedIdentifier(schema)}.{quoteDoubleQuotedIdentifier(tableName)}";
    }

    static string replaceIdentifiers(string sql, Func<string, string?> replacement)
    {
        var builder = new StringBuilder(sql.Length);

        for (var i = 0; i < sql.Length;)
        {
            var ch = sql[i];
            if (ch == '\'')
            {
                var end = copyQuoted(sql, i, '\'', builder);
                i = end;
                continue;
            }

            if (ch == '"')
            {
                var end = copyQuoted(sql, i, '"', builder);
                i = end;
                continue;
            }

            if (isIdentifierStart(ch))
            {
                var start = i;
                i++;
                while (i < sql.Length && isIdentifierPart(sql[i]))
                    i++;

                var identifier = sql[start..i];
                builder.Append(replacement(identifier) ?? identifier);
                continue;
            }

            builder.Append(ch);
            i++;
        }

        return builder.ToString();
    }

    static int copyQuoted(string sql, int start, char quote, StringBuilder builder)
    {
        builder.Append(sql[start]);
        var i = start + 1;

        while (i < sql.Length)
        {
            builder.Append(sql[i]);

            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    builder.Append(sql[i + 1]);
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return i;
    }

    static async Task<(List<string> Columns, List<Dictionary<string, object?>> Rows)> executeQueryAsync(
        CodeMemoryDbContext db,
        string sql,
        int maxResults,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToList();

        var rows = new List<Dictionary<string, object?>>();
        while (rows.Count < maxResults && await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < reader.FieldCount; i++)
                row[columns[i]] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);

            rows.Add(row);
        }

        return (columns, rows);
    }

    static readonly GenericDialect Dialect = new();

    static readonly Dictionary<string, List<(string Name, string Type, bool IsKey, bool IsNullable)>> DescribeSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SymbolRecord"] =
        [
            ("Id", "string", true, false),
            ("Name", "string", false, false),
            ("Kind", "string", false, false),
            ("FilePath", "string", false, false),
            ("LineStart", "int", false, false),
            ("LineEnd", "int", false, false),
            ("FullName", "string", false, false),
            ("Modifiers", "string", false, true),
            ("Documentation", "string", false, true),
        ],
        ["RelationshipRecord"] =
        [
            ("Id", "string", true, false),
            ("SourceSymbolId", "string", false, false),
            ("TargetSymbolId", "string", false, false),
            ("RelationshipType", "string", false, false),
        ],
    };

    static string unwrapMessage(Exception ex)
        => ex.InnerException?.Message ?? ex.Message;

    readonly SqlQueryParser parser = new();
    readonly IStorageService storageService;
    readonly ILogger<AspNetSqlQueryTool> logger;

    public AspNetSqlQueryTool(IStorageService storageService, ILogger<AspNetSqlQueryTool> logger)
    {
        this.storageService = storageService;
        this.logger = logger;
    }

    static AspNetSqlQueryResult? tryDescribe(string sql, Stopwatch sw)
    {
        var trimmed = sql.Trim();
        ReadOnlySpan<char> rest;

        if (trimmed.StartsWith("DESCRIBE ", StringComparison.OrdinalIgnoreCase))
            rest = trimmed.AsSpan(9).Trim();
        else if (trimmed.StartsWith("DESC ", StringComparison.OrdinalIgnoreCase))
            rest = trimmed.AsSpan(5).Trim();
        else
            return null;

        if (rest.Equals("TABLES", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (var (table, _) in DescribeSchemas)
                rows.Add(new Dictionary<string, object?> { ["Name"] = table });

            sw.Stop();
            return new AspNetSqlQueryResult(true, rows.Count, sw.ElapsedMilliseconds, ["Name"], rows, null);
        }

        var tableName = rest.ToString();
        if (!DescribeSchemas.TryGetValue(tableName, out var columns))
        {
            sw.Stop();
            return new AspNetSqlQueryResult(false, 0, sw.ElapsedMilliseconds, null, null,
                $"Unknown table '{tableName}'. Available tables: SymbolRecord, RelationshipRecord (ChunkRecord is not queryable via SQL in this backend — use semantic_search tool).");
        }

        var resultRows = columns.Select(c => new Dictionary<string, object?>
        {
            ["Name"] = c.Name,
            ["Type"] = c.Type,
            ["IsKey"] = c.IsKey,
            ["IsNullable"] = c.IsNullable,
        }).ToList();

        sw.Stop();
        return new AspNetSqlQueryResult(true, resultRows.Count, sw.ElapsedMilliseconds,
            ["Name", "Type", "IsKey", "IsNullable"], resultRows, null);
    }

    HybridStorageService? resolveHybridStorage()
    {
        var actualStorage = storageService is StorageServiceRouter router
            ? router.GetStorage()
            : storageService;

        return actualStorage as HybridStorageService;
    }

    (string? Error, bool IsValid) validateQuery(string query)
    {
        try
        {
            var statements = parser.Parse(query.AsSpan(), Dialect);

            if (statements.Count != 1)
                return ("Only single-statement queries are supported", false);

            if (statements[0] is not Statement.Select)
                return ("Only SELECT statements are supported", false);

            return (null, true);
        }
        catch (Exception ex)
        {
            return ($"Parse error: {ex.Message}", false);
        }
    }

    static bool containsTableReference(string sql, string tableName)
    {
        for (var i = 0; i < sql.Length;)
        {
            var ch = sql[i];

            if (ch is '\'' or '"')
            {
                i++;
                while (i < sql.Length && sql[i] != ch)
                {
                    if (sql[i] == '\\')
                        i++;
                    i++;
                }

                if (i < sql.Length)
                    i++;
                continue;
            }

            if (isIdentifierStart(ch))
            {
                var start = i;
                i++;
                while (i < sql.Length && isIdentifierPart(sql[i]))
                    i++;

                if (sql[start..i].Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            i++;
        }

        return false;
    }

    [McpServerTool, Description(@"
Execute SELECT-only SQL queries against the relational storage backend.
The query is forwarded to the underlying database engine (PostgreSQL or SQL Server)
after translating logical table/column names to physical names.

Only SELECT is supported. No INSERT/UPDATE/DELETE/CREATE.

TABLES:
  - SymbolRecord: Id, Name, Kind, FilePath, LineStart, LineEnd, FullName, Modifiers, Documentation
  - RelationshipRecord: Id, SourceSymbolId, TargetSymbolId, RelationshipType

ChunkRecord queries are not supported via SQL — chunks live in the vector store.
Use semantic_search instead.

SYNTAX:
  [WITH cte AS (SELECT ...)] SELECT [DISTINCT] cols|*|aggr FROM t [[AS] a]
    [JOIN t [[AS] a] ON c] [WHERE c [AND|OR ...]] [GROUP BY c]
    [HAVING c] [ORDER BY c [ASC|DESC]] [OFFSET m ROWS] [FETCH NEXT n ROWS ONLY]
  Strings use single quotes (''). Use double-quoted aliases: AS ""Alias"".
  Column names in results default to physical snake_case names (e.g., ""full_name"")
  unless aliased with AS.
  CTEs (non-recursive, chained), derived tables FROM (subquery) AS alias,
  self-joins, table aliases, and numeric ORDER BY (1-based positions) — all supported.

OPERATORS: =, <>, <, >, <=, >=, LIKE, CONCAT, IN(...), IS NULL, IS NOT NULL, BETWEEN
AND, OR, NOT, +, -, *, /

AGGREGATES: COUNT(*|col), SUM, AVG, MIN, MAX — use AS alias

EXAMPLES:
  SELECT * FROM SymbolRecord WHERE Kind = 'Class' ORDER BY Name OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY
  SELECT DISTINCT Kind FROM SymbolRecord
  SELECT FilePath, COUNT(*) AS cnt FROM SymbolRecord GROUP BY FilePath HAVING cnt > 1 ORDER BY cnt DESC
  SELECT Name, LineEnd - LineStart AS Length FROM SymbolRecord ORDER BY Length DESC OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY
  SELECT Id AS ""Id"", Name AS ""Name"", Kind AS ""Kind"" FROM SymbolRecord WHERE Kind = 'Class'
  SELECT s.Name, r.RelationshipType FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.SourceSymbolId
  SELECT Kind, AVG(LineEnd - LineStart) AS avgLen, COUNT(*) AS cnt FROM SymbolRecord GROUP BY Kind ORDER BY avgLen DESC
  SELECT Name, LineEnd - LineStart AS Length FROM SymbolRecord WHERE LineEnd - LineStart BETWEEN 5 AND 50 ORDER BY Length DESC
  SELECT * FROM SymbolRecord WHERE Id IN (SELECT SourceSymbolId FROM RelationshipRecord WHERE RelationshipType = 'References')
  SELECT CONCAT(Name, '::', Kind) AS combined FROM SymbolRecord WHERE Kind IN ('Class', 'Interface') ORDER BY Name OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY
  SELECT s.Name, COUNT(*) AS cnt FROM SymbolRecord s JOIN RelationshipRecord r ON s.Id = r.TargetSymbolId GROUP BY s.Name ORDER BY cnt DESC
  SELECT c.Name, COUNT(*) AS cnt FROM SymbolRecord c, SymbolRecord m WHERE m.Kind = 'Method' AND m.FullName LIKE CONCAT(c.FullName, '.%') AND c.Kind = 'Class' GROUP BY c.Name ORDER BY cnt DESC

RETURNS JSON: success, rowCount, executionTimeMs, columns, rows, error
")]
    public async Task<AspNetSqlQueryResult> SqlQueryAsync(
        [Description(@"SQL query string (e.g. SELECT * FROM SymbolRecord WHERE Kind = 'Class' ORDER BY Name OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY). Use single quotes for strings, double quotes for aliases. Logical table/column names are auto-translated to physical names.")]
        string query,
        [Description("Maximum number of rows to return from the result stream (1-10000, default 100)")]
        int maxResults = 100)
    {
        var sw = Stopwatch.StartNew();
        var cappedMaxResults = Math.Clamp(maxResults, 1, 10000);

        try
        {
            var describeResult = tryDescribe(query, sw);
            if (describeResult is not null)
                return describeResult;

            var (error, isValid) = validateQuery(query);
            if (!isValid)
            {
                sw.Stop();
                return new AspNetSqlQueryResult(false, 0, sw.ElapsedMilliseconds, null, null, error);
            }

            if (containsTableReference(query, "ChunkRecord"))
            {
                sw.Stop();
                return new AspNetSqlQueryResult(false, 0, sw.ElapsedMilliseconds, null, null,
                    "ChunkRecord queries not supported via SQL in this backend. Use semantic_search tool instead.");
            }

            var hybridStorage = resolveHybridStorage();
            if (hybridStorage is null)
            {
                sw.Stop();
                return new AspNetSqlQueryResult(false, 0, sw.ElapsedMilliseconds, null, null,
                    "sql_query requires HybridStorageService. The current storage provider does not expose relational SQL storage.");
            }

            await using var db = hybridStorage.CreateDbContext();
            var translatedSql = translateLogicalTableNames(query, db);
            var (columns, rows) = await executeQueryAsync(db, translatedSql, cappedMaxResults, CancellationToken.None);

            sw.Stop();
            return new AspNetSqlQueryResult(true, rows.Count, sw.ElapsedMilliseconds, columns, rows, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SQL query execution failed: {Query}", query);
            sw.Stop();
            return new AspNetSqlQueryResult(false, 0, sw.ElapsedMilliseconds, null, null, $"Query execution failed: {unwrapMessage(ex)}");
        }
    }
}
