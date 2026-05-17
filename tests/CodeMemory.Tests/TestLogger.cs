using Microsoft.Extensions.Logging;

namespace CodeMemory.Tests;

public sealed class TestLogger<T> : ILogger<T>, IDisposable
{
    public List<string> Warnings { get; } = [];
    public List<string> Info { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        if (logLevel == LogLevel.Warning)
            Warnings.Add(msg);
        else if (logLevel == LogLevel.Information)
            Info.Add(msg);
    }

    public void Dispose() { }
}
