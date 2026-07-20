using Microsoft.Extensions.Logging;

namespace Maddalena.Llm;

// Provider di logging che INTERCETTA tutti i log delle categorie "Maddalena.Llm.*"
// (LlmWorker, LlmService, LlmController...) e li manda su Postgres via LlmLogStore.
// Cosi' "tutto quello che riguarda l'LLM" — avvio, training, resume/reset, pronto,
// errori, generazioni, lezioni — finisce nel log persistente senza dover
// instrumentare a mano ogni punto: basta che il codice usi il suo ILogger.
public sealed class PostgresLlmLoggerProvider(LlmLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new PgLogger(categoryName, store);

    public void Dispose() { }

    private sealed class PgLogger(string category, LlmLogStore store) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Solo le categorie dell'LLM, da Information in su (niente Debug/Trace, niente
        // EF/ASP.NET). Si esclude LlmLogStore stesso per non rischiare cicli.
        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Information
               && category.StartsWith("Maddalena.Llm", StringComparison.Ordinal)
               && !category.EndsWith(nameof(LlmLogStore), StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            store.Enqueue(
                logLevel.ToString(),
                category,
                formatter(state, exception),
                exception?.ToString(),
                string.IsNullOrEmpty(eventId.Name) ? null : eventId.Name);
        }
    }
}
