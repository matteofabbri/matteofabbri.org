using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MainSite.Auth;

public sealed record LocalUser(
    int Id,
    string Username,
    string DisplayName,
    string? Email,
    bool IsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc);

// Credentials are kept out of the list/detail records on purpose: only the
// sign-in path and the cookie validator read them.
public sealed record LocalUserCredentials(
    int Id,
    string Username,
    string DisplayName,
    string? Email,
    string PasswordHash,
    string SecurityStamp,
    bool IsEnabled);

public sealed record LocalUserSession(int Id, string SecurityStamp, bool IsEnabled);

public sealed class DuplicateUsernameException(string username)
    : Exception($"The username '{username}' is already taken.");

public sealed class LastEnabledUserException()
    : Exception("The last enabled account cannot be disabled.");

public sealed class LocalUserValidationException(string message) : Exception(message);

// Local accounts in SQLite. One connection per operation: the database is tiny,
// SQLite is fast, and WAL plus a busy timeout keep concurrent logins from
// blocking each other. The schema is created at startup.
public sealed class LocalUserStore(
    IOptions<LocalAuthOptions> options,
    IHostEnvironment environment,
    ILogger<LocalUserStore> logger)
{
    private readonly LocalAuthOptions _options = options.Value;
    private readonly string _databasePath =
        ResolvePath(environment.ContentRootPath, options.Value.DatabasePath);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public string DatabasePath => _databasePath;
    public int MinPasswordLength => Math.Max(8, _options.MinPasswordLength);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            string directory = Path.GetDirectoryName(_databasePath)
                ?? throw new InvalidOperationException($"Cannot resolve the auth database folder for '{_databasePath}'.");
            Directory.CreateDirectory(directory);

            await using var connection = await OpenAsync(ct);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;", ct);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    display_name TEXT NOT NULL,
                    email TEXT NULL,
                    password_hash TEXT NOT NULL,
                    security_stamp TEXT NOT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    last_login_utc TEXT NULL
                );
                """, ct);

            await BootstrapAsync(connection, ct);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        return await ScalarIntAsync(connection, "SELECT EXISTS(SELECT 1 FROM users);", ct) != 0;
    }

    public async Task<LocalUserCredentials?> FindCredentialsAsync(string? username, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        string normalized = LocalUserRules.NormalizeUsername(username);
        if (!LocalUserRules.IsValidUsername(normalized))
            return null;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, username, display_name, email, password_hash, security_stamp, is_enabled
            FROM users
            WHERE username = $username
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$username", normalized);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new LocalUserCredentials(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6) != 0);
    }

    public async Task<LocalUserSession?> FindSessionAsync(int id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, security_stamp, is_enabled FROM users WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new LocalUserSession(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2) != 0);
    }

    public async Task<LocalUser?> FindByIdAsync(int id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, username, display_name, email, is_enabled, created_utc, last_login_utc
            FROM users
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    public async Task<IReadOnlyList<LocalUser>> ListAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, username, display_name, email, is_enabled, created_utc, last_login_utc
            FROM users
            ORDER BY username COLLATE NOCASE;
            """;

        var users = new List<LocalUser>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            users.Add(ReadUser(reader));
        return users;
    }

    public async Task<LocalUser> CreateAsync(
        string? username,
        string? displayName,
        string? email,
        string password,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        string normalized = LocalUserRules.NormalizeUsername(username);
        string? error = LocalUserRules.ValidateUsername(normalized)
            ?? LocalUserRules.ValidateDisplayName(displayName)
            ?? LocalUserRules.ValidateEmail(email)
            ?? LocalUserRules.ValidatePassword(password, MinPasswordLength);
        if (error is not null)
            throw new LocalUserValidationException(error);

        string cleanDisplayName = (displayName ?? "").Trim();
        string? cleanEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

        await using var connection = await OpenAsync(ct);
        int id;
        try
        {
            id = await InsertAsync(connection, normalized, cleanDisplayName, cleanEmail, password, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new DuplicateUsernameException(normalized);
        }

        return new LocalUser(id, normalized, cleanDisplayName, cleanEmail, true, DateTimeOffset.UtcNow, null);
    }

    public async Task<bool> SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        LocalUser? current = await FindByIdAsync(id, ct);
        if (current is null)
            return false;

        if (enabled == current.IsEnabled)
            return true;

        await using var connection = await OpenAsync(ct);
        if (!enabled)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM users WHERE is_enabled = 1 AND id <> $id;";
            countCommand.Parameters.AddWithValue("$id", id);
            if (await ScalarIntAsync(countCommand, ct) == 0)
                throw new LastEnabledUserException();
        }

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE users SET is_enabled = $enabled, updated_utc = $now WHERE id = $id;";
        update.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        update.Parameters.AddWithValue("$now", Now());
        update.Parameters.AddWithValue("$id", id);
        return await update.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SetPasswordAsync(int id, string password, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        string? error = LocalUserRules.ValidatePassword(password, MinPasswordLength);
        if (error is not null)
            throw new LocalUserValidationException(error);

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // A new security stamp invalidates every cookie issued for this account.
        command.CommandText = """
            UPDATE users
            SET password_hash = $hash, security_stamp = $stamp, updated_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$hash", LocalPasswordHasher.Hash(password));
        command.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task TouchLoginAsync(int id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET last_login_utc = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task BootstrapAsync(SqliteConnection connection, CancellationToken ct)
    {
        bool hasUsers = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM users;", ct) > 0;
        if (hasUsers)
        {
            if (!string.IsNullOrWhiteSpace(_options.BootstrapUsername))
                logger.LogInformation("Local accounts already exist: the bootstrap credentials are ignored.");
            return;
        }

        string username = LocalUserRules.NormalizeUsername(_options.BootstrapUsername);
        string password = _options.BootstrapPassword ?? "";
        if (username.Length == 0 && password.Length == 0)
        {
            logger.LogWarning(
                "No local accounts exist. Set Auth__BootstrapUsername and Auth__BootstrapPassword, restart once, then remove them.");
            return;
        }

        string? error = LocalUserRules.ValidateUsername(username)
            ?? LocalUserRules.ValidatePassword(password, MinPasswordLength);
        if (error is not null)
        {
            logger.LogError("Bootstrap credentials are invalid: {Error} No account was created.", error);
            return;
        }

        string displayName = string.IsNullOrWhiteSpace(_options.BootstrapDisplayName)
            ? username
            : _options.BootstrapDisplayName.Trim();
        if (displayName.Length > LocalUserRules.MaxDisplayNameLength)
            displayName = displayName[..LocalUserRules.MaxDisplayNameLength];

        await InsertAsync(connection, username, displayName, null, password, ct);
        logger.LogInformation(
            "Bootstrap account '{Username}' created. Remove Auth__BootstrapUsername and Auth__BootstrapPassword from the environment.",
            username);
    }

    private static async Task<int> InsertAsync(
        SqliteConnection connection,
        string username,
        string displayName,
        string? email,
        string password,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (username, display_name, email, password_hash, security_stamp, is_enabled, created_utc, updated_utc)
            VALUES ($username, $displayName, $email, $passwordHash, $stamp, 1, $created, $created)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$email", (object?)email ?? DBNull.Value);
        command.Parameters.AddWithValue("$passwordHash", LocalPasswordHasher.Hash(password));
        command.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$created", Now());
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await ScalarIntAsync(command, ct);
    }

    private static async Task<int> ScalarIntAsync(SqliteCommand command, CancellationToken ct) =>
        Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

    private static LocalUser ReadUser(SqliteDataReader reader) =>
        new(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4) != 0,
            ParseWhen(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseWhen(reader.GetString(6)));

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseWhen(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ResolvePath(string contentRoot, string path)
    {
        string combined = Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path);
        return Path.GetFullPath(combined);
    }
}
