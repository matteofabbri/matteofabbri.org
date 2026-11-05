namespace TheQult.Data;

// Una referenza: il giudizio che ospite e ospitato si lasciano l'uno
// dell'altro DOPO un soggiorno accettato. Una sola per soggiorno per
// direzione (l'host ne scrive una all'ospite e viceversa, non due). Il
// verdetto ha tre livelli come una volta; il testo è la parte che conta.
public sealed record Reference
{
    public required string Id { get; init; }

    // Il soggiorno accettato che l'ha meritata: garantisce che la referenza
    // racconti qualcosa che è davvero successo.
    public required string StayId { get; init; }

    public required string AuthorId { get; init; }
    public required string SubjectId { get; init; }

    public string Verdict { get; init; } = VerdictPositive;

    public string Text { get; init; } = "";

    public required DateTimeOffset CreatedAt { get; init; }

    public const string VerdictPositive = "positive";
    public const string VerdictNeutral = "neutral";
    public const string VerdictNegative = "negative";

    public static bool IsValidVerdict(string value) =>
        value is VerdictPositive or VerdictNeutral or VerdictNegative;
}
