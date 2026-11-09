using Microsoft.Extensions.Options;

namespace MainSite.RequestLogging;

// Scrive la coda nel database Sperone embedded in background, a blocchi. Non
// tocca mai il ciclo della richiesta: se il disco è lento o l'engine ha un
// problema il sito continua a rispondere e le righe restano in coda (fino al
// limite) per riprovare con backoff.
public sealed class RequestLogWriter : BackgroundService
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    private readonly RequestLogOptions _options;
    private readonly RequestLogQueue _queue;
    private readonly RequestLogStore _store;
    private readonly BotEnricher? _enricher;
    private readonly ILogger<RequestLogWriter> _logger;

    // Righe già lette dalla coda ma non ancora scritte: se l'InsertMany
    // fallisce restano qui e si riprovano al giro successivo.
    private readonly List<RequestLogEntry> _pending = [];

    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private TimeSpan _retryDelay = InitialRetryDelay;
    private bool _failureLogged;

    public RequestLogWriter(
        IOptions<RequestLogOptions> options,
        RequestLogQueue queue,
        RequestLogStore store,
        ILogger<RequestLogWriter> logger,
        BotEnricher? enricher = null)
    {
        _options = options.Value;
        _queue = queue;
        _store = store;
        _logger = logger;
        _enricher = enricher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Log richieste disattivato dalla configurazione");
            return;
        }

        _logger.LogInformation(
            "Log richieste su Sperone embedded: {Path}, collezione {Collection}",
            _store.ResolvedPath, _options.EffectiveCollection);

        using var timer = new PeriodicTimer(_options.EffectiveFlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await FlushAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Un errore imprevisto non deve fermare l'host (il default
                    // di BackgroundService sarebbe StopHost): si riprova.
                    _logger.LogError(ex, "Errore imprevisto nel log richieste; riprovo al prossimo giro");
                    ScheduleRetry(ex);
                }

                ReportDropped();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Spegnimento normale.
        }
        finally
        {
            // Ultimo giro con un tetto: un deploy non deve restare appeso a un
            // disco bloccato.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await DrainAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                // Tempo scaduto: quello che resta in coda è perso, ma il
                // container si spegne.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ultimo invio dei log a Sperone fallito");
            }

            ReportDropped();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Da qui in poi il middleware non accoda più: il writer drena il
        // rimanente e termina. Il Database viene chiuso dalla DI dopo.
        _queue.Complete();
        await base.StopAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        // Dopo un errore di scrittura il database può essere ancora buono:
        // senza questo controllo si riproverebbe a ogni tick, ignorando il
        // backoff impostato da ScheduleRetry.
        if (DateTimeOffset.UtcNow < _nextAttempt)
            return;

        await FillPendingAsync(ct);
        if (_pending.Count == 0)
            return;

        if (!await EnsureReadyAsync(ct))
            return;
        try
        {
            var result = await _store.InsertManyAsync(_pending, ct);

            if (result.NotFound > 0)
            {
                // Collezione sparita (es. cancellata a mano): lo store la
                // ricrea al prossimo giro, intanto teniamo le righe in coda.
                _logger.LogWarning(
                    "Log richieste: collezione Sperone non trovata; la ricreo e riprovo {Count} righe",
                    _pending.Count);
                return;
            }

            if (result.Invalid > 0)
            {
                // Payload/indici nostri rifiutati: riprovare non serve, si
                // scarta il blocco e si segnala forte.
                _logger.LogError(
                    "Log richieste: {Count} righe rifiutate da Sperone (indici non validi): le scarto",
                    result.Invalid);
                _pending.Clear();
                return;
            }

            int failed = result.Total - result.Ok - result.Duplicates;
            if (failed > 0)
                _logger.LogWarning("Log richieste: {Failed} righe su {Total} non inserite", failed, result.Total);

            _pending.Clear();
            _failureLogged = false;
            _retryDelay = InitialRetryDelay;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Errore di I/O o dell'engine: le righe restano in _pending.
            ScheduleRetry(ex);
        }
    }

    // Svuotamento dello shutdown: più blocchi in fila, finché c'è roba o
    // scade il tetto. Se una scrittura fallisce si ferma (niente busy-loop):
    // il resto della coda è perso, ma il container si spegne in fretta.
    private async Task DrainAsync(CancellationToken ct)
    {
        for (int i = 0; i < 100 && !ct.IsCancellationRequested; i++)
        {
            bool hasPending = _pending.Count > 0;
            bool hasQueued = _queue.Reader.TryPeek(out _);
            if (!hasPending && !hasQueued)
                return;

            await FlushAsync(ct);

            if (_pending.Count > 0)
                return;
        }
    }

    // Legge il batch dalla coda e lo classifica una sola volta. Se la scrittura
    // su Sperone fallisce le righe restano in _pending già arricchite: il retry
    // non richiama il modello.
    private async Task FillPendingAsync(CancellationToken ct)
    {
        int room = _options.EffectiveBatchSize - _pending.Count;
        if (room <= 0)
            return;

        var batch = new List<RequestLogEntry>(room);
        while (batch.Count < room && _queue.Reader.TryRead(out var entry))
            batch.Add(entry);

        if (batch.Count == 0)
            return;

        if (_enricher is not null)
        {
            var enriched = await _enricher.EnrichAsync(batch, ct);
            batch = enriched as List<RequestLogEntry> ?? enriched.ToList();
        }

        _pending.AddRange(batch);
    }

    // Apre il database embedded la prima volta. Da lì in poi è un return true
    // immediato; un errore imposta il backoff come per le scritture.
    private async Task<bool> EnsureReadyAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextAttempt)
            return false;

        try
        {
            if (await _store.EnsureInitializedAsync(ct))
                return true;

            ScheduleRetry(new InvalidOperationException("Inizializzazione del database Sperone fallita."));
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ScheduleRetry(ex);
            return false;
        }
    }

    private void ScheduleRetry(Exception ex)
    {
        if (!_failureLogged)
        {
            _logger.LogWarning(
                ex,
                "Sperone embedded non disponibile per il log richieste; riprovo tra {Delay}s",
                (int)_retryDelay.TotalSeconds);
            _failureLogged = true;
        }
        else
        {
            _logger.LogDebug(
                ex,
                "Sperone embedded ancora non disponibile; nuovo tentativo tra {Delay}s",
                (int)_retryDelay.TotalSeconds);
        }

        _nextAttempt = DateTimeOffset.UtcNow + _retryDelay;
        _retryDelay = TimeSpan.FromSeconds(Math.Min(_retryDelay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
    }

    private void ReportDropped()
    {
        long dropped = _queue.TakeDropped();
        if (dropped > 0)
        {
            _logger.LogWarning(
                "Log richieste: {Dropped} richieste scartate perché la coda è piena (store Sperone lento o non disponibile)",
                dropped);
        }
    }
}
