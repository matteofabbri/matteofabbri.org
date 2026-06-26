using System.Globalization;
using System.Text.Json;
using MainSite.InfoBots;
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
// Oltre alle richieste ospita la collezione "info" di /info-send.
public sealed class RequestLogStore : IDisposable
{
    private const string CollectionDescription = "Una riga per ogni richiesta ricevuta: user-agent, IP, percorso, esito e durata";
    private const string InfoCollection = "info";
    private const string InfoCollectionDescription = "Payload grezzi ricevuti da POST /info-send: GUID, IP, paese e body";
    private const string LedeCollection = "ledes";
    private const string LedeCollectionDescription = "Frasi brevi mostrate a caso nel jumbotron della home";

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

                var infoCreated = await db.Create(new CreateCollectionCommand(
                    _options.EffectiveDatabaseName,
                    InfoCollection)
                {
                    Description = InfoCollectionDescription,
                });

                if (infoCreated is not (OperationResult.Success or OperationResult.AlreadyExists))
                {
                    _logger.LogError(
                        "info-send: creazione della collezione Sperone fallita ({Result}) in {Path}",
                        infoCreated, ResolvedPath);
                    if (ownsNew)
                        db.Dispose();
                    return false;
                }

                var ledesCreated = await db.Create(new CreateCollectionCommand(
                    _options.EffectiveDatabaseName,
                    LedeCollection)
                {
                    Description = LedeCollectionDescription,
                });

                if (ledesCreated is not (OperationResult.Success or OperationResult.AlreadyExists))
                {
                    _logger.LogError(
                        "Lede: creazione della collezione Sperone fallita ({Result}) in {Path}",
                        ledesCreated, ResolvedPath);
                    if (ownsNew)
                        db.Dispose();
                    return false;
                }

                _db = db;
                _initialized = true;
                _logger.LogInformation(
                    "Log richieste: Sperone embedded in {Path}, collezioni {Collection}, {InfoCollection} e {LedeCollection}",
                    ResolvedPath, _options.EffectiveCollection, InfoCollection, LedeCollection);
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

    // Richieste di un paese nel periodo. Il paese è il generatore (la posting
    // list è piccola), il timestamp è il filtro successivo. Le righe scritte
    // prima dell'introduzione dell'indice "country" non compaiono qui: restano
    // contate nel totale e quindi tra le non geolocalizzate.
    public async Task<int> CountByCountryAsync(string countryCode, DateTimeOffset from, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return 0;

        var query = new QueryElementsCommand(_options.EffectiveCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("country", EqualTo: NormalizeCountry(countryCode)) }]),
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("timestamp", From: Ticks(from)) }]),
        ]);

        var (result, items) = await _db.Query(query);
        return result == OperationResult.Success ? items.Count : 0;
    }

    // Una pagina di richieste pubbliche del periodo, dalla più recente. Le
    // pagine /admin restano fuori: sono protette e possono contenere slug di
    // bozze. L'indice "public" scarta le altre senza leggerne il payload.
    public async Task<RequestLogPage> GetPageAsync(
        int page,
        int pageSize,
        DateTimeOffset from,
        CancellationToken ct = default)
    {
        if (pageSize <= 0)
            pageSize = 200;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return RequestLogPage.Empty;

        var query = new QueryElementsCommand(_options.EffectiveCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("timestamp", From: Ticks(from)) }]),
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("public", EqualTo: "1") }]),
        ]);

        var (result, items) = await _db.Query(query);
        if (result != OperationResult.Success || items.Count == 0)
            return RequestLogPage.Empty;

        int total = items.Count;
        int pageCount = Math.Max(1, (total + pageSize - 1) / pageSize);
        int current = Math.Clamp(page, 1, pageCount);

        // IDs in ordine crescente di timestamp: la pagina 1 è la coda.
        int endExclusive = total - (current - 1) * pageSize;
        int start = Math.Max(0, endExclusive - pageSize);

        var entries = new List<RequestLogEntry>(endExclusive - start);
        for (int i = start; i < endExclusive; i++)
        {
            var (found, payload) = await _db.Get(new GetElementCommand(_options.EffectiveCollection, items[i]));
            if (!found)
                continue;

            var entry = JsonSerializer.Deserialize(payload.Span, RequestLogJsonContext.Default.RequestLogEntry);
            if (entry is not null)
                entries.Add(entry);
        }

        entries.Reverse();
        return new RequestLogPage(entries, total, current, pageCount);
    }

    // Tutte le richieste pubbliche del periodo, dalla più recente. Serve ai
    // filtri della pagina /requests: IP, Client ID e User-Agent non hanno un
    // indice scalare, quindi il filtro viene applicato sul payload.
    public async Task<IReadOnlyList<RequestLogEntry>> GetPublicEntriesAsync(
        DateTimeOffset from,
        CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return [];

        var query = new QueryElementsCommand(_options.EffectiveCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("timestamp", From: Ticks(from)) }]),
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("public", EqualTo: "1") }]),
        ]);

        var (result, items) = await _db.Query(query);
        if (result != OperationResult.Success || items.Count == 0)
            return [];

        var entries = new List<RequestLogEntry>(items.Count);
        foreach (string id in items)
        {
            var (found, payload) = await _db.Get(new GetElementCommand(_options.EffectiveCollection, id));
            if (!found)
                continue;

            var entry = JsonSerializer.Deserialize(payload.Span, RequestLogJsonContext.Default.RequestLogEntry);
            if (entry is not null)
                entries.Add(entry);
        }

        entries.Reverse();
        return entries;
    }

    // Salva il body grezzo ricevuto su POST /info-send. Restituisce la riga
    // scritta (con il Guid generato) oppure null se Sperone non è disponibile.
    public async Task<InfoEntry?> InsertInfoAsync(
        string payload,
        string? remoteIp,
        string? countryCode,
        string? countryName,
        CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        DateTimeOffset timestamp = _timeProvider.GetUtcNow();
        Guid guid = Guid.NewGuid();
        var entry = new InfoEntry
        {
            Id = $"{timestamp.UtcTicks:D19}-{guid:N}",
            Guid = guid,
            Timestamp = timestamp,
            Payload = payload,
            RemoteIp = remoteIp,
            CountryCode = countryCode ?? "",
            CountryName = countryName,
        };

        var command = new CreateElementCommand(
            InfoCollection,
            entry.Id,
            InfoIndexingOf(entry),
            _options.RetentionSeconds,
            JsonSerializer.SerializeToUtf8Bytes(entry, InfoJsonContext.Default.InfoEntry));

        var results = await _db.InsertMany([command]);
        if (results.Count == 1 && results[0] == OperationResult.Success)
            return entry;

        _logger.LogWarning(
            "info-send: scrittura su Sperone fallita ({Result})",
            results.Count == 1 ? results[0] : OperationResult.Invalid);
        return null;
    }

    // Una pagina dei payload info, dalla più recente. Il generatore timestamp
    // enumera in ordine crescente: si legge la coda e si rovescia.
    public async Task<InfoPage> GetInfoPageAsync(
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (pageSize <= 0)
            pageSize = 50;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return InfoPage.Empty;

        var query = new QueryElementsCommand(InfoCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("timestamp", From: "0") }]),
        ]);

        var (result, items) = await _db.Query(query);
        if (result != OperationResult.Success || items.Count == 0)
            return InfoPage.Empty;

        int total = items.Count;
        int pageCount = Math.Max(1, (total + pageSize - 1) / pageSize);
        int current = Math.Clamp(page, 1, pageCount);
        int endExclusive = total - (current - 1) * pageSize;
        int start = Math.Max(0, endExclusive - pageSize);

        var entries = new List<InfoEntry>(endExclusive - start);
        for (int i = start; i < endExclusive; i++)
        {
            var (found, payload) = await _db.Get(new GetElementCommand(InfoCollection, items[i]));
            if (!found)
                continue;

            var entry = JsonSerializer.Deserialize(payload.Span, InfoJsonContext.Default.InfoEntry);
            if (entry is not null)
                entries.Add(entry);
        }

        entries.Reverse();
        return new InfoPage(entries, total, current, pageCount);
    }

    // --- Lede del jumbotron --------------------------------------------------

    public async Task<IReadOnlyList<LedeEntry>> GetLedesAsync(CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return [];

        var query = new QueryElementsCommand(LedeCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("timestamp", From: "0") }]),
        ]);

        var (result, items) = await _db.Query(query);
        if (result != OperationResult.Success || items.Count == 0)
            return [];

        var ledes = new List<LedeEntry>(items.Count);
        foreach (string id in items)
        {
            var (found, payload) = await _db.Get(new GetElementCommand(LedeCollection, id));
            if (!found)
                continue;

            var entry = JsonSerializer.Deserialize(payload.Span, LedeJsonContext.Default.LedeEntry);
            if (entry is not null)
                ledes.Add(entry);
        }

        // La home non ha bisogno di un ordine; l'admin mostra i più recenti.
        ledes.Reverse();
        return ledes;
    }

    public async Task<LedeEntry?> GetRandomLedeAsync(CancellationToken ct = default)
    {
        var ledes = await GetLedesAsync(ct);
        return ledes.Count == 0 ? null : ledes[Random.Shared.Next(ledes.Count)];
    }

    public async Task<LedeEntry?> AddLedeAsync(string text, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            return null;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var entry = new LedeEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = text,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var result = await _db.Insert(new CreateElementCommand(
            LedeCollection,
            entry.Id,
            LedeIndexingOf(entry),
            null,
            JsonSerializer.SerializeToUtf8Bytes(entry, LedeJsonContext.Default.LedeEntry)));

        return result == OperationResult.Success ? entry : null;
    }

    public async Task<LedeEntry?> UpdateLedeAsync(string id, string text, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || string.IsNullOrWhiteSpace(id))
            return null;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var (found, payload) = await _db.Get(new GetElementCommand(LedeCollection, id));
        if (!found)
            return null;

        var existing = JsonSerializer.Deserialize(payload.Span, LedeJsonContext.Default.LedeEntry);
        if (existing is null)
            return null;

        var updated = existing with
        {
            Text = text,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };

        var result = await _db.Update(new UpdateElementCommand(
            LedeCollection,
            id,
            LedeIndexingOf(updated),
            null,
            JsonSerializer.SerializeToUtf8Bytes(updated, LedeJsonContext.Default.LedeEntry)));

        return result == OperationResult.Success ? updated : null;
    }

    public async Task<bool> DeleteLedeAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !await EnsureInitializedAsync(ct) || _db is null)
            return false;

        var result = await _db.Delete(new DeleteElementCommand(LedeCollection, id));
        return result == OperationResult.Success;
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

    // Indexing per ogni riga. Scalar su timestamp/status/durata/paese
    // servono per i filtri; full-text su percorso e host per la ricerca libera.
    private static IndexElementCommand IndexingOf(RequestLogEntry entry) => new(
        Scalar:
        [
            new ScalarIndexCommand("timestamp", false, entry.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("status", false, entry.StatusCode.ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("durationMs", false, ((long)Math.Round(entry.DurationMs)).ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("country", false, NormalizeCountry(entry.CountryCode)),
            new ScalarIndexCommand("public", false, entry.Path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ? "0" : "1"),
        ],
        Geographic: null,
        Vectorial: null,
        FullText:
        [
            new FullTextIndexCommand("path", entry.Path + entry.QueryString),
            new FullTextIndexCommand("host", entry.Host),
        ]);

    private static string Ticks(DateTimeOffset value) => value.UtcTicks.ToString(CultureInfo.InvariantCulture);

    // "ZZ" (codice ISO riservato all'uso locale) per gli IP privati o non
    // geolocalizzati: un indice scalare non deve avere valori vuoti.
    private static string NormalizeCountry(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "ZZ" : code.Trim().ToUpperInvariant();

    private static IndexElementCommand InfoIndexingOf(InfoEntry entry) => new(
        Scalar:
        [
            new ScalarIndexCommand("timestamp", false, entry.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("country", false, NormalizeCountry(entry.CountryCode)),
        ],
        Geographic: null,
        Vectorial: null,
        FullText: null);

    private static IndexElementCommand LedeIndexingOf(LedeEntry entry) => new(
        Scalar:
        [
            new ScalarIndexCommand("timestamp", false, entry.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)),
        ],
        Geographic: null,
        Vectorial: null,
        FullText: null);
}

public readonly record struct RequestLogWriteResult(int Ok, int Duplicates, int NotFound, int Invalid)
{
    public int Total => Ok + Duplicates + NotFound + Invalid;
    public int Failed => NotFound + Invalid;
}

// Una pagina della lista pubblica: le righe sono già ordinate dalla più
// recente; Total conta solo le richieste pubbliche del periodo.
public sealed record RequestLogPage(
    IReadOnlyList<RequestLogEntry> Entries,
    int Total,
    int Page,
    int PageCount)
{
    public static readonly RequestLogPage Empty = new([], 0, 1, 1);
}

// Una pagina dei payload /info-send, dalla più recente.
public sealed record InfoPage(
    IReadOnlyList<InfoEntry> Entries,
    int Total,
    int Page,
    int PageCount)
{
    public static readonly InfoPage Empty = new([], 0, 1, 1);
}
