using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.Documents;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Printing;

/// <summary>Print defaults shared by the print engine, the designer and design validation.</summary>
public static class PrintDefaults
{
    /// <summary>
    /// Columns of a child table when the print format names none: the value fields in form order, without yes/no flags
    /// (rarely useful on paper) and without a "{link}_name" copy next to its Link column (the Link prints its title).
    /// Keeps wide item tables on the page; a print format can still list any columns.
    /// </summary>
    public static List<string> TableColumns(CompiledDocType child) =>
        child.Fields
            .Where(c => !c.IsLayout && c.Def.Type is not ("Check" or "Table" or "Password" or "Image" or "Attach" or "Signature"))
            .Where(c => !(c.Name.EndsWith("_name", StringComparison.Ordinal)
                          && child.ByName.TryGetValue(c.Name[..^5], out var link) && link.Def.Type == "Link"))
            .Select(c => c.Name)
            .ToList();

    /// <summary>
    /// Suggested second-line columns when a table is switched to two lines: the text columns after the first column
    /// (descriptions, names, units), so numbers stay aligned on the first line. Falls back to the last column.
    /// </summary>
    public static List<string> SecondLine(CompiledDocType child, IReadOnlyList<string> columns)
    {
        var text = columns.Skip(1).Where(c => child.ByName.TryGetValue(c, out var f) && f.Type.Editor != "number").ToList();
        return text.Count > 0 ? text : columns.Count > 1 ? [columns[^1]] : [];
    }
}

/// <summary>Company letterhead designed in /admin/print-designs. One letterhead per tenant is the default.</summary>
public sealed record LetterheadDesign
{
    public string Name { get; init; } = "";
    public bool IsDefault { get; init; }
    public string CompanyName { get; init; } = "";
    public string? Address { get; init; }
    public string? Contact { get; init; }
    public string? TaxId { get; init; }
    /// <summary>Logo as a data URI (PNG, JPEG, WebP or SVG). Printed as an image, so SVG scripts never run.</summary>
    public string? LogoDataUri { get; init; }
    /// <summary>left, center or right.</summary>
    public string Align { get; init; } = "left";
    /// <summary>Colour of the company name and the rule under the letterhead (#rrggbb).</summary>
    public string Accent { get; init; } = "#111827";
    /// <summary>Draw a line under the letterhead.</summary>
    public bool Rule { get; init; } = true;
    /// <summary>Printed at the bottom of every page (e.g. registered office, bank account).</summary>
    public string? FooterText { get; init; }
}

/// <summary>
/// Print format of a DocType designed in /admin/print-designs. <see cref="Format"/> is the same structure as the
/// DocType's metadata printFormat; the other properties control the page. A tenant can keep several formats per
/// DocType (e.g. "Standard" and "Customer copy"); one is the default.
/// </summary>
public sealed record PrintFormatDesign
{
    public string DocType { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDefault { get; init; }
    /// <summary>Letterhead name; null = the tenant's default letterhead.</summary>
    public string? Letterhead { get; init; }
    public bool ShowLetterhead { get; init; } = true;
    /// <summary>A4, A5, Letter or Legal.</summary>
    public string Paper { get; init; } = "A4";
    public bool Landscape { get; init; }
    /// <summary>Colour of the title, rules and highlights (#rrggbb).</summary>
    public string Accent { get; init; } = "#3f3d9a";
    /// <summary>Label/value pairs per row in the field blocks (1–3).</summary>
    public int FieldColumns { get; init; } = 2;
    /// <summary>Child tables left out of the print.</summary>
    public List<string> HiddenTables { get; init; } = [];
    public PrintFormatDef Format { get; init; } = new();
}

/// <summary>Stored design (letterhead or print format) as JSON.</summary>
public sealed record PrintDesignRecord(
    Guid Id, string TenantId, string Kind, string? DocType, string Name, bool IsDefault, string Json, int Version,
    DateTimeOffset ModifiedAt, string ModifiedBy);

/// <summary>Storage of print designs (table <c>frogframe_print_designs</c> in PostgreSQL).</summary>
public interface IPrintDesignStore
{
    Task<IReadOnlyList<PrintDesignRecord>> ListAsync(string tenantId, string kind, string? docType, CancellationToken ct = default);
    Task<PrintDesignRecord?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    /// <summary>Insert (<paramref name="expectedVersion"/> null) or update with optimistic concurrency.</summary>
    Task SaveAsync(PrintDesignRecord record, int? expectedVersion, CancellationToken ct = default);
    Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default);
}

public sealed class InMemoryPrintDesignStore : IPrintDesignStore
{
    private readonly ConcurrentDictionary<(string, Guid), PrintDesignRecord> items = new();
    private readonly object gate = new();

    public Task<IReadOnlyList<PrintDesignRecord>> ListAsync(string tenantId, string kind, string? docType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PrintDesignRecord>>(items.Values
            .Where(r => r.TenantId == tenantId && r.Kind == kind && (docType is null || r.DocType == docType))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList());

    public Task<PrintDesignRecord?> GetAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(items.GetValueOrDefault((tenantId, id)));

    public Task SaveAsync(PrintDesignRecord record, int? expectedVersion, CancellationToken ct = default)
    {
        lock (gate)
        {
            var current = items.GetValueOrDefault((record.TenantId, record.Id));
            if (expectedVersion is null ? current is not null : current?.Version != expectedVersion)
                throw new ConcurrencyException(record.Name, expectedVersion ?? 0, current?.Version ?? 0);
            items[(record.TenantId, record.Id)] = record;
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(items.TryRemove((tenantId, id), out _));
}

/// <summary>A stored design with its id and version (for editing with optimistic concurrency).</summary>
public sealed record Stored<T>(Guid Id, int Version, T Design, DateTimeOffset ModifiedAt, string ModifiedBy);

/// <summary>
/// Letterheads and print formats per tenant. Reading is open to everyone who can print; saving and deleting need
/// the System Manager role. Validation keeps prints safe: logos are size-limited images, colours are hex values
/// (they go into CSS), and every field, table column, total and signature state must exist in the DocType.
/// </summary>
public sealed partial class PrintDesignService(IPrintDesignStore store, IMetadataProvider metadata, TimeProvider clock)
{
    public const string LetterheadKind = "letterhead";
    public const string FormatKind = "format";
    public const int MaxLogoBytes = 300 * 1024;
    public static readonly IReadOnlySet<string> Papers = new HashSet<string>(StringComparer.Ordinal) { "A4", "A5", "Letter", "Legal" };
    public static readonly IReadOnlySet<string> Aligns = new HashSet<string>(StringComparer.Ordinal) { "left", "center", "right" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")] private static partial Regex HexColor();
    [GeneratedRegex("^data:image/(png|jpeg|webp|svg\\+xml);base64,[A-Za-z0-9+/=]+$")] private static partial Regex LogoUri();

    // ---------------- letterheads ----------------

    public async Task<IReadOnlyList<Stored<LetterheadDesign>>> LetterheadsAsync(string tenantId, CancellationToken ct = default) =>
        (await store.ListAsync(tenantId, LetterheadKind, null, ct)).Select(Read<LetterheadDesign>).ToList();

    /// <summary>The named letterhead, or the default one (null when the tenant has none).</summary>
    public async Task<LetterheadDesign?> LetterheadAsync(string tenantId, string? name, CancellationToken ct = default)
    {
        var all = await LetterheadsAsync(tenantId, ct);
        return (name is { Length: > 0 } ? all.FirstOrDefault(l => l.Design.Name == name) : null)?.Design
               ?? all.FirstOrDefault(l => l.Design.IsDefault)?.Design;
    }

    public async Task<Stored<LetterheadDesign>> SaveLetterheadAsync(UserContext user, Guid? id, LetterheadDesign design, int? expectedVersion, CancellationToken ct = default)
    {
        Demand(user);
        design = design with
        {
            Name = design.Name.Trim(), CompanyName = design.CompanyName.Trim(), Address = Blank(design.Address), Contact = Blank(design.Contact),
            TaxId = Blank(design.TaxId), FooterText = Blank(design.FooterText), LogoDataUri = Blank(design.LogoDataUri),
        };
        var errors = new List<FieldError>();
        if (design.Name.Length is 0 or > 80) errors.Add(new("name", "Name is required (at most 80 characters)."));
        if (design.CompanyName.Length is 0 or > 200) errors.Add(new("companyName", "Company name is required (at most 200 characters)."));
        if (!Aligns.Contains(design.Align)) errors.Add(new("align", "Alignment must be left, center or right."));
        if (!HexColor().IsMatch(design.Accent)) errors.Add(new("accent", "Colour must look like #1f2937."));
        if (design.LogoDataUri is { } logo)
        {
            if (!LogoUri().IsMatch(logo)) errors.Add(new("logo", "Logo must be a PNG, JPEG, WebP or SVG image."));
            else if (logo.Length - logo.IndexOf(',') > MaxLogoBytes * 4 / 3 + 4) errors.Add(new("logo", $"Logo is larger than {MaxLogoBytes / 1024} KB."));
        }
        var others = (await LetterheadsAsync(user.TenantId, ct)).Where(l => l.Id != id).ToList();
        if (others.Any(l => string.Equals(l.Design.Name, design.Name, StringComparison.OrdinalIgnoreCase)))
            errors.Add(new("name", $"A letterhead named '{design.Name}' already exists."));
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        if (others.All(l => !l.Design.IsDefault)) design = design with { IsDefault = true }; // the first letterhead is the default
        return await SaveAsync(user, id, LetterheadKind, null, design.Name, design.IsDefault, design, expectedVersion, others, ct);
    }

    // ---------------- print formats ----------------

    public async Task<IReadOnlyList<Stored<PrintFormatDesign>>> FormatsAsync(string tenantId, string docType, CancellationToken ct = default) =>
        (await store.ListAsync(tenantId, FormatKind, docType, ct)).Select(Read<PrintFormatDesign>).ToList();

    /// <summary>The named format of the DocType, or its default one (null when none is designed).</summary>
    public async Task<PrintFormatDesign?> FormatAsync(string tenantId, string docType, string? name, CancellationToken ct = default)
    {
        var all = await FormatsAsync(tenantId, docType, ct);
        return (name is { Length: > 0 } ? all.FirstOrDefault(f => f.Design.Name == name) : null)?.Design
               ?? all.FirstOrDefault(f => f.Design.IsDefault)?.Design;
    }

    /// <summary>Checks a format against the DocType (also used for the live preview of an unsaved draft).</summary>
    public IReadOnlyList<FieldError> Validate(PrintFormatDesign d)
    {
        var errors = new List<FieldError>();
        if (!metadata.Current.TryGet(d.DocType, out var meta) || meta.Def.IsChild || meta.Def.IsSingle)
        {
            errors.Add(new("docType", $"DocType '{d.DocType}' cannot be printed."));
            return errors;
        }
        if (d.Name.Trim().Length is 0 or > 80) errors.Add(new("name", "Name is required (at most 80 characters)."));
        if (!Papers.Contains(d.Paper)) errors.Add(new("paper", "Paper must be A4, A5, Letter or Legal."));
        if (d.FieldColumns is < 1 or > 3) errors.Add(new("fieldColumns", "Fields per row must be 1, 2 or 3."));
        if (!HexColor().IsMatch(d.Accent)) errors.Add(new("accent", "Colour must look like #3f3d9a."));
        bool Value(string f) => meta.ByName.TryGetValue(f, out var cf) ? !cf.IsLayout && cf.Def.Type is not ("Table" or "Password")
            : StandardFields.All.Contains(f);
        foreach (var (s, i) in (d.Format.Sections ?? []).Select((s, i) => (s, i)))
            foreach (var f in s.Fields.Where(f => !Value(f)))
                errors.Add(new($"sections[{i}]", $"Field '{f}' cannot be printed in a block."));
        var tables = meta.TableFields.ToDictionary(t => t.Name);
        foreach (var t in d.HiddenTables.Where(t => !tables.ContainsKey(t)))
            errors.Add(new("hiddenTables", $"'{t}' is not a table of {meta.Title}."));
        foreach (var (t, cols) in d.Format.TableColumns ?? [])
        {
            if (!tables.TryGetValue(t, out var tf)) { errors.Add(new("tableColumns", $"'{t}' is not a table of {meta.Title}.")); continue; }
            var child = metadata.Current.Get(tf.Def.ChildDocType!);
            foreach (var c in cols.Where(c => !child.ByName.ContainsKey(c)))
                errors.Add(new("tableColumns", $"'{c}' is not a column of {tf.Label}."));
            if (cols.Count == 0) errors.Add(new("tableColumns", $"Choose at least one column for {tf.Label}, or hide the table."));
        }
        foreach (var (t, below) in d.Format.TableSecondLine ?? [])
        {
            if (!tables.TryGetValue(t, out var tf)) { errors.Add(new("tableSecondLine", $"'{t}' is not a table of {meta.Title}.")); continue; }
            var child = metadata.Current.Get(tf.Def.ChildDocType!);
            var printed = d.Format.TableColumns?.GetValueOrDefault(t) ?? PrintDefaults.TableColumns(child);
            foreach (var c in below.Where(c => !printed.Contains(c)))
                errors.Add(new("tableSecondLine", $"'{c}' is not a printed column of {tf.Label}."));
            if (printed.Count > 0 && printed.All(below.Contains))
                errors.Add(new("tableSecondLine", $"Keep at least one column of {tf.Label} on the first line."));
        }
        foreach (var f in d.Format.TotalFields.Where(f => !(meta.ByName.TryGetValue(f, out var cf) && cf.Type.Editor == "number")))
            errors.Add(new("totalFields", $"'{f}' is not a number field."));
        if (d.Format.AmountInWordsField is { Length: > 0 } aw && !(meta.ByName.TryGetValue(aw, out var awf) && awf.Type.Editor == "number"))
            errors.Add(new("amountInWordsField", $"'{aw}' is not a number field."));
        var states = meta.Workflow?.States.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var sg in d.Format.Signatures)
        {
            if (string.IsNullOrWhiteSpace(sg.Label)) errors.Add(new("signatures", "Every signature box needs a label."));
            if (states is null) { errors.Add(new("signatures", $"{meta.Title} has no workflow: signatures come from workflow approvals.")); break; }
            foreach (var st in sg.ToStates.Append(sg.FromState).OfType<string>().Where(s => !states.Contains(s)))
                errors.Add(new("signatures", $"'{st}' is not a state of the {meta.Title} workflow."));
            if (sg.ToStates.Count == 0) errors.Add(new("signatures", $"Signature '{sg.Label}': choose the state(s) that sign it."));
        }
        return errors;
    }

    public async Task<Stored<PrintFormatDesign>> SaveFormatAsync(UserContext user, Guid? id, PrintFormatDesign design, int? expectedVersion, CancellationToken ct = default)
    {
        Demand(user);
        design = design with { Name = design.Name.Trim(), Letterhead = Blank(design.Letterhead) };
        var errors = Validate(design).ToList();
        if (design.Letterhead is { } lh && (await LetterheadsAsync(user.TenantId, ct)).All(l => l.Design.Name != lh))
            errors.Add(new("letterhead", $"Letterhead '{lh}' does not exist."));
        var others = (await FormatsAsync(user.TenantId, design.DocType, ct)).Where(f => f.Id != id).ToList();
        if (others.Any(f => string.Equals(f.Design.Name, design.Name, StringComparison.OrdinalIgnoreCase)))
            errors.Add(new("name", $"{design.DocType} already has a print format named '{design.Name}'."));
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        if (others.All(f => !f.Design.IsDefault)) design = design with { IsDefault = true };
        return await SaveAsync(user, id, FormatKind, design.DocType, design.Name, design.IsDefault, design, expectedVersion, others, ct);
    }

    /// <summary>
    /// Deletes a letterhead or format. When the default is deleted, the first remaining one (by name) becomes the
    /// default. Formats naming a deleted letterhead fall back to the default letterhead when printed.
    /// </summary>
    public async Task DeleteAsync(UserContext user, Guid id, CancellationToken ct = default)
    {
        Demand(user);
        var rec = await store.GetAsync(user.TenantId, id, ct) ?? throw new DocumentNotFoundException("Print design", id.ToString());
        await store.DeleteAsync(user.TenantId, id, ct);
        if (!rec.IsDefault) return;
        var next = (await store.ListAsync(user.TenantId, rec.Kind, rec.DocType, ct)).FirstOrDefault();
        if (next is null) return;
        await store.SaveAsync(next with { IsDefault = true, Json = Promote(next), Version = next.Version + 1, ModifiedAt = clock.GetUtcNow(), ModifiedBy = user.Id }, next.Version, ct);
    }

    private async Task<Stored<T>> SaveAsync<T>(UserContext user, Guid? id, string kind, string? docType, string name, bool isDefault, T design,
        int? expectedVersion, IReadOnlyList<Stored<T>> others, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        PrintDesignRecord record;
        if (id is null)
            record = new(Guid.NewGuid(), user.TenantId, kind, docType, name, isDefault, JsonSerializer.Serialize(design, Json), 1, now, user.Id);
        else
        {
            var current = await store.GetAsync(user.TenantId, id.Value, ct);
            if (current is null || current.Kind != kind) throw new DocumentNotFoundException("Print design", id.Value.ToString());
            record = current with { Name = name, IsDefault = isDefault, DocType = docType, Json = JsonSerializer.Serialize(design, Json), Version = current.Version + 1, ModifiedAt = now, ModifiedBy = user.Id };
            expectedVersion ??= current.Version;
        }
        await store.SaveAsync(record, id is null ? null : expectedVersion, ct);
        if (isDefault)
        {
            // Only one default: the others stop being the default.
            foreach (var o in others.Where(IsDefaultDesign))
            {
                var r = await store.GetAsync(user.TenantId, o.Id, ct);
                if (r is null || !r.IsDefault) continue;
                await store.SaveAsync(r with { IsDefault = false, Json = Demote(r), Version = r.Version + 1, ModifiedAt = now, ModifiedBy = user.Id }, r.Version, ct);
            }
        }
        return new Stored<T>(record.Id, record.Version, design, now, user.Id);

        static bool IsDefaultDesign(Stored<T> o) => o.Design switch
        {
            LetterheadDesign l => l.IsDefault,
            PrintFormatDesign f => f.IsDefault,
            _ => false,
        };
    }

    private static string Demote(PrintDesignRecord r) => r.Kind == LetterheadKind
        ? JsonSerializer.Serialize(JsonSerializer.Deserialize<LetterheadDesign>(r.Json, Json)! with { IsDefault = false }, Json)
        : JsonSerializer.Serialize(JsonSerializer.Deserialize<PrintFormatDesign>(r.Json, Json)! with { IsDefault = false }, Json);

    private static string Promote(PrintDesignRecord r) => r.Kind == LetterheadKind
        ? JsonSerializer.Serialize(JsonSerializer.Deserialize<LetterheadDesign>(r.Json, Json)! with { IsDefault = true }, Json)
        : JsonSerializer.Serialize(JsonSerializer.Deserialize<PrintFormatDesign>(r.Json, Json)! with { IsDefault = true }, Json);

    private static Stored<T> Read<T>(PrintDesignRecord r) =>
        new(r.Id, r.Version, JsonSerializer.Deserialize<T>(r.Json, Json)!, r.ModifiedAt, r.ModifiedBy);

    private static void Demand(UserContext user)
    {
        if (!user.Roles.Contains(UserContext.SystemManager) && !user.Roles.Contains(UserContext.Administrator))
            throw new PermissionDeniedException("Only a System Manager can change letterheads and print formats.");
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
