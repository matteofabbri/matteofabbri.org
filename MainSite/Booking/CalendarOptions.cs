namespace MainSite.Booking;

// Connection to a CalDAV calendar: the booking API reads busy periods and
// writes one event per appointment. Credentials are an app password with DAV
// access, not the mailbox password (real passwords change, and 2FA does not
// work for DAV clients at all).
//
// Empty CalDavUrl (or an empty Password) means bookings are off and the API
// answers 503; the site starts anyway.
public sealed class CalendarOptions
{
    public const string Section = "Calendar";

    // The calendar account is fixed: it is not configured from .env and cannot
    // be wrong. Program.cs forces this value with PostConfigure.
    public const string FixedUsername = "matteo@matteofabbri.org";

    // DAV base. It must end with a "/" (it is normalized).
    public string CalDavUrl { get; set; } = "";

    // The mailbox: the full email address is the user name.
    public string Username { get; set; } = FixedUsername;

    // App password with DAV access. Empty = bookings off (503), the site starts
    // anyway: an incomplete .env must not take the sites down.
    public string Password { get; set; } = "";

    // The calendar to read and write, relative to the principal. "personal"
    // is the usual default; a dedicated calendar would put its path here.
    public string CalendarPath { get; set; } = "Calendar/personal/";

    // In-memory fake calendar for local development (no credentials).
    // NEVER in production: bookings would not go anywhere.
    public bool UseFake { get; set; }
}
