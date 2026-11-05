namespace TheQult.Data;

// Configurazione di TheQult (sezione "TheQult" in appsettings.json). Il
// percorso dei dati arriva dall'AppHost: in produzione è il volume Docker
// /data/theqult, in locale una cartella accanto al progetto, come per il blog
// e i log di MainSite.
public sealed class TheQultOptions
{
    public const string Section = "TheQult";

    // Cartella che contiene il database Sperone e le chiavi DataProtection.
    public string DataPath { get; set; } = "App_Data/theqult";

    public string DatabaseName { get; set; } = "theqult";
    public string MembersCollection { get; set; } = "members";
    public string StaysCollection { get; set; } = "stays";
    public string ReferencesCollection { get; set; } = "references";
}
