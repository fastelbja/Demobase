using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DemoBase.App;

public class TimestampDebugLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TimestampDebugLogger(categoryName);
    public void Dispose() { }
}

public class TimestampDebugLogger : ILogger
{
    private readonly string _category;
    public TimestampDebugLogger(string category) => _category = category;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        // Raccourcir le nom de catégorie
        var cat = _category.Contains('.') ? _category[((_category.LastIndexOf('.') + 1)..)] : _category;
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {cat}: {msg}");
    }
}
