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
    private const string FullTextRemovedMarker = "fulltext-index-dropped.json";
    private const string ScalarIndexesDroppedMarker = "scalar-indexes-dropped.json";

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

            // Must run before the database opens: it deletes index stores
            // that no process may have open.
            DropLegacyFullTextIndexes();

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

                // Before the collection is opened for the first time in this
                // process: a corrupt scalar index would make every write throw.
                await DropCorruptScalarIndexesAsync(db);

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

    // One-time migration (2026-11-10). The request log no longer indexes path and
    // host (see IndexingOf): the pinned Sperone version can lose a full-text
    // overflow chunk, after which every Update of that row throws and the manual
    // bot flags plus the backfill fail. Deleting the persisted full-text stores
    // before the database is opened makes the next update of an old row find no
    // index to remove from (IndexManager.Remove tolerates a missing index); the
    // new spec never recreates it. Runs once, guarded by a marker next to the
    // database; every collection is visited because the folder names are GUIDs
    // and only the request collection ever had a full-text index.
    private void DropLegacyFullTextIndexes()
    {
        string marker = Path.Combine(ResolvedPath, FullTextRemovedMarker);
        if (File.Exists(marker))
            return;

        try
        {
            string storage = Path.Combine(ResolvedPath, "collections_storage");
            if (Directory.Exists(storage))
            {
                foreach (string collection in Directory.GetDirectories(storage))
                {
                    string fullText = Path.Combine(collection, "idx", "ft");
                    if (Directory.Exists(fullText))
                        Directory.Delete(fullText, recursive: true);
                }
            }

            Directory.CreateDirectory(ResolvedPath);
            File.WriteAllText(marker, "{\"droppedAt\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}");
            _logger.LogInformation("Sperone: full-text indexes dropped (one-time migration)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No marker: the next start retries. Deleting a missing folder is a
            // no-op, so a partially failed run is safe to repeat.
            _logger.LogWarning(ex, "Sperone: could not drop the full-text indexes; will retry on the next start");
        }
    }

    // One-off repair (2026-11-10). The pinned Sperone version can lose an
    // overflow chunk of a scalar B-tree node, after which every InsertMany and
    // every Update of the collection throws ("Blocco di overflow N non trovato
    // nello store") and the request log stops. The payloads are intact, so the
    // index is rebuildable: delete the requests collection's scalar-multiple
    // store before the collection opens (the next Insert/Update recreates it on
    // demand) and let RequestLogReindex refill it from the rows. The marker next
    // to the database keeps this to one run.
    private async Task DropCorruptScalarIndexesAsync(Database db)
    {
        string marker = Path.Combine(ResolvedPath, ScalarIndexesDroppedMarker);
        if (File.Exists(marker))
            return;

        try
        {
            var (found, metadata) = await db.GetMetadata(_options.EffectiveCollection);
            if (found)
            {
                string scalarIndexes = Path.Combine(metadata.Folder, "idx", "multi");
                if (Directory.Exists(scalarIndexes))
                    Directory.Delete(scalarIndexes, recursive: true);
            }

            Directory.CreateDirectory(ResolvedPath);
            File.WriteAllText(marker, "{\"droppedAt\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}");
            _logger.LogWarning(
                "Sperone: scalar indexes of {Collection} dropped; the reindex job rebuilds them",
                _options.EffectiveCollection);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Sperone: could not drop the scalar indexes; will retry on the next start");
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

    // How many requests start at the given moment. The query uses the
    // timestamp scalar index and does not read payloads.
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

    // Requests from one country in the period. The country is the generator
    // (its posting list is small) and the timestamp is the next filter. Rows
    // written before the "country" index existed do not appear here; they are
    // still counted in the total.
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

    // Legge tutte le righe (anche /admin) in un intervallo di tempo. Serve al
    // backfill dei flag: le pagine pubbliche non bastano, e il filtro per
    // timestamp evita di caricare 90 giorni in una volta.
    public async Task<IReadOnlyList<RequestLogEntry>> GetEntriesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return [];

        var query = new QueryElementsCommand(_options.EffectiveCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("timestamp", From: Ticks(from), To: Ticks(to)) }]),
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

        return entries;
    }

    // Riscrive il payload di righe esistenti (flag bot). L'ItemId non cambia,
    // quindi ordine, paginazione e indici restano quelli di prima; il TTL viene
    // riprogrammato al tempo che resta, così il backfill non allunga la
    // conservazione dei log.
    public async Task<int> UpdateManyAsync(
        IReadOnlyList<RequestLogEntry> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return 0;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return 0;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var commands = new List<UpdateElementCommand>(entries.Count);

        foreach (var entry in entries)
        {
            if (BuildUpdateCommand(entry, now) is { } command)
                commands.Add(command);
        }

        if (commands.Count == 0)
            return 0;

        var results = await _db.UpdateMany(commands);
        return results.Count(result => result == OperationResult.Success);
    }

    // One page of the index rebuild: enumerate the element store (keys only, no
    // index involved), then rewrite each row's indexing spec. The payload does
    // not change and BuildUpdateCommand keeps the remaining TTL; rows the store
    // refuses are counted and left to a later pass.
    public async Task<RequestLogReindexPage> ReindexPageAsync(int skip, int take, CancellationToken ct = default)
    {
        if (take <= 0)
            take = 200;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return new RequestLogReindexPage(0, 0, 0);

        var (result, ids) = await _db.ListElements(_options.EffectiveCollection, Math.Max(0, skip), take);
        if (result != OperationResult.Success || ids.Count == 0)
            return new RequestLogReindexPage(0, 0, 0);

        int updated = 0;
        int failed = 0;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (string id in ids)
        {
            try
            {
                var (found, payload) = await _db.Get(new GetElementCommand(_options.EffectiveCollection, id));
                if (!found)
                    continue;

                var entry = JsonSerializer.Deserialize(payload.Span, RequestLogJsonContext.Default.RequestLogEntry);
                if (entry is null || BuildUpdateCommand(entry, now) is not { } command)
                    continue;

                if (await _db.Update(command) == OperationResult.Success)
                    updated++;
                else
                    failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Reindex richieste: riga {RequestId} non reindicizzata", id);
            }
        }

        return new RequestLogReindexPage(ids.Count, updated, failed);
    }

    // Riscrive una singola riga: è la modifica manuale dei flag dalla pagina
    // admin. Stesse garanzie del batch (ItemId, indici e scadenza invariati).
    public async Task<bool> UpdateAsync(RequestLogEntry entry, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return false;

        var command = BuildUpdateCommand(entry, _timeProvider.GetUtcNow());
        if (command is null)
            return false;

        var result = await _db.Update(command);
        return result == OperationResult.Success;
    }

    // Rewrites both flags on every row of one client. The admin page passes the
    // period's public entries (the same list the public page reads), so a click
    // is about the client, not the single row.
    //
    // One row at a time, with the failures counted instead of thrown: a single
    // unreadable row must not block the other requests of the same client, and
    // the admin page can report how many went through and why the first one
    // did not.
    public async Task<ClientFlagUpdate> UpdateClientFlagsAsync(
        IReadOnlyList<RequestLogEntry> entries,
        string clientId,
        bool? isBot,
        bool? isMalicious,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return new ClientFlagUpdate(0, 0, 0, null);

        var matches = new List<RequestLogEntry>();
        foreach (var entry in entries)
        {
            if (!string.Equals(ClientIdOf(entry), clientId, StringComparison.OrdinalIgnoreCase))
                continue;

            matches.Add(entry with
            {
                IsBot = isBot,
                IsMaliciousURL = isMalicious,
                BotProbability = null,
                BotModel = isBot is null && isMalicious is null ? null : "manual",
            });
        }

        int updated = 0;
        int skipped = 0;
        string? firstError = null;

        foreach (var entry in matches)
        {
            try
            {
                if (await UpdateAsync(entry, ct))
                {
                    updated++;
                    continue;
                }

                skipped++;
                firstError ??= "the store rejected the update";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;
                firstError ??= $"{ex.GetType().Name}: {ex.Message}";
                _logger.LogWarning(ex, "Requests admin: update rejected for {RequestId}", entry.Id);
            }
        }

        return new ClientFlagUpdate(matches.Count, updated, skipped, firstError);
    }

    private static string ClientIdOf(RequestLogEntry entry) =>
        entry.ClientId ?? ClientIds.For(entry.RemoteIp, entry.UserAgent) ?? "";

    private UpdateElementCommand? BuildUpdateCommand(RequestLogEntry entry, DateTimeOffset now)
    {
        long? ttl = _options.RetentionSeconds is { } retention
            ? retention - (long)(now - entry.Timestamp).TotalSeconds
            : null;

        if (ttl is <= 0)
            return null;

        return new UpdateElementCommand(
            _options.EffectiveCollection,
            entry.Id,
            IndexingOf(entry),
            ttl,
            JsonSerializer.SerializeToUtf8Bytes(entry, RequestLogJsonContext.Default.RequestLogEntry));
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

    // One indexing spec per row: the scalar entries drive the filters (timestamp
    // range, status, duration, country) and the "public" flag that hides /admin
    // paths. No full-text index: nothing searches path or host, and the pinned
    // Sperone version can lose a full-text overflow chunk, after which every
    // Update of that row fails (the manual bot flags and the backfill). Rows
    // written before this change keep their old spec — with path/host — until
    // they are updated; the full-text store is dropped once on the server so
    // those updates find no index to remove from.
    private static IndexElementCommand IndexingOf(RequestLogEntry entry) => new(
        Scalar:
        [
            new ScalarIndexCommand("timestamp", false, entry.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("status", false, entry.StatusCode.ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("durationMs", false, ((long)Math.Round(entry.DurationMs)).ToString(CultureInfo.InvariantCulture)),
            new ScalarIndexCommand("country", false, NormalizeCountry(entry.CountryCode)),
            new ScalarIndexCommand("public", false, PublicPathOf(entry) ? "1" : "0"),
        ],
        Geographic: null,
        Vectorial: null,
        FullText: null);

    // Old payloads can carry null for fields that are non-nullable in the record.
    private static bool PublicPathOf(RequestLogEntry entry) =>
        !(entry.Path ?? "").StartsWith("/admin", StringComparison.OrdinalIgnoreCase);

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

// The outcome of a client-wide flag change from the admin page: how many rows
// the client had in the period, how many were written, how many the store
// refused (with the first reason, for the admin message).
public sealed record ClientFlagUpdate(int Matched, int Updated, int Skipped, string? FirstError);

// One page of the index rebuild: ids returned by the element store, rows whose
// indexing spec was rewritten, rows the store refused.
public sealed record RequestLogReindexPage(int Scanned, int Updated, int Failed);

// Una pagina dei payload /info-send, dalla più recente.
public sealed record InfoPage(
    IReadOnlyList<InfoEntry> Entries,
    int Total,
    int Page,
    int PageCount)
{
    public static readonly InfoPage Empty = new([], 0, 1, 1);
}
