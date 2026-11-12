using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Gap #3 field types: normalization, compiler rules, and document behaviour (password, dynamic link, multi-select, layout).</summary>
sealed partial class Tests
{
    private static object? Norm(IFieldType t, object? raw)
    {
        var r = t.Normalize(raw);
        if (!r.Ok) throw new AssertException($"{t.Name}: '{raw}' rejected: {r.Error}");
        return r.Value;
    }

    private static void Rejects(IFieldType t, object? raw) =>
        Assert.True(!t.Normalize(raw).Ok, $"{t.Name} rejects '{raw}'");

    [Test]
    public Task FieldTypes_are_registered()
    {
        var registry = sp.GetRequiredService<IFieldTypeRegistry>();
        foreach (var name in new[] { "Datetime", "Time", "Duration", "Percent", "Phone", "TextEditor", "Markdown", "Attach", "Password",
                     "DynamicLink", "TableMultiSelect", "Rating", "Color", "JSON", "Signature", "Barcode", "Geolocation", "ReadOnly",
                     "HTML", "Button", "Heading" })
            Assert.True(registry.TryGet(name, out _), $"type {name} registered");
        return Task.CompletedTask;
    }

    [Test]
    public Task FieldTypes_normalize_dates_times_and_numbers()
    {
        var dt = new DatetimeFieldType();
        Assert.Equal("2026-03-01T03:00:00Z", Norm(dt, "2026-03-01T03:00:00Z"), "UTC kept");
        Assert.Equal("2026-03-01T03:00:00Z", Norm(dt, "2026-03-01T10:00:00+07:00"), "offset converted");
        Assert.Equal("2026-03-01T03:00:00Z", Norm(dt, "2026-03-01 10:00"), "local time read as Asia/Jakarta (UTC+7)");
        Rejects(dt, "01/03/2026");

        var time = new TimeFieldType();
        Assert.Equal("08:30:00", Norm(time, "8:30"), "short time");
        Rejects(time, "25:00");

        var duration = new DurationFieldType();
        Assert.Equal(5400L, Norm(duration, "1h 30m"), "units");
        Assert.Equal(5400L, Norm(duration, "01:30:00"), "clock");
        Assert.Equal(90L, Norm(duration, 90m), "seconds");
        Assert.Equal("1h 30m", DurationFieldType.Display(5400), "display");
        Rejects(duration, "-5");

        var rating = new RatingFieldType();
        Assert.Equal(3.5m, Norm(rating, 3.4m), "half steps");
        Rejects(rating, 11m);

        var registry = sp.GetRequiredService<IFieldTypeRegistry>();
        Assert.True(registry.TryGet("Percent", out var percent) && percent.Editor == "percent", "percent editor");
        Assert.Equal(12.35m, Norm(percent, "12.346"), "percent rounded to 2 decimals");
        return Task.CompletedTask;
    }

    [Test]
    public Task FieldTypes_normalize_text_like_values()
    {
        var phone = new PhoneFieldType();
        Assert.Equal("+6281234567890", Norm(phone, "0812-3456-7890"), "leading 0 → +62");
        Assert.Equal("+6281234567890", Norm(phone, "+62 812 3456 7890"), "spaces removed");
        Assert.Equal("+6281234567890", Norm(phone, "6281234567890"), "country code without +");
        Rejects(phone, "12ab");

        var color = new ColorFieldType();
        Assert.Equal("#aabbcc", Norm(color, "#ABC"), "short hex expanded");
        Rejects(color, "red");

        var json = new JsonFieldType();
        Assert.Equal("{\"a\":1}", Norm(json, "{ \"a\" : 1 }"), "compact JSON");
        Rejects(json, "{a:1}");

        var geo = new GeolocationFieldType();
        Assert.Equal("-6.2,106.816666", Norm(geo, "-6.2, 106.816666"), "lat,lng");
        Rejects(geo, "100,0");

        var barcode = new BarcodeFieldType();
        Assert.Equal("ABC-123", Norm(barcode, " ABC-123 "), "trimmed");
        Rejects(barcode, "naïve");

        var multi = new TableMultiSelectFieldType();
        var items = (List<string>)Norm(multi, "VND-0001, VND-0002,VND-0001")!;
        Assert.Equal(2, items.Count, "split, trimmed, distinct");
        return Task.CompletedTask;
    }

    [Test]
    public Task FieldTypes_sanitize_rich_text_and_check_signatures()
    {
        var html = (string)Norm(new TextEditorFieldType(), "<p onclick=\"x()\">Hi<script>alert(1)</script></p>")!;
        Assert.True(!html.Contains("script") && !html.Contains("onclick") && html.Contains("Hi"), "script and handlers removed: " + html);

        var signature = new SignatureFieldType();
        Assert.True(signature.Normalize("<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0L10 10\"/></svg>").Ok, "plain SVG accepted");
        Rejects(signature, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        Rejects(signature, "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>");
        Rejects(signature, "<svg><image href=\"https://evil.example/x.png\"/></svg>");
        Rejects(signature, "<p>not svg</p>");
        return Task.CompletedTask;
    }

    [Test]
    public Task FieldTypes_password_is_encrypted_and_revealed_only_on_server()
    {
        var pw = new PasswordFieldType(new InMemorySecretProtector());
        var stored = (string)Norm(pw, "s3cret!")!;
        Assert.True(stored.StartsWith(PasswordFieldType.Prefix) && !stored.Contains("s3cret!"), "stored encrypted");
        Assert.Equal("s3cret!", pw.Reveal(stored), "server can decrypt");
        Assert.Equal(stored, Norm(pw, stored), "already-encrypted value kept");
        Rejects(pw, PasswordFieldType.Mask);
        Rejects(pw, PasswordFieldType.Prefix + "sec:dp:%%not-base64%%");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------- compiler

    private (bool Valid, string Errors) PreviewDraft(DocTypeDef def)
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var preview = reg.Preview(def, sp.GetRequiredService<RoleCatalog>().Scope("acme"));
        return (preview.Valid, string.Join(" | ", preview.Errors));
    }

    private static DocTypeDef FieldTestDocType(string name, params FieldDef[] extra) => new()
    {
        Name = name, Title = name, Module = "HR", Version = 1,
        Naming = new NamingDef { Pattern = "FT-{YYYY}-{#####}" },
        Fields = [new FieldDef { Name = "title", Type = "Data", Label = "Title", RequiredWhen = "true" }, .. extra],
        Permissions = [new PermissionDef { Role = UserContext.SystemManager, Actions = ["read", "create", "write", "delete"], Scope = "all" }],
        ListView = new ListViewDef { TitleField = "title", SearchFields = ["name", "title"] },
    };

    [Test]
    public Task FieldTypes_compiler_rules()
    {
        var ok = FieldTestDocType("FtAllTypes",
            new FieldDef { Name = "starts_at", Type = "Datetime", Label = "Starts" },
            new FieldDef { Name = "ref_type", Type = "Select", Label = "Ref type", Options = ["Vendor"] },
            new FieldDef { Name = "ref_name", Type = "DynamicLink", Label = "Ref", LinkToField = "ref_type" },
            new FieldDef { Name = "vendors", Type = "TableMultiSelect", Label = "Vendors", LinkTo = "Vendor" },
            new FieldDef { Name = "notes", Type = "TextEditor", Label = "Notes" },
            new FieldDef { Name = "secret", Type = "Password", Label = "Secret" },
            new FieldDef { Name = "intro", Type = "HTML", Label = "Intro", Content = "<p>Hello</p>" },
            new FieldDef { Name = "go", Type = "Button", Label = "Go", ButtonAction = "do_it" },
            new FieldDef { Name = "stars", Type = "Rating", Label = "Stars", Max = 5 });
        var (valid, errors) = PreviewDraft(ok);
        Assert.True(valid, "all new types compile: " + errors);

        var badDynamic = FieldTestDocType("FtBadDynamic",
            new FieldDef { Name = "ref_type", Type = "Select", Label = "Ref type", Options = ["NotADocType"] },
            new FieldDef { Name = "ref_name", Type = "DynamicLink", Label = "Ref", LinkToField = "ref_type" });
        Assert.True(!PreviewDraft(badDynamic).Valid, "DynamicLink options must be DocTypes");

        var buttonWithoutAction = FieldTestDocType("FtBadButton", new FieldDef { Name = "go", Type = "Button", Label = "Go" });
        Assert.True(!PreviewDraft(buttonWithoutAction).Valid, "Button needs buttonAction");

        var passwordInList = FieldTestDocType("FtBadList", new FieldDef { Name = "secret", Type = "Password", Label = "Secret", InListView = true });
        Assert.True(!PreviewDraft(passwordInList).Valid, "Password cannot be a list column");

        var computedHeading = FieldTestDocType("FtBadHeading", new FieldDef { Name = "h", Type = "Heading", Label = "H", Compute = "title" });
        Assert.True(!PreviewDraft(computedHeading).Valid, "layout fields cannot be computed");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------- documents

    private async Task<UserContext> PublishFieldTestDocTypeAsync()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var def = FieldTestDocType("FtDoc",
            new FieldDef { Name = "secret", Type = "Password", Label = "Secret" },
            new FieldDef { Name = "ref_type", Type = "Select", Label = "Ref type", Options = ["Vendor"] },
            new FieldDef { Name = "ref_name", Type = "DynamicLink", Label = "Ref", LinkToField = "ref_type" },
            new FieldDef { Name = "vendors", Type = "TableMultiSelect", Label = "Vendors", LinkTo = "Vendor" },
            new FieldDef { Name = "section_title", Type = "Heading", Label = "More" });
        reg.Publish(def, "admin", acknowledgeRisk: false, scope: sp.GetRequiredService<RoleCatalog>().Scope("acme"));
        await Task.CompletedTask;
        return U("admin");
    }

    [Test]
    public async Task FieldTypes_password_never_leaves_the_server()
    {
        var admin = await PublishFieldTestDocTypeAsync();
        var created = await Docs.InsertAsync(admin, "FtDoc", P(("title", "Mailbox"), ("secret", "s3cret!")));
        Assert.Equal(PasswordFieldType.Mask, created.Data["secret"], "insert result is masked");

        var stored = await sp.GetRequiredService<IDocumentStore>().GetAsync(admin.TenantId, "FtDoc", created.Name);
        var raw = stored!.Data["secret"] as string;
        Assert.True(raw is not null && raw.StartsWith(PasswordFieldType.Prefix) && !raw.Contains("s3cret!"), "stored encrypted");
        var type = (PasswordFieldType)(sp.GetRequiredService<IFieldTypeRegistry>().TryGet("Password", out var t) ? t : null)!;
        Assert.Equal("s3cret!", type.Reveal(raw), "server-side reveal");

        // Saving the form with the mask (or empty) keeps the stored secret.
        var read = await Docs.GetAsync(admin, "FtDoc", created.Name);
        Assert.Equal(PasswordFieldType.Mask, read.Data["secret"], "read is masked");
        var updated = await Docs.UpdateAsync(admin, "FtDoc", created.Name, read.Version, P(("title", "Mailbox 2"), ("secret", PasswordFieldType.Mask)));
        var after = await sp.GetRequiredService<IDocumentStore>().GetAsync(admin.TenantId, "FtDoc", updated.Name);
        Assert.Equal(raw, after!.Data["secret"], "mask keeps the stored value");
    }

    [Test]
    public async Task FieldTypes_dynamic_link_and_multiselect_are_checked()
    {
        var admin = await PublishFieldTestDocTypeAsync();

        var ok = await Docs.InsertAsync(admin, "FtDoc", P(("title", "Linked"), ("ref_type", "Vendor"), ("ref_name", "VND-0001"),
            ("vendors", new List<string> { "VND-0001" })));
        Assert.Equal("VND-0001", ok.Data["ref_name"], "dynamic link stored");
        Assert.Equal(1, ((List<string>)ok.Data["vendors"]!).Count, "multi-select stored as list");

        await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(admin, "FtDoc",
            P(("title", "Bad link"), ("ref_type", "Vendor"), ("ref_name", "VND-NOPE"))), "unknown dynamic link target document");
        await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(admin, "FtDoc",
            P(("title", "Bad multi"), ("vendors", new List<string> { "VND-NOPE" }))), "unknown multi-select item");
        await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(admin, "FtDoc",
            P(("title", "No type"), ("ref_name", "VND-0001"))), "dynamic link without its DocType");
    }

    [Test]
    public async Task FieldTypes_layout_fields_hold_no_value()
    {
        var admin = await PublishFieldTestDocTypeAsync();
        var doc = await Docs.InsertAsync(admin, "FtDoc", P(("title", "Layout"), ("section_title", "ignored")));
        Assert.True(!doc.Data.ContainsKey("section_title"), "heading value not returned");
        var stored = await sp.GetRequiredService<IDocumentStore>().GetAsync(admin.TenantId, "FtDoc", doc.Name);
        Assert.True(stored!.Data.GetValueOrDefault("section_title") is null, "heading value not stored");
    }
}
