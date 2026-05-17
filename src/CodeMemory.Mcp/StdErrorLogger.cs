using Microsoft.Extensions.Logging;

namespace CodeMemory.Mcp;

class StdErrorLogger<T> : ILogger<T>, IDisposable
{
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (logLevel != LogLevel.Information)
            Console.Error.WriteLine($"{logLevel}: {state}");
        else
            Console.Error.WriteLine(state);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable BeginScope<TState>(TState state) => this;

    public void Dispose()
    { }
}
