using Framework.Hosting;

/// <summary>
/// P0-1: demo mode outside Development. Demo seeding creates a System Manager with a shared, public password, so the
/// application must refuse to start with demo mode on outside Development unless a demo server opts in explicitly
/// with its own password. Development keeps working as before.
/// </summary>
sealed partial class Tests
{
    private static Func<string, string?> Config(params (string Key, string? Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);
        return k => map.GetValueOrDefault(k);
    }

    [Test]
    public Task DemoMode_is_resolved_from_environment_and_configuration()
    {
        var dev = DemoMode.Resolve(isDevelopment: true, Config());
        Assert.True(dev is { SeedData: true, ShowAccounts: true, Password: DemoMode.PublicPassword }, "Development is always demo mode");
        var composeDefault = DemoMode.Resolve(true, Config(("Demo:SeedData", "false"), ("Demo:ShowAccounts", "false"), ("Demo:Password", "")));
        Assert.True(composeDefault is { SeedData: true, ShowAccounts: true, Password: DemoMode.PublicPassword },
            "docker-compose default (Development, FROGFRAME_DEMO_*=false, empty password) is still demo mode");
        Assert.Equal(0, DeploymentChecks.Validate(false, Config(("Demo:SeedData", "false"), ("Demo:ShowAccounts", "false"), ("Demo:Password", ""))).Count,
            "going live with only ASPNETCORE_ENVIRONMENT=Production passes the checks");

        var prod = DemoMode.Resolve(isDevelopment: false, Config(("Demo:Password", "")));
        Assert.True(prod is { SeedData: false, ShowAccounts: false }, "off by default outside Development");
        Assert.Equal(DemoMode.PublicPassword, prod.Password, "empty password (appsettings.json) falls back instead of showing an empty one");

        var on = DemoMode.Resolve(false, Config(("Demo:SeedData", "True"), ("Demo:ShowAccounts", " true "), ("Demo:Password", "Trial-Server-2026!"), ("Demo:AllowOutsideDevelopment", "true")));
        Assert.True(on is { SeedData: true, ShowAccounts: true, AllowOutsideDevelopment: true, Password: "Trial-Server-2026!" }, "configured values (case and spaces tolerated)");
        Assert.True(!DemoMode.Resolve(false, Config(("Demo:SeedData", "yes"))).SeedData, "only true/false are understood");
        return Task.CompletedTask;
    }

    [Test]
    public Task DeploymentChecks_block_demo_mode_outside_development()
    {
        Assert.Equal(0, DeploymentChecks.Validate(true, Config(("Demo:SeedData", "true"))).Count, "Development: demo allowed");
        Assert.Equal(0, DeploymentChecks.Validate(false, Config(("Demo:SeedData", "false"), ("Demo:ShowAccounts", "false"))).Count, "production baseline passes");

        var seed = DeploymentChecks.Validate(false, Config(("Demo:SeedData", "true")));
        Assert.True(seed.Count == 1 && seed[0].Contains("FROGFRAME_DEMO_SEED_DATA=false") && seed[0].Contains("Demo:AllowOutsideDevelopment"), "seeding blocked: " + string.Join(" ", seed));
        Assert.Equal(1, DeploymentChecks.Validate(false, Config(("Demo:ShowAccounts", "true"))).Count, "showing accounts blocked too");

        // A dedicated demo server opts in, but only with its own long password.
        var optInDefault = DeploymentChecks.Validate(false, Config(("Demo:SeedData", "true"), ("Demo:AllowOutsideDevelopment", "true")));
        Assert.True(optInDefault.Count == 1 && optInDefault[0].Contains("FROGFRAME_DEMO_PASSWORD"), "public default password refused");
        Assert.Equal(1, DeploymentChecks.Validate(false, Config(("Demo:SeedData", "true"), ("Demo:AllowOutsideDevelopment", "true"), ("Demo:Password", "short"))).Count, "short password refused");
        Assert.Equal(1, DeploymentChecks.Validate(false, Config(("Demo:SeedData", "true"), ("Demo:AllowOutsideDevelopment", "true"), ("Demo:Password", DemoMode.PublicPassword))).Count, "default typed explicitly refused");
        Assert.Equal(0, DeploymentChecks.Validate(false, Config(("Demo:SeedData", "true"), ("Demo:AllowOutsideDevelopment", "true"), ("Demo:Password", "Trial-Server-2026!"))).Count, "opted-in demo server passes");

        var ex = Assert.Throws<InvalidOperationException>(() => DeploymentChecks.ThrowIfInvalid(false, Config(("Demo:SeedData", "true"))), "startup throws");
        Assert.True(ex.Message.StartsWith("FrogFrame will not start with this configuration:") && ex.Message.Contains("Demo mode is on"), ex.Message);
        DeploymentChecks.ThrowIfInvalid(true, Config(("Demo:SeedData", "true"))); // does not throw in Development
        return Task.CompletedTask;
    }
}
