using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace CodeMemory.Mcp;

sealed class CodeMemoryFileLoggerProvider : ILoggerProvider
{
    readonly CodeMemoryFileLogger.CodeMemoryFileLogWriter writer;

    public CodeMemoryFileLoggerProvider(string repoRoot, string version)
        => writer = new CodeMemoryFileLogger.CodeMemoryFileLogWriter(repoRoot, version);

    public ILogger CreateLogger(string categoryName) => new CodeMemoryFileLogger(categoryName, writer);

    public void Dispose() => writer.Dispose();
}

sealed class CodeMemoryFileLogger : ILogger
{
    readonly string categoryName;
    readonly CodeMemoryFileLogWriter writer;

    internal CodeMemoryFileLogger(string categoryName, CodeMemoryFileLogWriter writer)
    {
        this.categoryName = categoryName;
        this.writer = writer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
            return;

        var entry = new LogEntry(
            logLevel,
            categoryName,
            eventId,
            DateTimeOffset.Now,
            message,
            exception);

        writer.TryWrite(entry);
    }

    sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        { }
    }

    internal sealed record LogEntry(
        LogLevel Level,
        string Category,
        EventId EventId,
        DateTimeOffset Timestamp,
        string Message,
        Exception? Exception);

    internal sealed class CodeMemoryFileLogWriter : IDisposable
    {
        readonly string logDirectory;
        readonly string logId;
        readonly Channel<LogEntry> channel;
        readonly Task writerTask;
        readonly object disposeLock = new();
        bool disposed;

        internal CodeMemoryFileLogWriter(string repoRoot, string version)
        {
            logDirectory = Path.Combine(repoRoot, ".codememory");
            Directory.CreateDirectory(logDirectory);
            logId = Environment.ProcessId.ToString();

            channel = Channel.CreateUnbounded<LogEntry>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

            writerTask = Task.Run(ProcessQueueAsync);

            TryWrite(new LogEntry(LogLevel.Information, "CodeMemory.Mcp", default, DateTimeOffset.Now, $"MCP server v{version} started", null));
        }

        public void TryWrite(LogEntry entry)
            => channel.Writer.TryWrite(entry);

        public void Dispose()
        {
            lock (disposeLock)
            {
                if (disposed)
                    return;

                disposed = true;
                channel.Writer.TryComplete();
            }

            writerTask.Wait(TimeSpan.FromSeconds(5));
        }

        async Task ProcessQueueAsync()
        {
            var writers = new Dictionary<LogLevel, StreamWriter>();

            try
            {
                await foreach (var entry in channel.Reader.ReadAllAsync())
                {
                    var writer = GetWriter(writers, entry.Level);
                    await writer.WriteLineAsync(Format(entry));
                    await writer.FlushAsync();
                }
            }
            finally
            {
                foreach (var writer in writers.Values)
                    await writer.DisposeAsync();
            }
        }

        StreamWriter GetWriter(Dictionary<LogLevel, StreamWriter> writers, LogLevel level)
        {
            if (writers.TryGetValue(level, out var writer))
                return writer;

            var path = Path.Combine(logDirectory, $"Log.{logId}.{level}.txt");
            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                AutoFlush = false
            };
            writers[level] = writer;
            return writer;
        }

        static string Format(LogEntry entry)
        {
            var text = $"{entry.Timestamp:O} [{entry.Level}] {entry.Category}";

            if (entry.EventId.Id != 0 || !string.IsNullOrEmpty(entry.EventId.Name))
                text += $" ({entry.EventId.Id}:{entry.EventId.Name})";

            text += $": {entry.Message}";

            if (entry.Exception is not null)
                text += Environment.NewLine + entry.Exception;

            return text;
        }
    }
}
