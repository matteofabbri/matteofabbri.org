namespace TheQult.Data;

// Una richiesta di ospitalità: un ospite chiede un tetto a un host, con date
// e un messaggio personale. L'host accetta o rifiuta; chi ha chiesto può
// cancellare finché è in attesa. Lo stato "accepted" è quello che sblocca le
// referenze: è il minimo contratto della comunità.
public sealed record StayRequest
{
    public required string Id { get; init; }

    public required string GuestId { get; init; }
    public required string HostId { get; init; }

    // Notti richieste (estremi inclusi): partenza deve essere dopo l'arrivo.
    public required DateOnly Arrival { get; init; }
    public required DateOnly Departure { get; init; }

    public string Message { get; init; } = "";

    public string Status { get; init; } = StatusPending;

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public const string StatusPending = "pending";
    public const string StatusAccepted = "accepted";
    public const string StatusDeclined = "declined";
    public const string StatusCancelled = "cancelled";

    public static bool IsValidStatus(string value) =>
        value is StatusPending or StatusAccepted or StatusDeclined or StatusCancelled;

    // Chi partecipa al soggiorno, per le regole delle referenze.
    public bool Involves(string memberId) =>
        GuestId == memberId || HostId == memberId;

    // L'altro della coppia: a chi si scrive la referenza.
    public string OtherThan(string memberId) =>
        GuestId == memberId ? HostId : GuestId;
}
