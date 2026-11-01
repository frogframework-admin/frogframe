using System.Xml.Linq;
using Framework.Persistence.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

sealed partial class Tests
{
    [Test, PgTest]
    public Task Pg_PostgresXmlRepository_dapat_menyimpan_dan_mengambil_elemen_xml()
    {
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var repo = new PostgresXmlRepository(db);

        var keyXml = XElement.Parse("<key id=\"test-key-1\"><creationDate>2026-11-02T00:00:00Z</creationDate></key>");
        repo.StoreElement(keyXml, "test-key-1");

        var all = repo.GetAllElements();
        Assert.True(all.Any(e => e.Attribute("id")?.Value == "test-key-1"), "key berhasil disimpan dan dibaca kembali");

        // Upsert on conflict
        var updatedXml = XElement.Parse("<key id=\"test-key-1\"><creationDate>2026-11-02T01:00:00Z</creationDate></key>");
        repo.StoreElement(updatedXml, "test-key-1");

        var allUpdated = repo.GetAllElements().Where(e => e.Attribute("id")?.Value == "test-key-1").ToList();
        Assert.Equal(1, allUpdated.Count, "tidak ada duplikasi key dengan friendlyName yang sama");
        Assert.Equal("2026-11-02T01:00:00Z", allUpdated[0].Element("creationDate")?.Value, "isi XML berhasil diperbarui");

        return Task.CompletedTask;
    }

    [Test, PgTest]
    public Task Pg_DataProtection_enkripsi_dan_dekripsi_lintas_provider()
    {
        var db = sp.GetRequiredService<NpgsqlDataSource>();

        // Instance 1: menulis key dan mengenkripsi payload
        var services1 = new ServiceCollection();
        services1.AddDataProtection()
            .SetApplicationName("DocFrame");
        services1.AddSingleton<IXmlRepository>(new PostgresXmlRepository(db));
        using var sp1 = services1.BuildServiceProvider();

        var protector1 = sp1.GetRequiredService<IDataProtectionProvider>().CreateProtector("SessionCookie");
        const string plaintext = "user:andi:tenant:acme:stamp:abc123xyz";
        var ciphertext = protector1.Protect(plaintext);

        Assert.True(!string.IsNullOrEmpty(ciphertext), "ciphertext berhasil dibuat");
        Assert.True(ciphertext != plaintext, "ciphertext terenkripsi");

        // Instance 2: membaca key yang sama dari PostgreSQL dan mendekripsi
        var services2 = new ServiceCollection();
        services2.AddDataProtection()
            .SetApplicationName("DocFrame");
        services2.AddSingleton<IXmlRepository>(new PostgresXmlRepository(db));
        using var sp2 = services2.BuildServiceProvider();

        var protector2 = sp2.GetRequiredService<IDataProtectionProvider>().CreateProtector("SessionCookie");
        var decrypted = protector2.Unprotect(ciphertext);

        Assert.Equal(plaintext, decrypted, "instance 2 berhasil mendekripsi payload memakai key dari PostgreSQL");

        return Task.CompletedTask;
    }
}

