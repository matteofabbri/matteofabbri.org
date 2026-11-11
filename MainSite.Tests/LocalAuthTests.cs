using MainSite.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MainSite.Tests;

public sealed class LocalAuthTests : IDisposable
{
    private readonly List<string> _roots = [];

    [Fact]
    public void Password_hash_round_trips_and_rejects_wrong_passwords()
    {
        string hash = LocalPasswordHasher.Hash("correct horse battery staple");

        Assert.True(LocalPasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(LocalPasswordHasher.Verify("wrong password", hash));
        Assert.False(LocalPasswordHasher.Verify("correct horse battery staple", "not-a-hash"));
        Assert.NotEqual(hash, LocalPasswordHasher.Hash("correct horse battery staple"));
    }

    [Fact]
    public async Task Bootstrap_creates_the_first_account_and_is_ignored_afterwards()
    {
        var options = new LocalAuthOptions
        {
            BootstrapUsername = "Matteo",
            BootstrapPassword = "a-very-long-password",
            BootstrapDisplayName = "Matteo Fabbri",
        };
        var store = CreateStore(options);
        await store.InitializeAsync();

        var account = await store.FindCredentialsAsync("matteo");
        Assert.NotNull(account);
        Assert.Equal("matteo", account!.Username);
        Assert.Equal("Matteo Fabbri", account.DisplayName);
        Assert.True(LocalPasswordHasher.Verify("a-very-long-password", account.PasswordHash));

        // A later boot with different bootstrap credentials must not add or reset.
        var again = CreateStore(
            new LocalAuthOptions
            {
                BootstrapUsername = "other",
                BootstrapPassword = "another-long-password",
            },
            store.DatabasePath);
        await again.InitializeAsync();

        Assert.Single(await again.ListAsync());
        Assert.Null(await again.FindCredentialsAsync("other"));
    }

    [Fact]
    public async Task Create_is_case_insensitive_and_duplicate_is_refused()
    {
        var store = CreateStore(new LocalAuthOptions());
        var created = await store.CreateAsync("Matteo", "Matteo", null, "a-very-long-password");

        Assert.Equal("matteo", created.Username);
        Assert.NotNull(await store.FindCredentialsAsync("MATTEO"));
        await Assert.ThrowsAsync<DuplicateUsernameException>(() =>
            store.CreateAsync("matteo", "Other", null, "a-very-long-password"));
    }

    [Fact]
    public async Task Last_enabled_account_cannot_be_disabled()
    {
        var store = CreateStore(new LocalAuthOptions());
        var account = await store.CreateAsync("matteo", "Matteo", null, "a-very-long-password");

        await Assert.ThrowsAsync<LastEnabledUserException>(() =>
            store.SetEnabledAsync(account.Id, false));
    }

    [Fact]
    public async Task Disabling_one_of_two_accounts_is_allowed_and_blocks_it()
    {
        var store = CreateStore(new LocalAuthOptions());
        var first = await store.CreateAsync("matteo", "Matteo", null, "a-very-long-password");
        await store.CreateAsync("anna", "Anna", null, "another-long-password");

        Assert.True(await store.SetEnabledAsync(first.Id, false));
        var credentials = await store.FindCredentialsAsync("matteo");
        Assert.NotNull(credentials);
        Assert.False(credentials!.IsEnabled);
    }

    [Fact]
    public async Task Password_reset_rotates_the_security_stamp()
    {
        var store = CreateStore(new LocalAuthOptions());
        var account = await store.CreateAsync("matteo", "Matteo", null, "a-very-long-password");
        var before = await store.FindCredentialsAsync("matteo");

        Assert.True(await store.SetPasswordAsync(account.Id, "a-new-long-password"));
        var after = await store.FindCredentialsAsync("matteo");

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.NotEqual(before!.SecurityStamp, after!.SecurityStamp);
        Assert.True(LocalPasswordHasher.Verify("a-new-long-password", after.PasswordHash));
        Assert.False(LocalPasswordHasher.Verify("a-very-long-password", after.PasswordHash));
    }

    [Fact]
    public async Task Validation_rejects_bad_username_and_short_password()
    {
        var store = CreateStore(new LocalAuthOptions { MinPasswordLength = 12 });

        await Assert.ThrowsAsync<LocalUserValidationException>(() =>
            store.CreateAsync("a", "A", null, "a-very-long-password"));
        await Assert.ThrowsAsync<LocalUserValidationException>(() =>
            store.CreateAsync("matteo", "Matteo", null, "short"));
    }

    [Fact]
    public async Task Session_lookup_reports_enabled_state()
    {
        var store = CreateStore(new LocalAuthOptions());
        var account = await store.CreateAsync("matteo", "Matteo", null, "a-very-long-password");

        var session = await store.FindSessionAsync(account.Id);
        Assert.NotNull(session);
        Assert.True(session!.IsEnabled);
        Assert.False(string.IsNullOrEmpty(session.SecurityStamp));
        Assert.Null(await store.FindSessionAsync(999));
    }

    private LocalUserStore CreateStore(LocalAuthOptions options, string? databasePath = null)
    {
        string root = NewRoot();
        options.DatabasePath = databasePath ?? Path.Combine(root, "auth.db");
        return new LocalUserStore(
            Options.Create(options),
            new TestHostEnvironment(root),
            NullLogger<LocalUserStore>.Instance);
    }

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mfsite-auth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
