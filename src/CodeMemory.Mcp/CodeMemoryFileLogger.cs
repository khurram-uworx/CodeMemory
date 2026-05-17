using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace CodeMemory.Mcp;

sealed class CodeMemoryFileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CodeMemoryFileLogger(categoryName);

    public void Dispose() => CodeMemoryFileLogger.DisposeWriter();
}

sealed class CodeMemoryFileLogger : ILogger
{
    static readonly string LogDirectory;
    static readonly Channel<LogEntry> Channel;
    static readonly Task WriterTask;
    static readonly object DisposeLock = new();
    static bool disposed;

    readonly string categoryName;

    static CodeMemoryFileLogger()
    {
        LogDirectory = Path.Combine(Environment.CurrentDirectory, ".codememory");
        Directory.CreateDirectory(LogDirectory);

        foreach (var file in Directory.EnumerateFiles(LogDirectory, "Log.*.txt"))
            File.Delete(file);

        Channel = System.Threading.Channels.Channel.CreateUnbounded<LogEntry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        WriterTask = Task.Run(ProcessQueueAsync);
    }

    public CodeMemoryFileLogger(string categoryName)
    {
        this.categoryName = categoryName;
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

        Channel.Writer.TryWrite(entry);
    }

    public static void DisposeWriter()
    {
        lock (DisposeLock)
        {
            if (disposed)
                return;

            disposed = true;
            Channel.Writer.TryComplete();
        }

        WriterTask.Wait(TimeSpan.FromSeconds(5));
    }

    static async Task ProcessQueueAsync()
    {
        var writers = new Dictionary<LogLevel, StreamWriter>();

        try
        {
            await foreach (var entry in Channel.Reader.ReadAllAsync())
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

    static StreamWriter GetWriter(Dictionary<LogLevel, StreamWriter> writers, LogLevel level)
    {
        if (writers.TryGetValue(level, out var writer))
            return writer;

        var path = Path.Combine(LogDirectory, $"Log.{level}.txt");
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
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

    sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        { }
    }

    sealed record LogEntry(
        LogLevel Level,
        string Category,
        EventId EventId,
        DateTimeOffset Timestamp,
        string Message,
        Exception? Exception);
}
