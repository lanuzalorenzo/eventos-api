using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EventosApi.Tests;

/// <summary>Guarda las líneas de log para comprobar qué se escribe y qué no.</summary>
public sealed class CapturaLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Mensajes { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Registro(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Registro(CapturaLogs duenio, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            duenio.Mensajes.Enqueue($"{categoria}: {formatter(state, exception)}");
    }
}
