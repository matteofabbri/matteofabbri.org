namespace MainSite.Model;

// L'elenco dei download di Sperone: la versione dell'ultima release e un pacchetto per piattaforma.
// Lo produce DownloadController leggendo l'ultima release da GitHub (in cache); serve alle pagine
// statiche per mostrare versione e dimensioni accanto ai pulsanti.
public sealed class SperoneDownloadsResponse
{
    public string Version { get; set; } = "";

    public IReadOnlyList<SperoneDownload> Assets { get; set; } = [];
}
