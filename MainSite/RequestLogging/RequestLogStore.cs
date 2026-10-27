using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sperone;
using Sperone.Collections;
using Sperone.Commands.Collections;
using Sperone.Commands.Indexing;
using Sperone.Commands.Items;
using Sperone.Commands.Queries;
using Sperone.Indexing.FullText.Embeddings;
using Sperone.Storage;

namespace MainSite.RequestLogging;

// Il database Sperone embedded: la libreria gira in-process e scrive in una
// cartella su disco. Nessun server, nessuna connection string, nessun login.
// Un solo oggetto Database per processo; il writer è l'unico a scriverci.
public sealed class RequestLogStore : IDisposable
{
    private const string CollectionDescription = "Una riga per ogni richiesta ricevuta: client, browser, percorso, esito e durata";

    private readonly RequestLogOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RequestLogStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private Database? _db;
    private bool _initialized;
    private bool _disposed;

    public RequestLogStore(
        IOptions<RequestLogOptions> options,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _environment = environment;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<RequestLogStore>();
    }

    public string ResolvedPath => Path.IsPathRooted(_options.EffectivePath)
        ? _options.EffectivePath
        : Path.Combine(_environment.ContentRootPath, _options.EffectivePath);

    // Apre il database e crea la collezione la prima volta. È idempotente e
    // serializzato; il writer la chiama prima di ogni scrittura (dopo la prima
    // è un semplice return true).
    public async Task<bool> EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return true;

        await _initGate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return true;
            if (_disposed)
                return false;

            // Per un log di richieste serve solo BM25: niente modello
            // semantico, quindi niente download/caricamento dei ~2,3 GB.
            SemanticModel.ModelLocation = null;

            Database? db = _db;
            bool ownsNew = false;
            try
            {
                if (db is null)
                {
                    db = new Database(
                        ResolvedPath,
                        loggerFactory: _loggerFactory,
                        timeProvider: _timeProvider,
                        sizing: new StoreSizing(
                            HashCacheLines: _options.EffectiveHashCacheLines,
                            LogMemoryBits: _options.EffectiveLogMemoryBits,
                            LogPageBits: _options.EffectiveLogPageBits));
                    ownsNew = true;
                }

                var created = await db.Create(new CreateCollectionCommand(
                    _options.EffectiveDatabaseName,
                    _options.EffectiveCollection)
                {
                    Description = CollectionDescription,
                });

                if (created is not (OperationResult.Success or OperationResult.AlreadyExists))
                {
                    _logger.LogError(
                        "Log richieste: creazione della collezione Sperone fallita ({Result}) in {Path}",
                        created, ResolvedPath);
                    if (ownsNew)
                        db.Dispose();
                    return false;
                }

                _db = db;
                _initialized = true;
                _logger.LogInformation(
                    "Log richieste: Sperone embedded in {Path}, collezione {Collection}",
                    ResolvedPath, _options.EffectiveCollection);
                return true;
            }
            catch
            {
                if (ownsNew)
                    db?.Dispose();
                throw;
            }
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<RequestLogWriteResult> InsertManyAsync(
        IReadOnlyList<RequestLogEntry> entries,
        CancellationToken ct)
    {
        if (entries.Count == 0)
            return default;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return new RequestLogWriteResult(0, 0, entries.Count, 0);

        var commands = new List<CreateElementCommand>(entries.Count);
        foreach (var entry in entries)
        {
            commands.Add(new CreateElementCommand(
                _options.EffectiveCollection,
                entry.Id,
                IndexingOf(entry),
                _options.RetentionSeconds,
                JsonSerializer.SerializeToUtf8Bytes(entry, RequestLogJsonContext.Default.RequestLogEntry)));
        }

        var results = await _db.InsertMany(commands);

        int ok = 0, duplicates = 0, notFound = 0, invalid = 0;
        foreach (var result in results)
        {
            switch (result)
            {
                case OperationResult.Success: ok++; break;
                case OperationResult.AlreadyExists: duplicates++; break;
                case OperationResult.NotFound: notFound++; break;
                case OperationResult.Invalid: invalid++; break;
            }
        }

        // La collezione è sparita sotto i piedi (es. cancellata a mano):
        // la ricreiamo al prossimo giro e riproviamo le stesse righe.
        if (notFound > 0)
            _initialized = false;

        return new RequestLogWriteResult(ok, duplicates, notFound, invalid);
    }

    // Lettura puntuale: serve ai test e a un eventuale pannello admin futuro.
    public async Task<RequestLogEntry?> GetAsync(string itemId, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var (found, payload) = await _db.Get(new GetElementCommand(_options.EffectiveCollection, itemId));
        return found
            ? JsonSerializer.Deserialize(payload.Span, RequestLogJsonContext.Default.RequestLogEntry)
            : null;
    }

    // Quante richieste ci sono dal periodo richiesto in poi. La query usa
    // l'indice scalare timestamp e non legge i payload: alla mappa servono
    // solo i conteggi.
    public async Task<int> CountSinceAsync(DateTimeOffset from, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return 0;

        var query = new QueryElementsCommand(_options.EffectiveCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("timestamp", From: Ticks(from)) }]),
        ]);

        var (result, items) = await _db.Query(query);
        return result == OperationResult.Success ? items.Count : 0;
    }

    // Richieste di una regione nel periodo. La regione è il generatore (la
    // posting list è ~1/20 del totale), il timestamp è il filtro successivo.
    // Le righe scritte prima dell'introduzione dell'indice "region" non
    // compaiono qui: restano contate nel totale e quindi tra le sconosciute.
    public async Task<int> CountByRegionAsync(int regionCode, DateTimeOffset from, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return 0;

        var query = new QueryElementsCommand(_options.EffectiveCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("region", EqualTo: regionCode.ToString(CultureInfo.InvariantCulture)) }]),
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("timestamp", From: Ticks(from)) }]),
        ]);

        var (result, items) = await _db.Query(query);
        return result == OperationResult.Success ? items.Count : 0;
    }

    public void Dispose()
    {
        _initGate.Wait();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _db?.Dispose(); // committa e chiude gli store
            _db = null;
            _initialized = false;
        }
        finally
        {
            _initGate.Release();
        }
    }

    // Indexing per ogni riga. Scalar su timestamp/status/durata/bot/regione
    // servono per i filtri; full-text su percorso e host per la ricerca libera.
    private static IndexElementCommand IndexingOf(RequestLogEntry entry) => new(
        Scalar:
        [
            new ScalarIndexCommand("timestamp", false, entry.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("status", false, entry.StatusCode.ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("durationMs", false, ((long)Math.Round(entry.DurationMs)).ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("bot", false, entry.IsBot ? "1" : "0"),
            new ScalarIndexCommand("region", false, entry.RegionCode.ToString(CultureInfo.InvariantCulture)),
        ],
        Geographic: null,
        Vectorial: null,
        FullText:
        [
            new FullTextIndexCommand("path", entry.Path + entry.QueryString),
            new FullTextIndexCommand("host", entry.Host),
        ]);

    private static string Ticks(DateTimeOffset value) => value.UtcTicks.ToString(CultureInfo.InvariantCulture);
}

public readonly record struct RequestLogWriteResult(int Ok, int Duplicates, int NotFound, int Invalid)
{
    public int Total => Ok + Duplicates + NotFound + Invalid;
    public int Failed => NotFound + Invalid;
}
