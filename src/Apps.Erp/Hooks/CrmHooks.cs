using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

internal static class Crm
{
    public const string Converted = "Converted";

    public static async Task<IDictionary<string, object?>> SettingsAsync(Masters m, CancellationToken ct) => await m.SingleAsync("CRMSettings", ct);

    public static async Task<Document?> StageAsync(Masters m, string stageType, CancellationToken ct) =>
        (await m.QueryAsync("SalesStage", [new FilterClause("stage_type", "=", stageType)], ct, "sequence", "asc", 50))
            .FirstOrDefault(s => !(s.Data.TryGetValue("is_active", out var a) && a is false));

    /// <summary>Isi nama/satuan baris dari Item; harga dari ItemPrice bila masih 0 (khusus pelanggan dulu, lalu umum; berlaku pada tanggal).</summary>
    public static async Task FillLinesAsync(Masters m, List<Dictionary<string, object?>> rows, string? priceList, string? customer, DateOnly date, CancellationToken ct)
    {
        foreach (var r in rows)
        {
            var item = await m.GetAsync("Item", r.Str("item"), ct);
            if (item is null) continue;
            r.SetIfEmpty("item_name", item.Data.Str("item_name"));
            r.SetIfEmpty("uom", item.Data.Str("uom"));
            if (r.Dec("rate") != 0 || priceList is null) continue;
            var prices = (await m.QueryAsync("ItemPrice", [new FilterClause("item", "=", item.Name), new FilterClause("price_list", "=", priceList)], ct, take: 50))
                .Where(p => (p.Data.Date("valid_from") ?? DateOnly.MinValue) <= date && date <= (p.Data.Date("valid_to") ?? DateOnly.MaxValue)
                            && (p.Data.Str("customer") is null || p.Data.Str("customer") == customer))
                .OrderByDescending(p => p.Data.Str("customer") is not null).ThenByDescending(p => p.Data.Str("valid_from")).ToList();
            if (prices.FirstOrDefault() is { } price) r["rate"] = price.Data.Dec("rate");
        }
    }

    public static decimal LineAmount(Dictionary<string, object?> r) => D.R2(r.Dec("qty") * r.Dec("rate") * (1 - r.Dec("discount_percent") / 100m));
}

/// <summary>Lead: peringatan duplikat saat dibuat; hasil konversi (pelanggan, kontak, opportunity) diisi dari dokumen yang menunjuk lead ini.</summary>
public sealed class LeadHook(IDocumentStore store, TimeProvider clock) : IBeforeInsert<Lead>, IBeforeValidate<Lead>
{
    public async Task BeforeInsertAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var mode = (await Crm.SettingsAsync(m, ct)).Str("duplicate_check") ?? "Email & mobile";
        if (mode == "No") return;
        var checks = new List<(string DocType, string Field, string? Value)>();
        if (mode.StartsWith("Email", StringComparison.Ordinal)) { checks.Add(("Lead", "email", d.Str("email"))); checks.Add(("Customer", "email", d.Str("email"))); }
        if (mode == "Email & mobile") checks.Add(("Lead", "mobile", d.Str("mobile")));
        if (mode == "Company name") { checks.Add(("Lead", "company_name", d.Str("company_name"))); checks.Add(("Customer", "customer_name", d.Str("company_name"))); }
        foreach (var (docType, field, value) in checks.Where(x => x.Value is not null))
            if ((await m.QueryAsync(docType, [new FilterClause(field, "=", value)], ct, take: 1)).FirstOrDefault() is { } dup)
                c.Messages.Add($"Possible duplicate: {docType} {dup.Name} has the same {field} ({value})");
    }

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        if (d.Str("status") != Crm.Converted || d.Str("converted_customer") is not null) return;
        var m = new Masters(store, c.Doc.TenantId);
        var customer = (await m.QueryAsync("Customer", [new FilterClause("lead", "=", c.Doc.Name)], ct, take: 1)).FirstOrDefault();
        if (customer is null) return; // aturan metadata menolak status Dikonversi tanpa pelanggan
        d["converted_customer"] = customer.Name;
        d["converted_contact"] = customer.Data.Str("primary_contact");
        d["converted_opportunity"] = (await m.QueryAsync("Opportunity", [new FilterClause("lead", "=", c.Doc.Name)], ct, take: 1)).FirstOrDefault()?.Name;
        d["converted_on"] = D.Iso(DateOnly.FromDateTime(clock.GetLocalNow().Date));
    }
}

/// <summary>
/// Aksi "Convert to customer" (Salesforce Lead Convert): Customer (+ lead), Contact (tertaut ke Customer), Opportunity opsional, lalu Lead → Converted.
/// Berjalan sebagai user yang menekan tombol (izin & scope Sales berlaku). Framework tidak punya transaksi multi-dokumen dari form action,
/// jadi bila gagal di tengah dipakai kompensasi: dokumen yang sudah dibuat dihapus lagi (urutan terbalik, sebagai user sistem).
/// Bila penghapusan itu sendiri gagal, pesan menyebutkan dokumen yang tertinggal.
/// </summary>
public sealed class LeadConvertAction(IDocumentStore store, TimeProvider clock) : IFormAction<Lead>
{
    public string ActionId => "erp.lead.convert";
    public string Label => "Convert to customer";
    public string Icon => "user-check";
    public string Tone => "positive";

    public bool IsVisible(UserContext user, Document doc) =>
        doc.Data.Str("status") is not (Crm.Converted or "Unqualified") && (user.HasRole("Sales") || user.HasRole("Manager"));

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var m = new Masters(store, ctx.Doc.TenantId);
        var lead = ctx.Doc.Data;
        var settings = await Crm.SettingsAsync(m, ct);
        var group = settings.Str("default_customer_group");
        if (group is null) return FormActionResult.Fail("Set 'Customer group for converted leads' in CRM Settings");
        var created = new List<(string DocType, string Name)>();
        try
        {
            var name = lead.Str("company_name") ?? lead.Str("lead_name")!;
            var customer = await ctx.Docs.InsertAsync(ctx.User, "Customer", new Dictionary<string, object?>
            {
                ["customer_name"] = name, ["customer_type"] = lead.Str("company_name") is null ? "Individual" : "Company",
                ["customer_group"] = group, ["industry"] = lead.Str("industry"), ["email"] = lead.Str("email"),
                ["phone"] = lead.Str("phone") ?? lead.Str("mobile"), ["website"] = lead.Str("website"),
                ["account_owner"] = lead.Str("sales_rep"), ["lead"] = ctx.Doc.Name,
            }, "Created from lead", ct);
            created.Add(("Customer", customer.Name));

            var contact = await ctx.Docs.InsertAsync(ctx.User, "Contact", new Dictionary<string, object?>
            {
                ["salutation"] = lead.Str("salutation"), ["full_name"] = lead.Str("lead_name"), ["designation"] = lead.Str("job_title"),
                ["email"] = lead.Str("email"), ["mobile"] = lead.Str("mobile"), ["phone"] = lead.Str("phone"), ["is_primary"] = true,
                ["links"] = new List<Dictionary<string, object?>> { new() { ["link_doctype"] = "Customer", ["link_name"] = customer.Name } },
            }, "Created from lead", ct);
            created.Add(("Contact", contact.Name));
            await ctx.Docs.UpdateAsync(ctx.User, "Customer", customer.Name, customer.Version,
                new Dictionary<string, object?> { ["primary_contact"] = contact.Name }, ct);

            if (!settings.ContainsKey("create_opportunity_on_convert") || settings.Bool("create_opportunity_on_convert"))
            {
                var company = (await m.SingleAsync("GlobalDefaults", ct)).Str("default_company");
                var stage = settings.Str("default_sales_stage") ?? (await Crm.StageAsync(m, "Open", ct))?.Name;
                if (company is null || stage is null)
                    throw new ValidationFailedException([new FieldError("name", "Set the default company (General Settings) and sales stages")]);
                var opp = await ctx.Docs.InsertAsync(ctx.User, "Opportunity", new Dictionary<string, object?>
                {
                    ["opportunity_name"] = name, ["customer"] = customer.Name, ["lead"] = ctx.Doc.Name, ["company"] = company,
                    ["sales_stage"] = stage, ["lead_source"] = lead.Str("lead_source"), ["amount"] = lead.Dec("estimated_value"),
                    ["expected_close_date"] = D.Iso(DateOnly.FromDateTime(clock.GetLocalNow().Date).AddDays(30)),
                    ["sales_rep"] = lead.Str("sales_rep"),
                    ["contact_roles"] = new List<Dictionary<string, object?>> { new() { ["contact"] = contact.Name, ["role"] = "Decision Maker", ["is_primary"] = true } },
                }, "Created from lead", ct);
                created.Add(("Opportunity", opp.Name));
            }

            await ctx.Docs.UpdateAsync(ctx.User, "Lead", ctx.Doc.Name, ctx.Doc.Version, new Dictionary<string, object?> { ["status"] = Crm.Converted }, ct);
            return FormActionResult.NavigateTo($"/app/Customer/{customer.Name}", $"Lead converted: {string.Join(", ", created.Select(x => $"{x.DocType} {x.Name}"))}");
        }
        catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException or ConcurrencyException or LinkedDocumentException)
        {
            var left = await RollbackAsync(ctx.Docs, ctx.Doc.TenantId, created, ct);
            var tail = left.Count > 0 ? $" Could not remove: {string.Join(", ", left)}; delete them manually." : created.Count > 0 ? " Created documents were removed." : "";
            return FormActionResult.Fail($"Conversion failed: {ex.Message}.{tail}");
        }
    }

    /// <summary>Kompensasi: hapus dokumen yang sudah dibuat (terbalik). Mengembalikan dokumen yang gagal dihapus.</summary>
    private static async Task<List<string>> RollbackAsync(DocumentService docs, string tenant, List<(string DocType, string Name)> created, CancellationToken ct)
    {
        var sys = UserContext.System(tenant);
        var left = new List<string>();
        // Customer.primary_contact menunjuk Contact; lepaskan dulu agar Contact bisa dihapus (framework menolak hapus dokumen yang masih ditautkan).
        if (created.FirstOrDefault(x => x.DocType == "Customer") is { Name: { } cust })
        {
            try
            {
                var c = await docs.GetAsync(sys, "Customer", cust, ct);
                if (c.Data.Str("primary_contact") is not null)
                    await docs.UpdateAsync(sys, "Customer", cust, c.Version, new Dictionary<string, object?> { ["primary_contact"] = null }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        foreach (var (docType, name) in Enumerable.Reverse(created))
        {
            try
            {
                var current = await docs.GetAsync(sys, docType, name, ct);
                await docs.DeleteAsync(sys, docType, name, current.Version, ct);
            }
            catch (DocumentNotFoundException) { }
            catch (Exception ex) when (ex is not OperationCanceledException) { left.Add($"{docType} {name}"); }
        }
        return left;
    }
}

/// <summary>Opportunity: nilai dari total produk bila ada baris; tanggal closing saat tahap Menang/Kalah.</summary>
public sealed class OpportunityHook(IDocumentStore store, TimeProvider clock) : IBeforeValidate<Opportunity>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var today = DateOnly.FromDateTime(clock.GetLocalNow().Date);
        var rows = d.Rows("lines");
        await Crm.FillLinesAsync(m, rows, null, d.Str("customer"), today, ct);
        if (rows.Count > 0) d["amount"] = rows.Sum(Crm.LineAmount);
        var stageType = await m.StrAsync("SalesStage", d.Str("sales_stage"), "stage_type", ct);
        if (stageType is "Won" or "Lost") d.SetIfEmpty("closed_date", D.Iso(today));
        else d["closed_date"] = null;
        if (d.Str("customer") is null && d.Str("lead") is { } lead
            && (await m.GetAsync("Lead", lead, ct))?.Data.Str("converted_customer") is { } converted)
            d["customer"] = converted;
    }
}

/// <summary>Penawaran: data dari opportunity, masa berlaku default, harga dari daftar harga, PPN default; diterima → opportunity Menang.</summary>
public sealed class QuotationHook(IDocumentStore store) : IBeforeValidate<Quotation>, IAfterSave<Quotation>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("Opportunity", d.Str("opportunity"), ct) is { } opp)
        {
            d.SetIfEmpty("customer", opp.Data.Str("customer"));
            d.SetIfEmpty("lead", opp.Data.Str("lead"));
            d.SetIfEmpty("company", opp.Data.Str("company"));
            if (d.Rows("items").Count == 0)
                d["items"] = opp.Data.Rows("lines").Select(l => new Dictionary<string, object?>
                {
                    ["item"] = l.Str("item"), ["item_name"] = l.Str("item_name"), ["description"] = l.Str("description"),
                    ["qty"] = l.Dec("qty"), ["uom"] = l.Str("uom"), ["rate"] = l.Dec("rate"), ["discount_percent"] = l.Dec("discount_percent"),
                    ["is_vat_applicable"] = true,
                }).ToList();
        }
        var date = d.Date("quotation_date") ?? DateOnly.MinValue;
        var days = (await Crm.SettingsAsync(m, ct)).Dec("quotation_validity_days");
        if (date != DateOnly.MinValue) d.SetIfEmpty("valid_until", D.Iso(date.AddDays((int)(days > 0 ? days : 30))));
        if (await m.GetAsync("Customer", d.Str("customer"), ct) is { } customer)
        {
            d.SetIfEmpty("price_list", customer.Data.Str("default_price_list"));
            d.SetIfEmpty("payment_term", customer.Data.Str("payment_term"));
        }
        await Crm.FillLinesAsync(m, d.Rows("items"), d.Str("price_list"), d.Str("customer"), date, ct);
        if (d.Str("vat_code") is null && await m.GetAsync("Company", d.Str("company"), ct) is { } co && co.Data.Bool("is_pkp"))
            d["vat_code"] = (await m.SingleAsync("FinanceSettings", ct)).Str("default_output_vat");
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState == "Accepted" && c.Previous?.WorkflowState != "Accepted" && c.Doc.Data.Str("opportunity") is { } opp)
            ErpEvents.Emit(c, ErpEvents.QuotationWon, new() { ["opportunity"] = opp });
        return Task.CompletedTask;
    }
}

/// <summary>Aksi "Buat Faktur" dari penawaran yang diterima (role Finance, karena Sales tidak membuat faktur).</summary>
public sealed class QuotationMakeInvoiceAction(TimeProvider clock) : IFormAction<Quotation>
{
    public string ActionId => "erp.quotation.make_invoice";
    public string Label => "Create Sales Invoice";
    public string Icon => "receipt";

    public bool IsVisible(UserContext user, Document doc) => doc.WorkflowState == "Accepted" && user.HasRole("Finance");

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var q = ctx.Doc.Data;
        if (q.Str("customer") is null) return FormActionResult.Fail("This quotation is still for a lead; convert the lead to a customer first");
        var today = D.Iso(DateOnly.FromDateTime(clock.GetLocalNow().Date));
        try
        {
            var inv = await ctx.Docs.InsertAsync(ctx.User, "SalesInvoice", new Dictionary<string, object?>
            {
                ["company"] = q.Str("company"), ["customer"] = q.Str("customer"), ["posting_date"] = today, ["due_date"] = today,
                ["currency"] = q.Str("currency"), ["price_list"] = q.Str("price_list"), ["payment_term"] = q.Str("payment_term"),
                ["quotation"] = ctx.Doc.Name, ["vat_code"] = q.Str("vat_code"),
                ["items"] = q.Rows("items").Select(r => new Dictionary<string, object?>
                {
                    ["item"] = r.Str("item"), ["item_name"] = r.Str("item_name"), ["description"] = r.Str("description"), ["qty"] = r.Dec("qty"),
                    ["uom"] = r.Str("uom"), ["rate"] = r.Dec("rate"), ["discount_percent"] = r.Dec("discount_percent"),
                    ["is_vat_applicable"] = r.Bool("is_vat_applicable"),
                }).ToList(),
            }, $"Created from quotation {ctx.Doc.Name}", ct);
            return FormActionResult.NavigateTo($"/app/SalesInvoice/{inv.Name}", $"Draft invoice {inv.Name} created");
        }
        catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException)
        {
            return FormActionResult.Fail($"Invoice creation failed: {ex.Message}");
        }
    }
}
