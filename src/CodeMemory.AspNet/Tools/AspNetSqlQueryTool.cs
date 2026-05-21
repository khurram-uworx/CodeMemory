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

    static IDictionary<string, object?> fail(string error, Stopwatch sw)
    {
        sw.Stop();

        return new Dictionary<string, object?>
        {
            ["success"] = false,
            ["rowCount"] = 0,
            ["executionTimeMs"] = sw.ElapsedMilliseconds,
            ["columns"] = null,
            ["rows"] = null,
            ["error"] = error
        };
    }

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

    readonly SqlQueryParser parser = new();
    readonly IStorageService storageService;
    readonly ILogger<AspNetSqlQueryTool> logger;

    public AspNetSqlQueryTool(IStorageService storageService, ILogger<AspNetSqlQueryTool> logger)
    {
        this.storageService = storageService;
        this.logger = logger;
    }

    HybridStorageService? resolveHybridStorage()
    {
        var actualStorage = storageService is StorageServiceRouter router
            ? router.GetStorage()
            : storageService;

        return actualStorage as HybridStorageService;
    }

    (bool Success, string TableName, string? Error) validateQuery(string query)
    {
        Sequence<Statement> statements;

        try
        {
            statements = parser.Parse(query.AsSpan(), Dialect);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"Parse error: {ex.Message}");
        }

        if (statements.Count != 1)
            return (false, string.Empty, "Only single-statement queries are supported");

        if (statements[0] is not Statement.Select select)
            return (false, string.Empty, "Only SELECT statements are supported");

        if (select.Query.Body is not SetExpression.SelectExpression selectExpression)
            return (false, string.Empty, "Only simple SELECT queries are supported");

        var selectBody = selectExpression.Select;
        if (selectBody.From is null || selectBody.From.Count == 0)
            return (false, string.Empty, "SELECT must have a FROM clause with a table name");

        if (selectBody.From.Count > 1 || selectBody.From[0].Joins is { Count: > 0 })
            return (false, string.Empty, "JOINs and multiple FROM tables are not supported by this tool");

        var tableName = extractTableName(selectBody.From[0].Relation);
        if (tableName is null)
            return (false, string.Empty, "Could not determine table name from FROM clause");

        if (!tableName.Equals("SymbolRecord", StringComparison.OrdinalIgnoreCase)
            && !tableName.Equals("RelationshipRecord", StringComparison.OrdinalIgnoreCase)
            && !tableName.Equals("ChunkRecord", StringComparison.OrdinalIgnoreCase))
        {
            return (false, string.Empty,
                "Unknown table. Available tables: SymbolRecord, RelationshipRecord. ChunkRecord is queried via semantic_search.");
        }

        return (true, tableName, null);
    }

    [McpServerTool, Description(@"
Execute SELECT-only SQL queries against the relational storage backend.

TABLES:
  - SymbolRecord: Id, Name, Kind, FilePath, LineStart, LineEnd, FullName, Modifiers, Documentation
  - RelationshipRecord: Id, SourceSymbolId, TargetSymbolId, RelationshipType

ChunkRecord queries are not supported via SQL in this backend. Use semantic_search instead.

The tool validates that the statement is a single SELECT query, translates SymbolRecord/RelationshipRecord
to the provider tables, executes on the current repo database, and returns JSON:
success, rowCount, executionTimeMs, columns, rows, error.
")]
    public async Task<IDictionary<string, object?>> SqlQueryAsync(
        [Description("SQL query string, e.g. SELECT * FROM SymbolRecord WHERE Kind = 'Class' LIMIT 10")]
        string query,
        [Description("Maximum number of rows to return from the result stream (1-10000, default 100)")]
        int maxResults = 100)
    {
        var sw = Stopwatch.StartNew();
        var cappedMaxResults = Math.Clamp(maxResults, 1, 10000);

        try
        {
            var validation = validateQuery(query);
            if (!validation.Success)
                return fail(validation.Error!, sw);

            if (validation.TableName.Equals("ChunkRecord", StringComparison.OrdinalIgnoreCase))
                return fail("ChunkRecord queries not supported via SQL in this backend. Use semantic_search tool instead.", sw);

            var hybridStorage = resolveHybridStorage();
            if (hybridStorage is null)
                return fail("sql_query requires HybridStorageService. The current storage provider does not expose relational SQL storage.", sw);

            await using var db = hybridStorage.CreateDbContext();
            var translatedSql = translateLogicalTableNames(query, db);
            var rows = await executeQueryAsync(db, translatedSql, cappedMaxResults, CancellationToken.None);

            sw.Stop();

            return new Dictionary<string, object?>
            {
                ["success"] = true,
                ["rowCount"] = rows.Rows.Count,
                ["executionTimeMs"] = sw.ElapsedMilliseconds,
                ["columns"] = rows.Columns,
                ["rows"] = rows.Rows,
                ["error"] = null
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SQL query execution failed: {Query}", query);
            return fail($"Query execution failed: {ex.Message}", sw);
        }
    }
}
