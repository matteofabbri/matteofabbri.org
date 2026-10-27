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

    public BlogAuthOptions Auth { get; set; } = new();
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
public sealed class BlogAuthState(BlogAuthOptions options, bool isDevelopment)
{
    public BlogAuthOptions Options { get; } = options;
    public bool UseFake { get; } = options.UseFake && isDevelopment;
    public bool Configured { get; } = options.IsConfigured;

    // Basta il client OAuth2: qualunque casella che mailcow autentica può
    // scrivere, non c'è un elenco di autori da mantenere.
    public bool Enabled { get; } = (options.UseFake && isDevelopment)
        || options.IsConfigured;

    // In sviluppo l'accesso finto entra con un'identità qualsiasi, così le
    // pagine admin hanno un autore anche senza mailcow.
    public string FakeAuthor => "sviluppo@localhost";

    // Cookie di autore: il claim viene messo solo dopo che mailcow ha
    // confermato che la casella esiste (vedi BlogAuth).
    public bool IsAuthor(ClaimsPrincipal user) => BlogAuth.IsAuthor(user);
}
