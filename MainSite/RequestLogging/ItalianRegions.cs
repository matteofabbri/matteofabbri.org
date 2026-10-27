namespace MainSite.RequestLogging;

// Una regione italiana: codice ISTAT 1-20 (quello che indicizziamo in Sperone)
// e nome mostrato. Code 0 = non geolocalizzata / estera.
public readonly record struct ItalianRegion(int Code, string Name)
{
    public static readonly ItalianRegion Unknown = new(0, "Sconosciuta");
}

// Elenco unico delle 20 regioni. Gli ISO 3166-2 sono quelli che usano i
// database GeoIP (DB-IP/MaxMind) nei campi "subdivision"; il codice numerico è
// invece l'ISTAT 1-20, comodo e stabile per l'indice scalare di Sperone.
public static class ItalianRegions
{
    public static readonly IReadOnlyList<ItalianRegion> All =
    [
        new(1, "Piemonte"),
        new(2, "Valle d'Aosta/Vallée d'Aoste"),
        new(3, "Lombardia"),
        new(4, "Trentino-Alto Adige/Südtirol"),
        new(5, "Veneto"),
        new(6, "Friuli-Venezia Giulia"),
        new(7, "Liguria"),
        new(8, "Emilia-Romagna"),
        new(9, "Toscana"),
        new(10, "Umbria"),
        new(11, "Marche"),
        new(12, "Lazio"),
        new(13, "Abruzzo"),
        new(14, "Molise"),
        new(15, "Campania"),
        new(16, "Puglia"),
        new(17, "Basilicata"),
        new(18, "Calabria"),
        new(19, "Sicilia"),
        new(20, "Sardegna"),
    ];

    private static readonly Dictionary<string, ItalianRegion> ByIsoCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IT-21"] = All[0],
        ["IT-23"] = All[1],
        ["IT-25"] = All[2],
        ["IT-32"] = All[3],
        ["IT-34"] = All[4],
        ["IT-36"] = All[5],
        ["IT-42"] = All[6],
        ["IT-45"] = All[7],
        ["IT-52"] = All[8],
        ["IT-55"] = All[9],
        ["IT-57"] = All[10],
        ["IT-62"] = All[11],
        ["IT-65"] = All[12],
        ["IT-67"] = All[13],
        ["IT-72"] = All[14],
        ["IT-75"] = All[15],
        ["IT-77"] = All[16],
        ["IT-78"] = All[17],
        ["IT-82"] = All[18],
        ["IT-88"] = All[19],
    };

    private static readonly Dictionary<int, ItalianRegion> ByCode = All.ToDictionary(r => r.Code);

    // DB-IP Lite (e a volte GeoLite2) non compila il campo ISO della
    // suddivisione e usa il nome in inglese. Accettiamo sia i nomi inglesi sia
    // quelli italiani: il primo che combacia vince.
    private static readonly Dictionary<string, ItalianRegion> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Piedmont"] = All[0], ["Piemonte"] = All[0],
        ["Aosta Valley"] = All[1], ["Valle d'Aosta"] = All[1], ["Vallée d'Aoste"] = All[1],
        ["Valle d'Aosta/Vallée d'Aoste"] = All[1],
        ["Lombardy"] = All[2], ["Lombardia"] = All[2],
        ["Trentino-Alto Adige"] = All[3], ["Trentino Alto Adige"] = All[3],
        ["Trentino-Alto Adige/Südtirol"] = All[3], ["Trentino-South Tyrol"] = All[3],
        ["Trentino-Südtirol"] = All[3], ["Trentino"] = All[3], ["Südtirol"] = All[3], ["Alto Adige"] = All[3],
        ["Veneto"] = All[4],
        ["Friuli-Venezia Giulia"] = All[5], ["Friuli Venezia Giulia"] = All[5],
        ["Liguria"] = All[6],
        ["Emilia-Romagna"] = All[7], ["Emilia Romagna"] = All[7],
        ["Tuscany"] = All[8], ["Toscana"] = All[8],
        ["Umbria"] = All[9],
        ["Marche"] = All[10], ["The Marches"] = All[10],
        ["Lazio"] = All[11], ["Latium"] = All[11],
        ["Abruzzo"] = All[12], ["Abruzzi"] = All[12],
        ["Molise"] = All[13],
        ["Campania"] = All[14],
        ["Apulia"] = All[15], ["Puglia"] = All[15],
        ["Basilicata"] = All[16],
        ["Calabria"] = All[17],
        ["Sicily"] = All[18], ["Sicilia"] = All[18],
        ["Sardinia"] = All[19], ["Sardegna"] = All[19],
    };

    public static ItalianRegion FromCode(int code) =>
        ByCode.TryGetValue(code, out var region) ? region : ItalianRegion.Unknown;

    public static bool TryFromIsoCode(string? isoCode, out ItalianRegion region)
    {
        if (!string.IsNullOrWhiteSpace(isoCode) && ByIsoCode.TryGetValue(isoCode.Trim(), out var found))
        {
            region = found;
            return true;
        }

        region = ItalianRegion.Unknown;
        return false;
    }

    public static bool TryFromName(string? name, out ItalianRegion region)
    {
        if (!string.IsNullOrWhiteSpace(name) && ByName.TryGetValue(name.Trim(), out var found))
        {
            region = found;
            return true;
        }

        region = ItalianRegion.Unknown;
        return false;
    }
}
