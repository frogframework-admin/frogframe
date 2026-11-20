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

    /// <summary>Problems with the configuration; empty when the application may start.</summary>
    public static IReadOnlyList<string> Validate(bool isDevelopment, Func<string, string?> config)
    {
        var errors = new List<string>();
        if (isDevelopment) return errors;

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

    /// <summary>Throws with every problem listed when <see cref="Validate"/> finds any.</summary>
    public static void ThrowIfInvalid(bool isDevelopment, Func<string, string?> config)
    {
        var errors = Validate(isDevelopment, config);
        if (errors.Count > 0)
            throw new InvalidOperationException("FrogFrame will not start with this configuration:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
    }
}
