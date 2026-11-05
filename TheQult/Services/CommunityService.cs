using TheQult.Data;

namespace TheQult.Services;

// Le regole della comunità, separate dalle pagine e dallo store così si
// provano da sole nei test: chi può cambiare lo stato di un soggiorno, quando
// nasce una referenza, cosa è un profilo valido. Gli errori sono CODICI brevi
// ("email-taken"); le pagine li traducono nelle loro frasi in inglese.
public sealed class CommunityService
{
    public const int MaxStayNights = 30;
    public const int MaxStayHorizonDays = 365;
    public const int MaxMessageLength = 1000;
    public const int MaxReferenceLength = 1500;

    private readonly TheQultStore _store;
    private readonly TimeProvider _time;

    public CommunityService(TheQultStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    public sealed record Result<T>(T? Value, string? Error) where T : class
    {
        public bool Ok => Error is null;

        public static Result<T> Success(T value) => new(value, null);
        public static Result<T> Fail(string error) => new(null, error);
    }

    // Cosa serve per iscriversi. L'email e la password non si cambiano dopo
    // (cambio email/password: nella roadmap, col recupero via mail).
    public sealed record JoinInput(
        string Email,
        string Password,
        string DisplayName,
        string City,
        string Country,
        string Hosting,
        int MaxGuests,
        string Languages,
        string About);

    // Cosa si può modificare dal profilo.
    public sealed record ProfileInput(
        string DisplayName,
        string City,
        string Country,
        string Hosting,
        int MaxGuests,
        string Languages,
        string About);

    // --- iscrizione ---------------------------------------------------------

    public async Task<Result<Member>> JoinAsync(JoinInput input, CancellationToken ct = default)
    {
        string email = TheQultStore.NormalizeEmail(input.Email);
        if (email.Length is < 5 or > 120 || !email.Contains('@') || email.EndsWith('@') || email.Contains(' '))
            return Result<Member>.Fail("email");

        if (string.IsNullOrWhiteSpace(input.Password) || input.Password.Length is < 8 or > 200)
            return Result<Member>.Fail("password");

        string displayName = input.DisplayName.Trim();
        if (displayName.Length is < 1 or > 60)
            return Result<Member>.Fail("display-name");

        string hosting = input.Hosting;
        if (!Member.IsValidHosting(hosting))
            return Result<Member>.Fail("hosting");

        string country = input.Country.Trim().ToUpperInvariant();
        if (country.Length is not (0 or 2))
            return Result<Member>.Fail("country");

        string city = input.City.Trim();
        if (city.Length > 80)
            return Result<Member>.Fail("city");

        if (input.MaxGuests is < 0 or > 16)
            return Result<Member>.Fail("max-guests");

        string about = input.About.Trim();
        if (about.Length > 2000)
            return Result<Member>.Fail("about");

        var now = _time.GetUtcNow();
        var member = new Member
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = email,
            PasswordHash = PasswordHasher.Hash(input.Password),
            DisplayName = displayName,
            City = city,
            Country = country,
            About = about,
            Hosting = hosting,
            MaxGuests = input.MaxGuests,
            Languages = input.Languages.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        return await _store.CreateMemberAsync(member, ct) is null
            ? Result<Member>.Fail("email-taken")
            : Result<Member>.Success(member);
    }

    public async Task<Result<Member>> UpdateProfileAsync(
        string memberId, ProfileInput input, CancellationToken ct = default)
    {
        var member = await _store.GetMemberAsync(memberId, ct);
        if (member is null)
            return Result<Member>.Fail("not-found");

        string displayName = input.DisplayName.Trim();
        if (displayName.Length is < 1 or > 60)
            return Result<Member>.Fail("display-name");

        string hosting = input.Hosting;
        if (!Member.IsValidHosting(hosting))
            return Result<Member>.Fail("hosting");

        string country = input.Country.Trim().ToUpperInvariant();
        if (country.Length is not (0 or 2))
            return Result<Member>.Fail("country");

        string city = input.City.Trim();
        if (city.Length > 80)
            return Result<Member>.Fail("city");

        if (input.MaxGuests is < 0 or > 16)
            return Result<Member>.Fail("max-guests");

        string about = input.About.Trim();
        if (about.Length > 2000)
            return Result<Member>.Fail("about");

        var updated = member with
        {
            DisplayName = displayName,
            City = city,
            Country = country,
            Hosting = hosting,
            MaxGuests = input.MaxGuests,
            Languages = input.Languages.Trim(),
            About = about,
            UpdatedAt = _time.GetUtcNow(),
        };

        return await _store.UpdateMemberAsync(updated, ct)
            ? Result<Member>.Success(updated)
            : Result<Member>.Fail("not-found");
    }

    // --- soggiorni ----------------------------------------------------------

    public async Task<Result<StayRequest>> RequestStayAsync(
        string guestId,
        string hostId,
        DateOnly arrival,
        DateOnly departure,
        string message,
        CancellationToken ct = default)
    {
        if (guestId == hostId)
            return Result<StayRequest>.Fail("self");

        var host = await _store.GetMemberAsync(hostId, ct);
        if (host is null)
            return Result<StayRequest>.Fail("host-not-found");

        if (host.Hosting == Member.HostingNo)
            return Result<StayRequest>.Fail("host-not-hosting");

        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        if (departure <= arrival)
            return Result<StayRequest>.Fail("dates");

        if (arrival < today)
            return Result<StayRequest>.Fail("past");

        if (arrival > today.AddDays(MaxStayHorizonDays))
            return Result<StayRequest>.Fail("too-far");

        if (departure.DayNumber - arrival.DayNumber > MaxStayNights)
            return Result<StayRequest>.Fail("too-long");

        if (message.Length > MaxMessageLength)
            return Result<StayRequest>.Fail("message");

        var stay = new StayRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            GuestId = guestId,
            HostId = hostId,
            Arrival = arrival,
            Departure = departure,
            Message = message.Trim(),
            Status = StayRequest.StatusPending,
            CreatedAt = _time.GetUtcNow(),
        };

        return await _store.CreateStayAsync(stay, ct) is null
            ? Result<StayRequest>.Fail("not-found")
            : Result<StayRequest>.Success(stay);
    }

    // Accept/decline sono dell'host, cancel dell'ospite, e solo finché la
    // richiesta è in attesa: dopo, il soggiorno è storia (e referenza).
    public async Task<Result<StayRequest>> ChangeStayStatusAsync(
        string actorId, string stayId, string action, CancellationToken ct = default)
    {
        var stay = await _store.GetStayAsync(stayId, ct);
        if (stay is null)
            return Result<StayRequest>.Fail("not-found");

        if (stay.Status != StayRequest.StatusPending)
            return Result<StayRequest>.Fail("closed");

        string nextStatus = action switch
        {
            "accept" => StayRequest.StatusAccepted,
            "decline" => StayRequest.StatusDeclined,
            "cancel" => StayRequest.StatusCancelled,
            _ => "",
        };
        if (nextStatus.Length == 0)
            return Result<StayRequest>.Fail("action");

        bool actorIsHost = stay.HostId == actorId;
        bool actorIsGuest = stay.GuestId == actorId;
        if (action is "accept" or "decline" && !actorIsHost)
            return Result<StayRequest>.Fail("forbidden");
        if (action == "cancel" && !actorIsGuest)
            return Result<StayRequest>.Fail("forbidden");
        if (!actorIsHost && !actorIsGuest)
            return Result<StayRequest>.Fail("forbidden");

        var updated = stay with { Status = nextStatus, UpdatedAt = _time.GetUtcNow() };
        return await _store.UpdateStayAsync(updated, ct)
            ? Result<StayRequest>.Success(updated)
            : Result<StayRequest>.Fail("not-found");
    }

    // --- referenze ----------------------------------------------------------

    // Una referenza esiste solo per un soggiorno ACCETTATO, la scrive uno dei
    // due partecipanti per l'altro, una sola volta per direzione.
    public async Task<Result<Reference>> WriteReferenceAsync(
        string authorId, string stayId, string verdict, string text, CancellationToken ct = default)
    {
        var stay = await _store.GetStayAsync(stayId, ct);
        if (stay is null)
            return Result<Reference>.Fail("not-found");

        if (stay.Status != StayRequest.StatusAccepted)
            return Result<Reference>.Fail("stay-not-accepted");

        if (!stay.Involves(authorId))
            return Result<Reference>.Fail("forbidden");

        if (!Reference.IsValidVerdict(verdict))
            return Result<Reference>.Fail("verdict");

        text = text.Trim();
        if (text.Length is < 1 or > MaxReferenceLength)
            return Result<Reference>.Fail("text");

        if (await _store.GetReferenceAsync(authorId, stayId, ct) is not null)
            return Result<Reference>.Fail("duplicate");

        var reference = new Reference
        {
            Id = Guid.NewGuid().ToString("N"),
            StayId = stayId,
            AuthorId = authorId,
            SubjectId = stay.OtherThan(authorId),
            Verdict = verdict,
            Text = text,
            CreatedAt = _time.GetUtcNow(),
        };

        return await _store.CreateReferenceAsync(reference, ct) is null
            ? Result<Reference>.Fail("duplicate")
            : Result<Reference>.Success(reference);
    }
}
