namespace MainSite.Auth;

// Local-account sign-in: the site owns the accounts (SQLite), with no external
// identity provider. The bootstrap credentials create the FIRST account only;
// once at least one account exists they are ignored and can be removed.
public sealed class LocalAuthOptions
{
    public const string Section = "Auth";

    // SQLite file. A relative path resolves against the content root;
    // production uses the absolute /data/auth/auth.db on a Docker volume.
    public string DatabasePath { get; set; } = "App_Data/auth/auth.db";

    // Read once at startup, only while the users table is empty.
    public string BootstrapUsername { get; set; } = "";
    public string BootstrapPassword { get; set; } = "";
    public string BootstrapDisplayName { get; set; } = "";

    public int MinPasswordLength { get; set; } = 12;
}
