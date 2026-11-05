using System.Globalization;
using TheQult.Data;

namespace TheQult.Pages;

// Etichette inglesi per i codici salvati nei payload (UI tutta in inglese,
// regola della casa). Le frasi d'errore vivono in Errors.
public static class Display
{
    public static string Hosting(string hosting) => hosting switch
    {
        Member.HostingHost => "Can host",
        Member.HostingMaybe => "Maybe",
        Member.HostingNo => "Not hosting",
        _ => "Unknown",
    };

    public static string Verdict(string verdict) => verdict switch
    {
        Reference.VerdictPositive => "Positive",
        Reference.VerdictNeutral => "Neutral",
        Reference.VerdictNegative => "Negative",
        _ => "—",
    };

    public static string StayStatus(string status) => status switch
    {
        StayRequest.StatusPending => "Waiting for a reply",
        StayRequest.StatusAccepted => "Accepted",
        StayRequest.StatusDeclined => "Declined",
        StayRequest.StatusCancelled => "Cancelled",
        _ => status,
    };

    public static string Day(DateOnly value) =>
        value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Day(DateTimeOffset value) =>
        value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}

// Traduzione dei codici d'errore di CommunityService in frasi per le persone.
public static class Errors
{
    public static string For(string? code) => code switch
    {
        "email" => "That email address doesn't look right.",
        "email-taken" => "That email is already registered. Do you want to log in instead?",
        "password" => "Passwords are at least 8 characters.",
        "display-name" => "Please tell us your name (up to 60 characters).",
        "hosting" => "Choose a hosting status.",
        "country" => "The country code is two letters, like IT.",
        "city" => "The city name is a bit too long.",
        "max-guests" => "Guests per night: between 0 and 16.",
        "about" => "The introduction is a bit too long (2000 characters max).",
        "self" => "You cannot stay with yourself — however pleasant that sounds.",
        "host-not-found" => "That member does not exist.",
        "host-not-hosting" => "This member is not hosting right now.",
        "dates" => "Departure must be after arrival.",
        "past" => "The arrival date is in the past.",
        "too-far" => "You can plan up to a year ahead.",
        "too-long" => "Stays are up to 30 nights.",
        "message" => "The message is a bit too long (1000 characters max).",
        "not-found" => "We couldn't find that.",
        "closed" => "This request is already settled.",
        "action" => "Unknown action.",
        "forbidden" => "This is not yours to do.",
        "stay-not-accepted" => "References are written after an accepted stay.",
        "verdict" => "Choose a verdict.",
        "text" => "Write a few words (up to 1500 characters).",
        "duplicate" => "You already wrote a reference for this stay.",
        _ => "Something went wrong.",
    };
}
