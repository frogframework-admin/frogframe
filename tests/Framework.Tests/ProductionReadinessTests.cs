using Framework.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

sealed partial class Tests
{
    private sealed class StubHostEnvironment(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "FrogFrame.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static void ValidateProductionSafety(string envName, Dictionary<string, string?> values)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(envName));
        services.AddFrogFrameHttpHardening(cfg);
        using var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<IOptions<ProductionSafetySettings>>().Value;
    }

    [Test]
    public Task ProductionSafety_rejects_demo_and_placeholders_outside_development()
    {
        var validProd = new Dictionary<string, string?>
        {
            ["App:PublicBaseUrl"] = "https://erp.example.com",
            ["AllowedHosts"] = "erp.example.com",
            ["Demo:SeedData"] = "false",
            ["Demo:ShowAccounts"] = "false",
            ["Bootstrap:AdminPassword"] = "StrongProdPass#99",
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:FrogFrame"] = "Host=postgres;Port=5432;Database=frogframe;Username=frogframe;Password=StrongDbPass#99"
        };
        ValidateProductionSafety(Environments.Production, validProd);

        void AssertThrows(string key, string value, string reason)
        {
            var bad = new Dictionary<string, string?>(validProd) { [key] = value };
            var threw = false;
            try { ValidateProductionSafety(Environments.Production, bad); }
            catch (OptionsValidationException) { threw = true; }
            Assert.True(threw, reason);
        }

        AssertThrows("Demo:SeedData", "true", "Production must reject Demo:SeedData=true");
        AssertThrows("Demo:ShowAccounts", "true", "Production must reject Demo:ShowAccounts=true");
        AssertThrows("Bootstrap:AdminPassword", "change-me-admin-password", "Production must reject placeholder admin password");
        AssertThrows("ConnectionStrings:FrogFrame", "Host=localhost;Database=frogframe;Username=frogframe;Password=frogframe",
            "Production must reject default frogframe DB password");
        return Task.CompletedTask;
    }

    [Test]
    public Task ProductionSafety_allows_demo_in_development()
    {
        var dev = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "*",
            ["Demo:SeedData"] = "true",
            ["Demo:ShowAccounts"] = "true",
            ["Bootstrap:AdminPassword"] = "change-me-admin-password",
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:FrogFrame"] = "Host=localhost;Database=frogframe;Username=frogframe;Password=frogframe"
        };
        ValidateProductionSafety(Environments.Development, dev);
        return Task.CompletedTask;
    }
}
