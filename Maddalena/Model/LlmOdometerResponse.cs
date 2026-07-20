namespace Maddalena.Model;

/// <summary>
/// L'odometro dell'LLM: quanti token il modello ha macinato in tutta la sua vita,
/// diviso per attività. Ogni voce si SPIEGA DA SÉ — nome leggibile, cosa la fa
/// muovere, e se cresce dal vivo o solo all'avvio — così è chiaro cosa si guarda.
/// Sopravvive ai riavvii (viaggia nello stato salvato su disco).
///
/// I contatori sono BigInteger senza tetto → viaggiano come STRINGHE decimali:
/// i number di JavaScript perdono precisione oltre 2^53. Il campo TokensHuman è
/// solo per l'occhio (arrotondato); per i conti usare Tokens.
/// </summary>
public sealed class LlmOdometerResponse
{
    /// <summary>Le voci dell'odometro, la più "viva" per prima.</summary>
    public LlmOdometerEntry[] Counters { get; set; } = [];

    /// <summary>Tutto sommato, in token (stringa decimale).</summary>
    public string TotalTokens { get; set; } = "0";

    /// <summary>Il totale in forma leggibile (es. "305,0 mln").</summary>
    public string TotalHuman { get; set; } = "0";
}

/// <summary>Una singola voce dell'odometro, autoesplicativa.</summary>
public sealed class LlmOdometerEntry
{
    /// <summary>Identificativo stabile per il codice: trained, evaluated, absorbed, prompted, generated.</summary>
    public string Key { get; set; } = "";

    /// <summary>Nome leggibile in italiano.</summary>
    public string Label { get; set; } = "";

    /// <summary>Token contati per questa voce (stringa decimale, senza tetto — per i conti).</summary>
    public string Tokens { get; set; } = "0";

    /// <summary>Lo stesso numero arrotondato per l'occhio (es. "101,6 mln").</summary>
    public string TokensHuman { get; set; } = "0";

    /// <summary>
    /// true = si muove mentre il modello serve/impara (lo vedi salire nel tempo);
    /// false = si muove UNA volta (all'avvio) e poi resta fermo fino al riavvio.
    /// </summary>
    public bool Live { get; set; }

    /// <summary>In una riga: cosa fa muovere questo numero.</summary>
    public string Explanation { get; set; } = "";
}
