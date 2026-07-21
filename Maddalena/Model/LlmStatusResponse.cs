namespace Maddalena.Model;

public sealed class LlmStatusResponse
{
    /// <summary>starting | training | ready | off | failed.</summary>
    public string State { get; set; } = "";

    /// <summary>true se il modello e' stato ripreso dallo stato su disco, false se e' nato ora.</summary>
    public bool Resumed { get; set; }

    public DateTimeOffset? LastSavedAt { get; set; }

    /// <summary>Cosa hanno fruttato le datasources all'ultimo training di avvio.</summary>
    public string? CorpusSummary { get; set; }

    public string? LastError { get; set; }

    /// <summary>Backend di calcolo attivo: "cuda" se una GPU NVIDIA e' in uso, "cpu" altrimenti.</summary>
    public string Backend { get; set; } = "cpu";

    public long? ParameterCount { get; set; }

    // L'odometro del modello: token macinati in tutta la sua vita, per attivita'.
    // Stringhe decimali, non number: i contatori sono BigInteger senza tetto e i
    // number di JavaScript perdono precisione oltre 2^53.
    public string? TokensTrained { get; set; }

    public string? TokensAbsorbed { get; set; }

    public string? TokensPrompted { get; set; }

    public string? TokensGenerated { get; set; }

    // Il crawler: pagine assorbite da questo processo e coda su Postgres
    // (null se la tabella non esiste ancora, es. crawler spento).
    public long? PagesLearned { get; set; }

    public long? QueuePending { get; set; }

    public long? QueueKnown { get; set; }

    // Il corpus sedimentato su disco: quanto testo il crawler ha messo da parte
    // per il futuro (tokenizer BPE, pretraining). Cresce sempre, non si svuota.
    public long? CorpusBytes { get; set; }

    public long? CorpusDocuments { get; set; }
}
