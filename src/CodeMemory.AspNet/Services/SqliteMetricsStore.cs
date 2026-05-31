using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Models;
using CodeMemory.AspNet.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CodeMemory.AspNet.Services;

sealed class SqliteMetricsStore : IMetricsStore, IDisposable
{
    static string SerializeTags(IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        if (tags is null || tags.Count == 0)
            return "";

        var sorted = tags
            .Select(kvp => $"{kvp.Key}={kvp.Value?.ToString() ?? ""}")
            .OrderBy(x => x, StringComparer.Ordinal);

        return string.Join("|", sorted);
    }

    static IReadOnlyList<KeyValuePair<string, string>>? DeserializeTags(string tagKey)
    {
        if (string.IsNullOrEmpty(tagKey))
            return null;

        return tagKey
            .Split('|')
            .Select(p =>
            {
                var eq = p.IndexOf('=');
                return eq >= 0
                    ? new KeyValuePair<string, string>(p[..eq], p[(eq + 1)..])
                    : new KeyValuePair<string, string>(p, "");
            })
            .ToList();
    }

    readonly LocalMetricsOptions options;
    readonly ILogger<SqliteMetricsStore>? logger;
    readonly string connectionString;
    readonly object writeLock = new();
    volatile bool initialized;
    SqliteConnection? connection;

    public SqliteMetricsStore(
        IOptions<LocalMetricsOptions> options,
        ILogger<SqliteMetricsStore>? logger = null)
    {
        this.options = options.Value;
        this.logger = logger;
        connectionString = "Data Source=App_Data/metrics.db";
    }

    internal SqliteMetricsStore(
        string connectionString,
        LocalMetricsOptions options,
        ILogger<SqliteMetricsStore>? logger = null)
    {
        this.connectionString = connectionString;
        this.options = options;
        this.logger = logger;
    }

    void ensureInitialized()
    {
        if (initialized) return;
        lock (writeLock)
        {
            if (initialized) return;

            var builder = new SqliteConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource;
            if (!string.IsNullOrEmpty(dataSource) && dataSource != ":memory:")
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }

            connection = new SqliteConnection(connectionString);
            connection.Open();

            using var initCmd = connection.CreateCommand();
            initCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout = 5000;";
            initCmd.ExecuteNonQuery();

            using var schemaCmd = connection.CreateCommand();
            schemaCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS MetricInstruments (
                    InstrumentName TEXT NOT NULL,
                    TagKey TEXT NOT NULL,
                    InstrumentType TEXT NOT NULL,
                    CounterValue INTEGER NOT NULL DEFAULT 0,
                    Count INTEGER NOT NULL DEFAULT 0,
                    Sum REAL NOT NULL DEFAULT 0,
                    Min REAL,
                    Max REAL,
                    Last REAL,
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    PRIMARY KEY (InstrumentName, TagKey)
                )
                """;
            schemaCmd.ExecuteNonQuery();

            if (options.MaxMeasurementsPerTagSet > 0)
            {
                schemaCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS MetricMeasurements (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        InstrumentName TEXT NOT NULL,
                        TagKey TEXT NOT NULL,
                        Value REAL NOT NULL,
                        Timestamp TEXT NOT NULL
                    )
                    """;
                schemaCmd.ExecuteNonQuery();

                schemaCmd.CommandText = """
                    CREATE INDEX IF NOT EXISTS IX_MetricMeasurements_Lookup
                    ON MetricMeasurements(InstrumentName, TagKey, Timestamp DESC)
                    """;
                schemaCmd.ExecuteNonQuery();
            }

            initialized = true;
        }
    }

    SqliteConnection getConnection()
    {
        ensureInitialized();
        return connection!;
    }

    bool isUnderTagLimit(SqliteConnection conn, string instrumentName, string tagKey)
    {
        if (options.MaxUniqueTagCombinations <= 0)
            return true;

        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT 1 FROM MetricInstruments WHERE InstrumentName = @name AND TagKey = @tagKey";
        checkCmd.Parameters.AddWithValue("@name", instrumentName);
        checkCmd.Parameters.AddWithValue("@tagKey", tagKey);
        if (checkCmd.ExecuteScalar() is not null)
            return true;

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM MetricInstruments WHERE InstrumentName = @name";
        countCmd.Parameters.AddWithValue("@name", instrumentName);
        var count = (long)countCmd.ExecuteScalar()!;

        if (count >= options.MaxUniqueTagCombinations)
        {
            logger?.LogWarning(
                "MaxUniqueTagCombinations ({Limit}) reached for instrument '{Instrument}'. Dropping tag set: {TagKey}",
                options.MaxUniqueTagCombinations, instrumentName, tagKey);
            return false;
        }

        return true;
    }

    void insertMeasurement(SqliteConnection conn, string instrumentName, string tagKey, double value)
    {
        if (options.MaxMeasurementsPerTagSet <= 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MetricMeasurements (InstrumentName, TagKey, Value, Timestamp)
            VALUES (@name, @tagKey, @value, @ts)
            """;
        cmd.Parameters.AddWithValue("@name", instrumentName);
        cmd.Parameters.AddWithValue("@tagKey", tagKey);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            DELETE FROM MetricMeasurements
            WHERE Id NOT IN (
                SELECT Id FROM MetricMeasurements
                WHERE InstrumentName = @name AND TagKey = @tagKey
            ORDER BY Timestamp DESC
                LIMIT @max
            )
            """;
        cmd.Parameters.AddWithValue("@name", instrumentName);
        cmd.Parameters.AddWithValue("@tagKey", tagKey);
        cmd.Parameters.AddWithValue("@max", options.MaxMeasurementsPerTagSet);
        cmd.ExecuteNonQuery();
    }

    IReadOnlyList<MetricMeasurement>? getMeasurements(SqliteConnection conn, string instrumentName, string tagKey)
    {
        if (options.MaxMeasurementsPerTagSet <= 0)
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Value, Timestamp FROM MetricMeasurements
            WHERE InstrumentName = @name AND TagKey = @tagKey
            ORDER BY Timestamp ASC
            LIMIT @max
            """;
        cmd.Parameters.AddWithValue("@name", instrumentName);
        cmd.Parameters.AddWithValue("@tagKey", tagKey);
        cmd.Parameters.AddWithValue("@max", options.MaxMeasurementsPerTagSet);

        var measurements = new List<MetricMeasurement>(options.MaxMeasurementsPerTagSet);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            measurements.Add(new MetricMeasurement(
                reader.GetDouble(0),
                DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return measurements.Count > 0 ? measurements : null;
    }

    public void RecordCounter(string instrumentName, long value, IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        var tagKey = SerializeTags(tags);
        var conn = getConnection();

        lock (writeLock)
        {
            if (!isUnderTagLimit(conn, instrumentName, tagKey))
                return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO MetricInstruments (InstrumentName, TagKey, InstrumentType, CounterValue)
                VALUES (@name, @tagKey, 'counter', @value)
                ON CONFLICT(InstrumentName, TagKey) DO UPDATE SET
                    CounterValue = CounterValue + @value,
                    UpdatedAt = datetime('now')
                """;
            cmd.Parameters.AddWithValue("@name", instrumentName);
            cmd.Parameters.AddWithValue("@tagKey", tagKey);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();

            insertMeasurement(conn, instrumentName, tagKey, value);
        }
    }

    public void RecordGauge(string instrumentName, double value, IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        var tagKey = SerializeTags(tags);
        var conn = getConnection();

        lock (writeLock)
        {
            if (!isUnderTagLimit(conn, instrumentName, tagKey))
                return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO MetricInstruments (InstrumentName, TagKey, InstrumentType, Last)
                VALUES (@name, @tagKey, 'gauge', @value)
                ON CONFLICT(InstrumentName, TagKey) DO UPDATE SET
                    Last = @value,
                    UpdatedAt = datetime('now')
                """;
            cmd.Parameters.AddWithValue("@name", instrumentName);
            cmd.Parameters.AddWithValue("@tagKey", tagKey);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();

            insertMeasurement(conn, instrumentName, tagKey, value);
        }
    }

    public void RecordHistogram(string instrumentName, double value, IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        var tagKey = SerializeTags(tags);
        var conn = getConnection();

        lock (writeLock)
        {
            if (!isUnderTagLimit(conn, instrumentName, tagKey))
                return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO MetricInstruments (InstrumentName, TagKey, InstrumentType,
                    Count, Sum, Min, Max, Last)
                VALUES (@name, @tagKey, 'histogram', 1, @value, @value, @value, @value)
                ON CONFLICT(InstrumentName, TagKey) DO UPDATE SET
                    Count = Count + 1,
                    Sum = Sum + @value,
                    Min = CASE WHEN Min IS NULL THEN @value WHEN @value < Min THEN @value ELSE Min END,
                    Max = CASE WHEN Max IS NULL THEN @value WHEN @value > Max THEN @value ELSE Max END,
                    Last = @value,
                    UpdatedAt = datetime('now')
                """;
            cmd.Parameters.AddWithValue("@name", instrumentName);
            cmd.Parameters.AddWithValue("@tagKey", tagKey);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();

            insertMeasurement(conn, instrumentName, tagKey, value);
        }
    }

    public RepoMetricsSnapshot GetSnapshot(bool reset = false)
    {
        var conn = getConnection();
        var snapshotTime = DateTime.UtcNow;

        lock (writeLock)
        {
            var instruments = new Dictionary<string, List<(string TagKey, string Type, MetricValue Value)>>(
                StringComparer.Ordinal);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT InstrumentName, TagKey, InstrumentType,
                       CounterValue, Count, Sum, Min, Max, Last
                FROM MetricInstruments
                ORDER BY InstrumentName
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var tagKey = reader.GetString(1);
                var type = reader.GetString(2);
                var counterValue = reader.GetInt64(3);
                var count = reader.GetInt64(4);
                var sum = reader.GetDouble(5);
                var min = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6);
                var max = reader.IsDBNull(7) ? (double?)null : reader.GetDouble(7);
                var last = reader.IsDBNull(8) ? (double?)null : reader.GetDouble(8);

                var measurements = getMeasurements(conn, name, tagKey);
                var tags = DeserializeTags(tagKey);

                MetricValue metricValue = type switch
                {
                    "counter" => new MetricValue(
                        Count: counterValue, Sum: null, Min: null, Max: null, Last: null,
                        Measurements: measurements, Tags: tags),
                    "gauge" => new MetricValue(
                        Count: null, Sum: null, Min: null, Max: null, Last: last,
                        Measurements: measurements, Tags: tags),
                    _ => new MetricValue(
                        Count: count, Sum: sum, Min: min, Max: max, Last: last,
                        Measurements: measurements, Tags: tags)
                };

                if (!instruments.TryGetValue(name, out var list))
                {
                    list = [];
                    instruments[name] = list;
                }
                list.Add((tagKey, type, metricValue));
            }

            if (reset)
            {
                using var resetCmd = conn.CreateCommand();
                resetCmd.CommandText = """
                    UPDATE MetricInstruments SET
                        CounterValue = 0,
                        Count = 0,
                        Sum = 0,
                        Min = NULL,
                        Max = NULL,
                        Last = 0,
                        UpdatedAt = datetime('now')
                    """;
                resetCmd.ExecuteNonQuery();

                if (options.MaxMeasurementsPerTagSet > 0)
                {
                    resetCmd.CommandText = "DELETE FROM MetricMeasurements";
                    resetCmd.ExecuteNonQuery();
                }
            }

            var instrumentMetrics = instruments.Select(kvp =>
            {
                var (_, instrumentType, _) = kvp.Value[0];
                return new InstrumentMetric(kvp.Key, instrumentType,
                    kvp.Value.Select(v => v.Value).ToList());
            }).ToList();

            return new RepoMetricsSnapshot(snapshotTime, instrumentMetrics);
        }
    }

    public void RemoveInstrumentTags(string instrumentName, IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        var tagKey = SerializeTags(tags);
        var conn = getConnection();

        lock (writeLock)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM MetricInstruments WHERE InstrumentName = @name AND TagKey = @tagKey";
            cmd.Parameters.AddWithValue("@name", instrumentName);
            cmd.Parameters.AddWithValue("@tagKey", tagKey);
            cmd.ExecuteNonQuery();

            if (options.MaxMeasurementsPerTagSet > 0)
            {
                cmd.CommandText = "DELETE FROM MetricMeasurements WHERE InstrumentName = @name AND TagKey = @tagKey";
                cmd.ExecuteNonQuery();
            }
        }
    }

    public void Dispose()
    {
        if (connection is not null)
        {
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            connection = null;
        }
    }
}
