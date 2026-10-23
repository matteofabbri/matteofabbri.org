using System.Security.Claims;

namespace MainSite.Blog;

// Configurazione della sezione "Blog" di appsettings.json. I percorsi relativi
// si intendono rispetto alla content root (in sviluppo la cartella del progetto,
// in produzione /app); sul server il volume è montato in /data/blog e il valore
// arriva da Blog__ContentPath.
public sealed class BlogOptions
{
    public const string Section = "Blog";

    public string ContentPath { get; set; } = "App_Data/blog";

    // Fuso usato per mostrare e interpretare le date nell'area di scrittura.
    // Il server gira in UTC: senza questo, "23 ottobre 09:00" sarebbe ambiguo.
    public string TimeZone { get; set; } = "Europe/Rome";

    // Nome mostrato ai lettori. L'email della casella resta nei metadati del
    // post, ma non è quello che vogliamo stampare in fondo all'articolo.
    public string AuthorName { get; set; } = "Matteo Fabbri";

    // Caselle mailcow abilitate a scrivere, separate da virgola o punto e virgola.
    // Il confronto è case-insensitive. Vuoto = nessun autore: l'accesso resta
    // spento finché non si configura almeno una casella (come per le prenotazioni).
    public string Authors { get; set; } = "";

    public BlogAuthOptions Auth { get; set; } = new();

    // Elenco normalizzato degli autori. Le voci senza "@" sono refusi di
    // configurazione e vengono ignorate: meglio nessun autore che un accesso
    // aperto per sbaglio.
    public IReadOnlySet<string> ParseAuthors()
    {
        var authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in Authors.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (raw.Contains('@'))
                authors.Add(raw);
        return authors;
    }
}

// Autenticazione delegata a mailcow: il sito manda l'utente sulla webmail,
// riceve un codice e lo scambia con un token. La password della casella non
// passa mai dal blog, e il 2FA di mailcow continua a funzionare.
public sealed class BlogAuthOptions
{
    // Accesso finto per lo sviluppo locale: nessun redirect e nessun token.
    // In produzione viene ignorato anche se impostato (vedi Program.cs).
    public bool UseFake { get; set; }

    // Radice pubblica di mailcow (la stessa della webmail). Gli endpoint OAuth2
    // sono /oauth/authorize, /oauth/token e /oauth/profile sotto questa radice.
    public string BaseUrl { get; set; } = "https://mail.maddalena.me";

    // Client OAuth2 creato in mailcow: Configurazione → Access → OAuth2 Apps.
    // Il redirect URI deve essere esattamente <sito>/admin/login/callback.
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    public string AuthorizeUrl => $"{BaseUrl.TrimEnd('/')}/oauth/authorize";
    public string TokenUrl => $"{BaseUrl.TrimEnd('/')}/oauth/token";
    public string ProfileUrl => $"{BaseUrl.TrimEnd('/')}/oauth/profile";
}

// Stato effettivo dell'autenticazione, calcolato una volta all'avvio: le pagine
// non devono decidere da sole se l'accesso finto è attivo (in produzione non lo è
// mai, anche se appsettings lo chiede).
public sealed class BlogAuthState(BlogAuthOptions options, bool isDevelopment, IReadOnlySet<string> authors)
{
    public BlogAuthOptions Options { get; } = options;
    public IReadOnlySet<string> Authors { get; } = authors;
    public bool UseFake { get; } = options.UseFake && isDevelopment;
    public bool Configured { get; } = options.IsConfigured;

    // Con OAuth configurato ma nessuna casella abilitata non c'è nessuno che
    // possa entrare: meglio dirlo sulla pagina di login che far fare un giro
    // fino a mailcow per poi rifiutare.
    public bool Enabled { get; } = (options.UseFake && isDevelopment)
        || (options.IsConfigured && authors.Count > 0);

    // In sviluppo l'accesso finto entra come primo autore configurato, così le
    // pagine admin hanno un'identità (e quindi un autore) anche senza mailcow.
    public string FakeAuthor => Authors.FirstOrDefault() ?? "sviluppo@localhost";

    public bool IsAllowed(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && (Authors.Contains(email)
            || (UseFake && string.Equals(email, FakeAuthor, StringComparison.OrdinalIgnoreCase)));

    // Cookie di autore + casella ancora in elenco. È il controllo da usare
    // ovunque si debba sapere "questa persona può scrivere ADESSO": togliere
    // una casella da Blog:Authors invalida anche i cookie già emessi.
    public bool IsAuthor(ClaimsPrincipal user) =>
        BlogAuth.IsAuthor(user)
        && IsAllowed(user.FindFirst(ClaimTypes.Email)?.Value);
}
