namespace Framework.Hosting;

/// <summary>
/// Demo mode settings in one place (seeding demo users and sample data, listing demo accounts on the login page).
/// Development always runs in demo mode. Outside Development demo mode is off unless configured, and then only
/// starts when explicitly allowed (see <see cref="DeploymentChecks"/>).
/// </summary>
public static class DemoMode
{
    /// <summary>Password of the demo accounts when none is configured. Public (it is in the repository).</summary>
    public const string PublicPassword = "FrogFrame#2026";

    public sealed record Settings(bool SeedData, bool ShowAccounts, string Password, bool AllowOutsideDevelopment);

    /// <param name="isDevelopment">ASPNETCORE_ENVIRONMENT is Development.</param>
    /// <param name="config">Configuration lookup by key (e.g. <c>k =&gt; configuration[k]</c>).</param>
    public static Settings Resolve(bool isDevelopment, Func<string, string?> config)
    {
        var password = config("Demo:Password") is { Length: > 0 } p && !string.IsNullOrWhiteSpace(p) ? p : PublicPassword;
        return new Settings(
            SeedData: isDevelopment || Flag(config("Demo:SeedData")),
            ShowAccounts: isDevelopment || Flag(config("Demo:ShowAccounts")),
            Password: password,
            AllowOutsideDevelopment: Flag(config("Demo:AllowOutsideDevelopment")));
    }

    internal static bool Flag(string? value) => bool.TryParse(value?.Trim(), out var b) && b;
}

/// <summary>
/// Configuration checks run once at startup, before anything is seeded. Outside Development a failed check stops the
/// application with a message that names the setting to change, instead of starting in an unsafe state.
/// </summary>
public static class DeploymentChecks
{
    /// <summary>Minimum length of the demo password on a demo server reachable outside Development.</summary>
    public const int MinDemoPasswordLength = 12;

    /// <summary>Minimum length of a password or secret key outside Development.</summary>
    public const int MinSecretLength = 12;

    /// <summary>Values that ship in the repository or are commonly guessed; never accepted outside Development.</summary>
    private static readonly HashSet<string> KnownWeak = new(StringComparer.OrdinalIgnoreCase)
    {
        DemoMode.PublicPassword, "frogframe", "postgres", "password", "admin", "secret", "changeme", "change-me",
        "ganti-password-ini", "123456", "12345678", "123456789012", "password1234", "admin1234567",
    };

    /// <summary>Problems with the configuration; empty when the application may start.</summary>
    public static IReadOnlyList<string> Validate(bool isDevelopment, Func<string, string?> config)
    {
        var errors = new List<string>();
        if (isDevelopment) return errors;
        ValidateSecrets(config, errors);

        var demo = DemoMode.Resolve(isDevelopment, config);
        if (demo.SeedData || demo.ShowAccounts)
        {
            if (!demo.AllowOutsideDevelopment)
                errors.Add(
                    "Demo mode is on outside Development (Demo:SeedData or Demo:ShowAccounts is true). Demo seeding creates accounts " +
                    "such as 'admin' (System Manager) with a known password. Set FROGFRAME_DEMO_SEED_DATA=false and " +
                    "FROGFRAME_DEMO_SHOW_ACCOUNTS=false for production. For a dedicated demo server only, set " +
                    "Demo:AllowOutsideDevelopment=true together with your own Demo:Password.");
            else if (demo.Password == DemoMode.PublicPassword || demo.Password.Length < MinDemoPasswordLength)
                errors.Add(
                    $"Demo mode is allowed outside Development, but Demo:Password is the public default or shorter than " +
                    $"{MinDemoPasswordLength} characters. Set FROGFRAME_DEMO_PASSWORD to your own long password.");
        }
        return errors;
    }

    /// <summary>
    /// Secrets outside Development: database passwords, the initial admin password, object-storage and AI keys must be
    /// set to real values: not a "change-me" placeholder from .env.example, not a value from the repository or a
    /// common default, and passwords at least <see cref="MinSecretLength"/> characters long. Settings that are not used
    /// (e.g. no S3 storage, Bootstrap:AdminPassword cleared after the first login) are not checked.
    /// </summary>
    private static void ValidateSecrets(Func<string, string?> config, List<string> errors)
    {
        if (string.Equals(config("Persistence:Provider"), "PostgreSql", StringComparison.OrdinalIgnoreCase))
            CheckSecret(ConnectionPassword(config("ConnectionStrings:FrogFrame")),
                "The database password in ConnectionStrings:FrogFrame", "POSTGRES_PASSWORD", errors);
        CheckSecret(ConnectionPassword(config("ConnectionStrings:FrogFrameReadOnly")),
            "The read-only database password in ConnectionStrings:FrogFrameReadOnly", "FROGFRAME_RO_PASSWORD", errors);
        CheckSecret(config("Bootstrap:AdminPassword"),
            "Bootstrap:AdminPassword (initial admin)", "FROGFRAME_ADMIN_PASSWORD", errors);
        if (string.Equals(config("Storage:Provider"), "S3", StringComparison.OrdinalIgnoreCase))
        {
            CheckSecret(config("Storage:S3:AccessKey"), "Storage:S3:AccessKey", "FROGFRAME_S3_ACCESS_KEY", errors, minLength: 0);
            CheckSecret(config("Storage:S3:SecretKey"), "Storage:S3:SecretKey", "FROGFRAME_S3_SECRET_KEY", errors);
        }
        if (DemoMode.Flag(config("AI:Enabled")) && string.Equals(config("AI:Auth"), "ApiKey", StringComparison.OrdinalIgnoreCase))
            CheckSecret(config("AI:ApiKey"), "AI:ApiKey", "FROGFRAME_AI_API_KEY", errors, minLength: 0);
    }

    /// <summary>Adds an error when a configured secret is a placeholder, a known weak value or too short. Empty = not checked.</summary>
    private static void CheckSecret(string? value, string what, string envVar, List<string> errors, int minLength = MinSecretLength)
    {
        if (string.IsNullOrEmpty(value)) return;
        var reason =
            value.StartsWith("change-me", StringComparison.OrdinalIgnoreCase) ? "is still the placeholder from .env.example" :
            KnownWeak.Contains(value.Trim()) ? "is a default value that is in the repository or easy to guess" :
            value.Length < minLength ? $"is shorter than {minLength} characters" : null;
        if (reason is not null)
            errors.Add($"{what} {reason}. Set {envVar} to a long random value (e.g. openssl rand -base64 24); " +
                       "see docs/guides/secrets.md.");
    }

    /// <summary>Password (or Pwd) of a PostgreSQL connection string, quoted values included; null when there is none.</summary>
    public static string? ConnectionPassword(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        try
        {
            var b = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
            return (b.TryGetValue("Password", out var p) || b.TryGetValue("Pwd", out p)) ? p?.ToString() : null;
        }
        catch (ArgumentException) { return null; } // malformed: Npgsql reports it when connecting
    }

    /// <summary>Throws with every problem listed when <see cref="Validate"/> finds any.</summary>
    public static void ThrowIfInvalid(bool isDevelopment, Func<string, string?> config)
    {
        var errors = Validate(isDevelopment, config);
        if (errors.Count > 0)
            throw new InvalidOperationException("FrogFrame will not start with this configuration:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
    }
}
