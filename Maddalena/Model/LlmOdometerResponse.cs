namespace Maddalena.Model;

/// <summary>
/// L'odometro dell'LLM: token macinati in tutta la vita del modello, per
/// attivita'. Sopravvive ai riavvii (viaggia nello stato salvato su disco).
/// I contatori sono BigInteger senza tetto, quindi viaggiano come STRINGHE
/// decimali: i number di JavaScript perdono precisione oltre 2^53.
/// </summary>
public sealed class LlmOdometerResponse
{
    public long ParameterCount { get; set; }

    /// <summary>Token consumati dal training batch (finestre per epoche).</summary>
    public string Trained { get; set; } = "0";

    /// <summary>Token valutati (perplexity, sonde prima/dopo di ogni lezione).</summary>
    public string Evaluated { get; set; } = "0";

    /// <summary>Token assorbiti dall'apprendimento online (teach + pagine crawlate).</summary>
    public string Absorbed { get; set; } = "0";

    /// <summary>Token letti come prompt dalla generazione.</summary>
    public string Prompted { get; set; } = "0";

    /// <summary>Token emessi dal modello.</summary>
    public string Generated { get; set; } = "0";

    /// <summary>Tutto, sommato.</summary>
    public string Total { get; set; } = "0";
}
