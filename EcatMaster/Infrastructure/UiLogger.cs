using Microsoft.Extensions.Logging;

namespace EcatMaster.Infrastructure;

/// <summary>Core 의 ILogger 출력을 UI 로그 패널로 보내는 최소 구현.</summary>
public sealed class UiLogger<T> : ILogger<T>
{
    private readonly Action<LogLevel, string> _sink;

    public UiLogger(Action<LogLevel, string> sink) => _sink = sink;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        if (exception is not null) msg += $" ({exception.GetType().Name}: {exception.Message})";
        _sink(logLevel, msg);
    }
}
