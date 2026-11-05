namespace TheQult.Data;

// Un membro della comunità. Il payload finisce in Sperone così com'è (JSON
// camelCase): non c'è schema sul server, quindi aggiungere un campo qui non
// richiede migrazioni.
//
// L'email serve solo al login: nessuna pagina pubblica la mostra. Le chiavi
// "hosting" (host/maybe/no) e "city" sono indicizzate in Sperone, quindi la
// ricerca degli host non legge i payload.
public sealed record Member
{
    // ItemId di Sperone: un Guid "N", stabile e senza ordine di tempo
    // (l'ordinamento usa l'indice scalare "createdAt").
    public required string Id { get; init; }

    // Minuscolo e senza spazi: il login cerca per indice scalare "email".
    public required string Email { get; init; }

    // Formato di Services.PasswordHasher (pbkdf2-sha512.v1$...). Mai in chiaro.
    public required string PasswordHash { get; init; }

    public required string DisplayName { get; init; }

    // Città com'è scritta nel profilo; nell'indice va in minuscolo, senza
    // spazi di troppo, così la ricerca "Roma" trova "roma".
    public string City { get; init; } = "";

    // ISO 3166-1 alpha-2 ("IT", "FR"); vuoto = non dichiarato.
    public string Country { get; init; } = "";

    // Chi sei e come ospiti. Testo libero, limite alto ma finito.
    public string About { get; init; } = "";

    public string Hosting { get; init; } = HostingNo;

    // Ospiti al massimo così tante persone per notte.
    public int MaxGuests { get; init; }

    // Lingue parlate, in una riga libera ("Italiano, English, ...").
    public string Languages { get; init; } = "";

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public const string HostingHost = "host";
    public const string HostingMaybe = "maybe";
    public const string HostingNo = "no";

    public static bool IsValidHosting(string value) =>
        value is HostingHost or HostingMaybe or HostingNo;
}
