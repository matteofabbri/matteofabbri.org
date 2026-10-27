namespace MainSite.RequestLogging;

// Configurazione della sezione "RequestLog" di appsettings.json.
//
// Il database è Sperone EMBEDDED: la libreria gira nello stesso processo del
// sito e scrive in una cartella su disco. Non c'è un server da contattare né
// una connection string.
public sealed class RequestLogOptions
{
    public const string Section = "RequestLog";

    // Spento = nessun middleware, nessun database, nessuna coda.
    public bool Enabled { get; set; } = true;

    // Cartella del database Sperone. Relativa = rispetto alla content root
    // (in sviluppo MainSite/, in produzione /app).
    // In produzione arriva da RequestLog__Path = /data/sperone, montata sul
    // volume Docker request-log-data: così i log sopravvivono ai deploy.
    public string Path { get; set; } = "App_Data/sperone";

    // Database MMDB per la geolocalizzazione degli IP (DB-IP Lite City o
    // GeoLite2 City). Relativo = rispetto alla content root. Se il file non
    // c'è, il log funziona lo stesso con paese "sconosciuto" e la mappa
    // richieste lo segnala. Download: deploy/update-geoip.*.
    public string GeoIpDatabasePath { get; set; } = "App_Data/geoip/dbip-city-lite.mmdb";

    // Nome "logico" del database nei metadata delle collezioni.
    public string DatabaseName { get; set; } = "matteofabbri";

    // Collezione in cui finiscono le richieste. Viene creata al primo avvio.
    public string Collection { get; set; } = "requests";

    // Giorni di conservazione dei log; 0 o negativo = per sempre.
    // Sperone applica la scadenza da solo (TTL per elemento, in secondi).
    public int RetentionDays { get; set; } = 90;

    // Quante righe restano in coda se il database non tiene il passo. Oltre
    // questo limite le nuove richieste vengono scartate (il sito non si blocca).
    public int QueueCapacity { get; set; } = 8_192;

    // Quanti elementi per singolo InsertMany (una transazione di scrittura).
    public int BatchSize { get; set; } = 200;

    // Ogni quanto la coda viene svuotata, in secondi.
    public int FlushSeconds { get; set; } = 1;

    // Dimensione in memoria dei cassetti FASTER: cache line da 64 byte per la
    // tabella hash, bit per la finestra di log. I default sono volutamente
    // modesti (log di un sito personale, non un database primario): alzali se
    // il volume di richieste cresce. Viaggiano in StoreSizing.
    public long HashCacheLines { get; set; } = 1L << 16; // ~4 MB per store
    public int? LogMemoryBits { get; set; } = 23;        // ~8 MB
    public int? LogPageBits { get; set; } = 22;          // ~4 MB

    // Percorsi esclusi dal log (confronto per segmento, non per prefisso
    // grezzo: "/health" non esclude "/healthcheck"). Default: health check.
    public string[] ExcludePaths { get; set; } = ["/health", "/alive"];

    public string EffectivePath => string.IsNullOrWhiteSpace(Path) ? "App_Data/sperone" : Path.Trim();
    public string EffectiveGeoIpDatabasePath => string.IsNullOrWhiteSpace(GeoIpDatabasePath)
        ? "App_Data/geoip/dbip-city-lite.mmdb"
        : GeoIpDatabasePath.Trim();
    public string EffectiveDatabaseName => string.IsNullOrWhiteSpace(DatabaseName) ? "matteofabbri" : DatabaseName.Trim();
    public string EffectiveCollection => string.IsNullOrWhiteSpace(Collection) ? "requests" : Collection.Trim();
    public int EffectiveQueueCapacity => Math.Clamp(QueueCapacity, 16, 1_000_000);
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 1_000);
    public TimeSpan EffectiveFlushInterval => TimeSpan.FromSeconds(Math.Clamp(FlushSeconds, 1, 60));
    public long EffectiveHashCacheLines => HashCacheLines >= 1_024 ? HashCacheLines : 1L << 16;
    public int? EffectiveLogMemoryBits => LogMemoryBits is >= 16 and <= 26 ? LogMemoryBits : 23;
    public int? EffectiveLogPageBits => LogPageBits is >= 16 and <= 26 ? LogPageBits : 22;
    public long? RetentionSeconds => RetentionDays > 0 ? RetentionDays * 86_400L : null;

    public bool IsExcluded(PathString path)
    {
        foreach (var raw in ExcludePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var excluded = raw.StartsWith('/') ? raw : "/" + raw;
            if (string.Equals(path.Value, excluded, StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
