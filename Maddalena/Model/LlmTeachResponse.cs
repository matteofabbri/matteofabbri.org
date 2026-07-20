namespace Maddalena.Model;

public sealed class LlmTeachResponse
{
    /// <summary>Quanto il testo sorprendeva il modello PRIMA di assorbirlo (loss media).</summary>
    public float SurpriseBefore { get; set; }

    /// <summary>Quanto lo sorprende DOPO: se scende, ha imparato.</summary>
    public float SurpriseAfter { get; set; }

    public int Steps { get; set; }

    /// <summary>Quando lo stato con la lezione e' stato scritto su disco.</summary>
    public DateTimeOffset? SavedAt { get; set; }
}
