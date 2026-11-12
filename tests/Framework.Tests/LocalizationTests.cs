using System.Globalization;
using Framework.Localization;
using Framework.Metadata;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Multi-language UI (Indonesian/English): dictionaries, tenant translations, message templates.</summary>
sealed partial class Tests
{
    private static TranslationCatalog TestCatalog(ITranslationStore? store = null) => new(
    [
        new TranslationFile { Language = "id", Translations = new() { ["Save draft"] = "Simpan draf", ["{0} is required."] = "{0} wajib diisi." } },
        new TranslationFile { Language = "en", Translations = new() { ["Prioritas"] = "Priority", ["{0} wajib diisi"] = "{0} is required", ["{0} maksimal {1}"] = "{0} must be at most {1}", ["{0} maksimal {1} karakter"] = "{0} must be at most {1} characters" } },
        new TranslationFile { Language = "en", Translations = new() { ["Prioritas"] = "Urgency" } }, // application file wins over framework file
    ], store ?? new InMemoryTranslationStore());

    [Test]
    public async Task Localization_catalog_translates_texts_templates_and_tenant_overrides()
    {
        var catalog = TestCatalog();
        Assert.Equal("Simpan draf", catalog.Text("acme", "id", "Save draft"), "framework dictionary");
        Assert.Equal("Save draft", catalog.Text("acme", "en", "Save draft"), "no translation → text as written");
        Assert.Equal("Urgency", catalog.Text("acme", "en", "Prioritas"), "later file wins");
        Assert.Equal("Prioritas wajib diisi", catalog.Text("acme", "en", "Prioritas wajib diisi"), "Text does not use templates");

        Assert.Equal("Urgency is required", catalog.Message("acme", "en", "Prioritas wajib diisi"), "template + translated label");
        Assert.Equal("Nama must be at most 140 characters", catalog.Message("acme", "en", "Nama maksimal 140 karakter"), "most specific template first");
        Assert.Equal("Qty must be at most 5", catalog.Message("acme", "en", "Qty maksimal 5"), "shorter template");
        Assert.Equal("Something else", catalog.Message("acme", "en", "Something else"), "no template → unchanged");

        // Tenant translations win and are isolated per tenant.
        var before = catalog.Version;
        await catalog.SaveAsync("acme", "en", "Prioritas", "Importance", "admin");
        Assert.True(catalog.Version != before, "version changes");
        Assert.Equal("Importance", catalog.Text("acme", "en", "Prioritas"), "tenant override");
        Assert.Equal("Urgency", catalog.Text("other", "en", "Prioritas"), "other tenant unaffected");
        Assert.Equal("Importance is required", catalog.Message("acme", "en", "Prioritas wajib diisi"), "override used inside templates");

        await catalog.SaveAsync("acme", "en", "{0} wajib diisi", "Please fill in {0}", "admin");
        Assert.Equal("Please fill in Importance", catalog.Message("acme", "en", "Prioritas wajib diisi"), "tenant template wins");

        await catalog.DeleteAsync("acme", "en", "Prioritas");
        Assert.Equal("Urgency", catalog.Text("acme", "en", "Prioritas"), "delete restores shipped translation");

        await Assert.Throws<ArgumentException>(() => catalog.SaveAsync("acme", "en", "{0} wajib diisi", "is required", "admin"), "placeholders must match");
        await Assert.Throws<ArgumentException>(() => catalog.SaveAsync("acme", "fr", "Ya", "Oui", "admin"), "unsupported language");
    }

    [Test]
    public Task Localization_languages_and_files()
    {
        Assert.Equal("en", Languages.Normalize("en-US"), "en-US");
        Assert.Equal("id", Languages.Normalize("ID"), "ID");
        Assert.True(Languages.Normalize("fr-FR") is null, "unsupported");
        Assert.Equal("en", Languages.FromCulture(CultureInfo.GetCultureInfo("en-US")), "culture");

        var file = TranslationFile.Parse("""{ "language": "en-GB", "translations": { "Pemohon": "Requester" } }""");
        Assert.Equal("en", file.Language, "language normalized");
        Assert.Equal("Requester", TranslationFile.Parse(file.ToJson()).Translations["Pemohon"], "round trip");
        Assert.Throws<System.Text.Json.JsonException>(() => TranslationFile.Parse("""{ "language": "fr", "translations": {} }"""), "unsupported language rejected");

        // Framework dictionaries are embedded and valid.
        var shipped = LocalizationSetup.FrameworkTranslations().ToList();
        Assert.True(shipped.Any(f => f.Language == "id" && f.Translations["Save draft"] == "Simpan draf"), "framework id dictionary");
        Assert.True(shipped.Any(f => f.Language == "en" && f.Translations.ContainsKey("{0} wajib diisi")), "framework en dictionary");
        foreach (var f in shipped)
            foreach (var (source, target) in f.Translations)
                Assert.Equal(TranslationCatalog.Placeholders(source), TranslationCatalog.Placeholders(target), $"placeholders of '{source}' ({f.Language})");
        return Task.CompletedTask;
    }

    [Test]
    public Task Localization_sample_metadata_is_fully_translated_to_english()
    {
        var catalog = sp.GetRequiredService<TranslationCatalog>();
        var texts = MetadataTexts.Collect(sp.GetRequiredService<IMetadataProvider>().Current);
        Assert.True(texts.Any(t => t.Text == "Pemohon"), "labels collected");
        Assert.True(texts.Any(t => t.Text == "Tinggi" && t.Where.Contains("option")), "options collected");
        Assert.True(texts.Any(t => t.Text == "Setujui" && t.Where.Contains("action")), "workflow actions collected");

        var missing = texts.Where(t => !catalog.TryExact("acme", "en", t.Text, out _)).Select(t => t.Text).ToList();
        Assert.Equal(0, missing.Count, "untranslated sample texts: " + string.Join(" | ", missing));
        Assert.Equal("Purchase Request", catalog.Text("acme", "en", "Pengajuan Pembelian"), "DocType title");
        Assert.Equal("Justification is required", catalog.Message("acme", "en", "Justifikasi wajib diisi"), "validation message");
        Assert.Equal("Simpan draf", catalog.Text("acme", "id", "Save draft"), "UI text in Indonesian");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Localization_ui_text_translates_field_copies_without_touching_metadata()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Set(U("andi"));
            var t = scope.ServiceProvider.GetRequiredService<UiText>();
            Assert.Equal("en", t.Language, "language from UI culture");

            var meta = sp.GetRequiredService<IMetadataProvider>().Current.Get("PurchaseRequest");
            var priority = meta.ByName["priority"];
            var copy = t.Field(priority);
            Assert.Equal("Priority", copy.Label, "label translated");
            Assert.Equal("Prioritas", priority.Label, "shared metadata unchanged");
            Assert.Equal("priority", copy.Name, "name unchanged");
            Assert.Equal(string.Join(",", priority.Def.Options!), string.Join(",", copy.Def.Options!), "stored option values unchanged");
            Assert.True(ReferenceEquals(copy, t.Field(priority)), "copy cached");
            Assert.Equal("High", t.Option("Tinggi"), "option display text");
            Assert.Equal("Purchase Request", t.Title(meta), "title");

            var section = meta.Layout.SelectMany(tab => tab.Sections).First(s => s.Title == "Kebutuhan");
            var translated = t.Section(section);
            Assert.Equal("Requirement", translated.Title, "section title");
            Assert.True(translated.Columns.SelectMany(c => c.Fields).Any(f => f.Name == "priority" && f.Label == "Priority"), "section fields translated");

            // A tenant translation replaces the cached copy.
            var catalog = sp.GetRequiredService<TranslationCatalog>();
            await catalog.SaveAsync("acme", "en", "Prioritas", "Urgency", "admin");
            Assert.Equal("Urgency", t.Field(priority).Label, "cache refreshed after a tenant translation");
            await catalog.DeleteAsync("acme", "en", "Prioritas");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
