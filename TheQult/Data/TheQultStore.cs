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

namespace TheQult.Data;

// Il database Sperone embedded: la libreria gira in-process e scrive in una
// cartella su disco (TheQult:DataPath). Nessun server, nessuna connection
// string. Tre collezioni:
//   members     — i profili (email/password, status di ospitalità, referenze
//                 in numero calcolato a parte);
//   stays       — le richieste di ospitalità e il loro esito;
//   references  — i giudizi post-soggiorno, la fiducia della comunità.
// Un solo oggetto Database per processo; l'inizializzazione è pigra e
// serializzata (stesso schema di RequestLogStore in MainSite).
public sealed class TheQultStore : IDisposable
{
    private const string MembersDescription =
        "I profili della comunità: email di accesso, status di ospitalità, referenze ricevute";
    private const string StaysDescription =
        "Le richieste di ospitalità: ospite, host, date, messaggio ed esito";
    private const string ReferencesDescription =
        "Le referenze post-soggiorno: autore, soggetto, verdetto e testo";

    private readonly TheQultOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TheQultStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private Database? _db;
    private bool _initialized;
    private bool _disposed;

    public TheQultStore(
        IOptions<TheQultOptions> options,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _environment = environment;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<TheQultStore>();
    }

    public string ResolvedPath => Path.IsPathRooted(_options.DataPath)
        ? _options.DataPath
        : Path.Combine(_environment.ContentRootPath, _options.DataPath);

    // Apre il database e crea le collezioni la prima volta. Idempotente e
    // serializzato: ogni metodo passa da qui, dopo il primo giro è un return.
    public async Task<bool> EnsureInitializedAsync(CancellationToken ct = default)
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

            // Niente modello semantico: per i profili serve solo BM25/indici
            // scalari, quindi non scaricare i ~2,3 GB (come nel log di MainSite).
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
                        timeProvider: _timeProvider);
                    ownsNew = true;
                }

                foreach (var (name, description) in new (string, string)[]
                {
                    (_options.MembersCollection, MembersDescription),
                    (_options.StaysCollection, StaysDescription),
                    (_options.ReferencesCollection, ReferencesDescription),
                })
                {
                    var created = await db.Create(new CreateCollectionCommand(
                        _options.DatabaseName, name) { Description = description });

                    if (created is not (OperationResult.Success or OperationResult.AlreadyExists))
                    {
                        _logger.LogError(
                            "TheQult: creazione della collezione Sperone {Collection} fallita ({Result}) in {Path}",
                            name, created, ResolvedPath);
                        if (ownsNew)
                            db.Dispose();
                        return false;
                    }
                }

                _db = db;
                _initialized = true;
                _logger.LogInformation(
                    "TheQult: Sperone embedded in {Path}, collezioni {Members}, {Stays} e {References}",
                    ResolvedPath, _options.MembersCollection, _options.StaysCollection,
                    _options.ReferencesCollection);
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

    // --- membri -------------------------------------------------------------

    // Crea il profilo; null se l'email è già registrata. Il controllo diretto
    // + il risultato Insert (AlreadyExists) chiudono anche la corsa stretta
    // tra due iscrizioni simultaneousi.
    public async Task<Member?> CreateMemberAsync(Member member, CancellationToken ct = default)
    {
        if (await GetMemberByEmailAsync(member.Email, ct) is not null)
            return null;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var result = await _db.Insert(new CreateElementCommand(
            _options.MembersCollection,
            member.Id,
            MemberIndexingOf(member),
            null,
            Serialize(member)));

        return result == OperationResult.Success ? member : null;
    }

    public async Task<Member?> GetMemberAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var (found, payload) = await _db.Get(new GetElementCommand(_options.MembersCollection, id));
        return found ? DeserializeMember(payload) : null;
    }

    public async Task<Member?> GetMemberByEmailAsync(string email, CancellationToken ct = default)
    {
        email = NormalizeEmail(email);
        if (email.Length == 0)
            return null;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var query = new QueryElementsCommand(_options.MembersCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("email", EqualTo: email) }]),
        ]);

        return await GetFirst(query, DeserializeMember, ct);
    }

    public async Task<bool> UpdateMemberAsync(Member updated, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return false;

        var result = await _db.Update(new UpdateElementCommand(
            _options.MembersCollection,
            updated.Id,
            MemberIndexingOf(updated),
            null,
            Serialize(updated)));

        return result == OperationResult.Success;
    }

    // Un elenco di profili (per la pagina /members): filtri opzionali su città
    // e status di ospitalità, tutti per indice scalare. Piccolo ordinamento in
    // memoria per createdAt decrescente: la community è piccola e resterà
    // piccola a lungo, e la pagina è impaginata comunque.
    public async Task<MembersPage> ListMembersAsync(
        string? city,
        string? hosting,
        int page,
        int pageSize = 24,
        CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return MembersPage.Empty;

        var steps = new List<QueryStep>
        {
            new([new QueryClause { Scalar = new ScalarQuery("createdAt", From: "0") }]),
        };
        if (!string.IsNullOrWhiteSpace(city))
            steps.Add(new QueryStep([new QueryClause { Scalar = new ScalarQuery("city", EqualTo: NormalizeCity(city)) }]));
        if (!string.IsNullOrWhiteSpace(hosting))
            steps.Add(new QueryStep([new QueryClause { Scalar = new ScalarQuery("hosting", EqualTo: hosting) }]));

        var (result, items) = await _db.Query(new QueryElementsCommand(_options.MembersCollection, steps.ToArray()));
        if (result != OperationResult.Success || items.Count == 0)
            return MembersPage.Empty;

        var members = await FetchAllAsync(_options.MembersCollection, items, DeserializeMember, ct);
        members.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));

        int total = members.Count;
        int pageCount = Math.Max(1, (total + pageSize - 1) / pageSize);
        int current = Math.Clamp(page, 1, pageCount);

        return new MembersPage(
            members.Skip((current - 1) * pageSize).Take(pageSize).ToList(),
            total, current, pageCount);
    }

    public async Task<int> CountMembersAsync(CancellationToken ct = default) =>
        await CountAsync("createdAt", From: "0", ct: ct);

    // Gli host veri e propri: quelli che dicono "sì, posso ospitare".
    public async Task<int> CountHostsAsync(CancellationToken ct = default) =>
        await CountAsync("hosting", EqualTo: Member.HostingHost, ct: ct);

    // --- soggiorni ----------------------------------------------------------

    public async Task<StayRequest?> CreateStayAsync(StayRequest stay, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var result = await _db.Insert(new CreateElementCommand(
            _options.StaysCollection,
            stay.Id,
            StayIndexingOf(stay),
            null,
            Serialize(stay)));

        return result == OperationResult.Success ? stay : null;
    }

    public async Task<StayRequest?> GetStayAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var (found, payload) = await _db.Get(new GetElementCommand(_options.StaysCollection, id));
        return found ? DeserializeStay(payload) : null;
    }

    // Tutti i soggiorni che vedono un membro: in entrata (da host) e in uscita
    // (da ospite), i più recenti prima.
    public async Task<IReadOnlyList<StayRequest>> GetStaysInvolvingAsync(
        string memberId, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return [];

        var asHost = new QueryElementsCommand(_options.StaysCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("host", EqualTo: memberId) }]),
        ]);
        var asGuest = new QueryElementsCommand(_options.StaysCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("guest", EqualTo: memberId) }]),
        ]);

        var stays = new List<StayRequest>();
        foreach (var query in new[] { asHost, asGuest })
        {
            var (result, items) = await _db.Query(query);
            if (result != OperationResult.Success)
                continue;

            stays.AddRange(await FetchAllAsync(_options.StaysCollection, items, DeserializeStay, ct));
        }

        // Le due query si possono sovrapporre solo se un membro è host e
        // ospite dello stesso soggiorno: le regole lo vietano, ma deduplichiamo.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return stays
            .Where(s => seen.Add(s.Id))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    public async Task<bool> UpdateStayAsync(StayRequest updated, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return false;

        var result = await _db.Update(new UpdateElementCommand(
            _options.StaysCollection,
            updated.Id,
            StayIndexingOf(updated),
            null,
            Serialize(updated)));

        return result == OperationResult.Success;
    }

    public async Task<int> CountStaysAsync(CancellationToken ct = default) =>
        await CountAsync("createdAt", From: "0", ct: ct, collection: _options.StaysCollection);

    // --- referenze ----------------------------------------------------------

    // Scrive la referenza; null se l'autore ne ha già scritta una per questo
    // soggiorno. Il controllo + AlreadyExists chiudono il doppio click.
    public async Task<Reference?> CreateReferenceAsync(Reference reference, CancellationToken ct = default)
    {
        if (await GetReferenceAsync(reference.AuthorId, reference.StayId, ct) is not null)
            return null;

        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var result = await _db.Insert(new CreateElementCommand(
            _options.ReferencesCollection,
            reference.Id,
            ReferenceIndexingOf(reference),
            null,
            Serialize(reference)));

        return result == OperationResult.Success ? reference : null;
    }

    public async Task<Reference?> GetReferenceAsync(
        string authorId, string stayId, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return null;

        var query = new QueryElementsCommand(_options.ReferencesCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("author", EqualTo: authorId) }]),
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("stay", EqualTo: stayId) }]),
        ]);

        return await GetFirst(query, DeserializeReference, ct);
    }

    public async Task<IReadOnlyList<Reference>> GetReferencesForAsync(
        string subjectId, CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return [];

        var query = new QueryElementsCommand(_options.ReferencesCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery("subject", EqualTo: subjectId) }]),
        ]);

        var (result, items) = await _db.Query(query);
        if (result != OperationResult.Success || items.Count == 0)
            return [];

        var references = await FetchAllAsync(_options.ReferencesCollection, items, DeserializeReference, ct);
        return references.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public async Task<int> CountReferencesAsync(CancellationToken ct = default) =>
        await CountAsync("createdAt", From: "0", ct: ct, collection: _options.ReferencesCollection);

    // --- interni ------------------------------------------------------------

    private async Task<T?> GetFirst<T>(QueryElementsCommand query, Func<ReadOnlyMemory<byte>, T?> deserialize, CancellationToken ct)
        where T : class
    {
        if (_db is null)
            return null;

        var (result, items) = await _db.Query(query);
        if (result != OperationResult.Success || items.Count == 0)
            return null;

        foreach (string id in items)
        {
            var (found, payload) = await _db.Get(new GetElementCommand(query.Collection, id));
            if (found)
                return deserialize(payload);
        }

        return null;
    }

    private async Task<int> CountAsync(
        string field,
        string? From = null,
        string? EqualTo = null,
        string? collection = null,
        CancellationToken ct = default)
    {
        if (!await EnsureInitializedAsync(ct) || _db is null)
            return 0;

        var query = new QueryElementsCommand(collection ?? _options.MembersCollection,
        [
            new QueryStep([new QueryClause { Scalar = new ScalarQuery(field, From: From, EqualTo: EqualTo) }]),
        ]);

        var (result, items) = await _db.Query(query);
        return result == OperationResult.Success ? items.Count : 0;
    }

    private async Task<List<T>> FetchAllAsync<T>(
        string collection,
        IReadOnlyList<string> ids,
        Func<ReadOnlyMemory<byte>, T?> deserialize,
        CancellationToken ct)
        where T : class
    {
        var items = new List<T>(ids.Count);
        if (_db is null)
            return items;

        foreach (string id in ids)
        {
            var (found, payload) = await _db.Get(new GetElementCommand(collection, id));
            if (found)
            {
                var item = deserialize(payload);
                if (item is not null)
                    items.Add(item);
            }
        }

        return items;
    }

    // Serializzazione source-generated, una voce per tipo: i contesti JSON
    // generati espongono una proprietà per tipo registrato (come fa
    // RequestLogJsonContext in MainSite) e niente reflection.
    private static byte[] Serialize(Member value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, TheQultJsonContext.Default.Member);

    private static byte[] Serialize(StayRequest value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, TheQultJsonContext.Default.StayRequest);

    private static byte[] Serialize(Reference value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, TheQultJsonContext.Default.Reference);

    private static Member? DeserializeMember(ReadOnlyMemory<byte> payload) =>
        JsonSerializer.Deserialize(payload.Span, TheQultJsonContext.Default.Member);

    private static StayRequest? DeserializeStay(ReadOnlyMemory<byte> payload) =>
        JsonSerializer.Deserialize(payload.Span, TheQultJsonContext.Default.StayRequest);

    private static Reference? DeserializeReference(ReadOnlyMemory<byte> payload) =>
        JsonSerializer.Deserialize(payload.Span, TheQultJsonContext.Default.Reference);

    private static IndexElementCommand MemberIndexingOf(Member member) => new(
        Scalar:
        [
            new ScalarIndexCommand("email", false, NormalizeEmail(member.Email)),
            new ScalarIndexCommand("city", false, NormalizeCity(member.City)),
            new ScalarIndexCommand("hosting", false, member.Hosting),
            new ScalarIndexCommand("createdAt", false, member.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)),
        ],
        Geographic: null,
        Vectorial: null,
        FullText:
        [
            new FullTextIndexCommand("displayName", member.DisplayName),
            new FullTextIndexCommand("about", member.About),
            new FullTextIndexCommand("city", member.City),
        ]);

    private static IndexElementCommand StayIndexingOf(StayRequest stay) => new(
        Scalar:
        [
            new ScalarIndexCommand("host", false, stay.HostId),
            new ScalarIndexCommand("guest", false, stay.GuestId),
            new ScalarIndexCommand("status", false, stay.Status),
            new ScalarIndexCommand("createdAt", false, stay.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)),
        ],
        Geographic: null,
        Vectorial: null,
        FullText:
        [
            new FullTextIndexCommand("message", stay.Message),
        ]);

    private static IndexElementCommand ReferenceIndexingOf(Reference reference) => new(
        Scalar:
        [
            new ScalarIndexCommand("author", false, reference.AuthorId),
            new ScalarIndexCommand("subject", false, reference.SubjectId),
            new ScalarIndexCommand("stay", false, reference.StayId),
            new ScalarIndexCommand("createdAt", false, reference.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)),
        ],
        Geographic: null,
        Vectorial: null,
        FullText:
        [
            new FullTextIndexCommand("text", reference.Text),
        ]);

    public static string NormalizeEmail(string email) => (email ?? "").Trim().ToLowerInvariant();

    public static string NormalizeCity(string city) =>
        string.Join(' ', (city ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    public void Dispose()
    {
        _initGate.Dispose();
        if (_disposed)
            return;

        _disposed = true;
        _db?.Dispose();
    }
}

// Una pagina dell'elenco /members.
public sealed record MembersPage(
    IReadOnlyList<Member> Items,
    int Total,
    int Page,
    int PageCount)
{
    public static readonly MembersPage Empty = new([], 0, 1, 1);
}
