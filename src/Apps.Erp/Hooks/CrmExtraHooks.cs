using Framework.Accounting;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

public sealed class Campaign { }
public sealed class SalesOrder { }
public sealed class CustomerContract { }
public sealed class CommissionStatement { }

internal static class CrmLines
{
    public static List<Dictionary<string, object?>> Copy(IEnumerable<Dictionary<string, object?>> rows) =>
        rows.Select(r => new Dictionary<string, object?>
        {
            ["item"] = r.Str("item"), ["item_name"] = r.Str("item_name"), ["description"] = r.Str("description"), ["qty"] = r.Dec("qty"),
            ["uom"] = r.Str("uom"), ["rate"] = r.Dec("rate"), ["discount_percent"] = r.Dec("discount_percent"), ["is_vat_applicable"] = r.Bool("is_vat_applicable"),
        }).ToList();
}

/// <summary>Opportunity & Lead: territory dari customer bila kosong.</summary>
public sealed class OpportunityTerritoryHook(IDocumentStore store) : IBeforeValidate<Opportunity>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct) =>
        c.Doc.Data.SetIfEmpty("territory", await new Masters(store, c.Doc.TenantId).StrAsync("Customer", c.Doc.Data.Str("customer"), "territory", ct));
}

/// <summary>Campaign: jumlah lead, opportunity, dan nilai menang dihitung saat dibuka (ROI kampanye).</summary>
public sealed class CampaignHook(IDocumentStore store) : IOnLoad<Campaign>
{
    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var opps = await m.QueryAsync("Opportunity", [new FilterClause("campaign", "=", c.Doc.Name)], ct, take: 10_000);
        c.Doc.Data["lead_count"] = (long)(await m.QueryAsync("Lead", [new FilterClause("campaign", "=", c.Doc.Name)], ct, take: 10_000)).Count;
        c.Doc.Data["opportunity_count"] = (long)opps.Count;
        c.Doc.Data["won_amount"] = opps.Where(o => o.Data.Str("stage_type") == "Won").Sum(o => o.Data.Dec("amount"));
    }
}

/// <summary>Sales Order: isi dari penawaran Accepted, harga dari daftar harga, PPN default; status penagihan dihitung saat dibuka.</summary>
public sealed class SalesOrderHook(IDocumentStore store) : IBeforeValidate<SalesOrder>, IOnLoad<SalesOrder>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("Quotation", d.Str("quotation"), ct) is { } q)
        {
            if (c.Doc.DocStatus == DocStatus.Draft && q.WorkflowState is not ("Accepted" or "Billed"))
                throw Errors.One("quotation", $"{q.Name} is {q.WorkflowState}; only an accepted quotation becomes an order");
            if (q.Data.Str("customer") is null) throw Errors.One("quotation", "This quotation is still for a lead; convert the lead to a customer first");
            d.SetIfEmpty("customer", q.Data.Str("customer"));
            d.SetIfEmpty("company", q.Data.Str("company"));
            d.SetIfEmpty("price_list", q.Data.Str("price_list"));
            d.SetIfEmpty("payment_term", q.Data.Str("payment_term"));
            d.SetIfEmpty("vat_code", q.Data.Str("vat_code"));
            d.SetIfEmpty("sales_rep", q.Data.Str("sales_rep"));
            if (d.Rows("items").Count == 0) d["items"] = CrmLines.Copy(q.Data.Rows("items"));
        }
        if (await m.GetAsync("Customer", d.Str("customer"), ct) is { } customer)
        {
            d.SetIfEmpty("price_list", customer.Data.Str("default_price_list"));
            d.SetIfEmpty("payment_term", customer.Data.Str("payment_term"));
        }
        await Crm.FillLinesAsync(m, d.Rows("items"), d.Str("price_list"), d.Str("customer"), d.Date("order_date") ?? DateOnly.MinValue, ct);
        if (d.Str("vat_code") is null && await m.GetAsync("Company", d.Str("company"), ct) is { } co && co.Data.Bool("is_pkp"))
            d["vat_code"] = (await m.SingleAsync("FinanceSettings", ct)).Str("default_output_vat");
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted) { c.Doc.Data["billing_status"] = "Not billed"; return; }
        var billed = (await new Masters(store, c.Doc.TenantId).QueryAsync("SalesInvoice", [new FilterClause("sales_order", "=", c.Doc.Name)], ct, take: 1000))
            .Where(i => i.DocStatus == DocStatus.Submitted).Sum(i => i.Data.Dec("total"));
        var total = c.Doc.Data.Dec("total");
        c.Doc.Data["billed_amount"] = billed;
        c.Doc.Data["billing_status"] = billed <= 0 ? "Not billed" : billed < total ? "Partly billed" : "Fully billed";
    }
}

/// <summary>Faktur penjualan: sales rep (dasar komisi) dari sales order → penawaran → account owner pelanggan; sales order harus milik pelanggan yang sama.</summary>
public sealed class SalesInvoiceSalesRepHook(IDocumentStore store) : IBeforeValidate<SalesInvoice>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("SalesOrder", d.Str("sales_order"), ct) is { } so)
        {
            if (so.Data.Str("customer") != d.Str("customer")) throw Errors.One("sales_order", $"{so.Name} belongs to another customer");
            d.SetIfEmpty("quotation", so.Data.Str("quotation"));
            d.SetIfEmpty("sales_rep", so.Data.Str("sales_rep"));
        }
        d.SetIfEmpty("sales_rep", await m.StrAsync("Quotation", d.Str("quotation"), "sales_rep", ct));
        d.SetIfEmpty("sales_rep", await m.StrAsync("Customer", d.Str("customer"), "account_owner", ct));
    }
}

/// <summary>Aksi "Create sales order" dari penawaran Accepted.</summary>
public sealed class QuotationMakeSalesOrderAction : IFormAction<Quotation>
{
    public string ActionId => "erp.quotation.make_sales_order";
    public string Label => "Create sales order";
    public string Icon => "clipboard-check";

    public bool IsVisible(UserContext user, Document doc) => doc.WorkflowState == "Accepted" && (user.HasRole("Sales") || user.HasRole("Manager"));

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        try
        {
            var so = await ctx.Docs.InsertAsync(ctx.User, "SalesOrder", new Dictionary<string, object?>
            {
                ["quotation"] = ctx.Doc.Name, ["customer"] = ctx.Doc.Data.Str("customer"), ["company"] = ctx.Doc.Data.Str("company"),
                ["currency"] = ctx.Doc.Data.Str("currency"), ["sales_rep"] = ctx.Doc.Data.Str("sales_rep"),
            }, $"Created from quotation {ctx.Doc.Name}", ct);
            return FormActionResult.NavigateTo($"/app/SalesOrder/{so.Name}", $"Draft sales order {so.Name} created");
        }
        catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException)
        {
            return FormActionResult.Fail($"Sales order creation failed: {ex.Message}");
        }
    }
}

/// <summary>Aksi "Create Sales Invoice" dari sales order (sisa yang belum ditagih ditagih penuh; ubah qty di draft untuk tagihan sebagian).</summary>
public sealed class SalesOrderMakeInvoiceAction(TimeProvider clock) : IFormAction<SalesOrder>
{
    public string ActionId => "erp.salesorder.make_invoice";
    public string Label => "Create Sales Invoice";
    public string Icon => "receipt";

    public bool IsVisible(UserContext user, Document doc) =>
        doc.DocStatus == DocStatus.Submitted && doc.Data.Str("billing_status") != "Fully billed" && (user.HasRole("Finance") || user.IsAdmin);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var so = ctx.Doc.Data;
        var today = D.Iso(DateOnly.FromDateTime(clock.GetLocalNow().Date));
        try
        {
            var inv = await ctx.Docs.InsertAsync(ctx.User, "SalesInvoice", new Dictionary<string, object?>
            {
                ["company"] = so.Str("company"), ["customer"] = so.Str("customer"), ["posting_date"] = today, ["due_date"] = today,
                ["currency"] = so.Str("currency"), ["price_list"] = so.Str("price_list"), ["payment_term"] = so.Str("payment_term"),
                ["sales_order"] = ctx.Doc.Name, ["quotation"] = so.Str("quotation"), ["sales_rep"] = so.Str("sales_rep"),
                ["customer_po_no"] = so.Str("customer_po_no"), ["vat_code"] = so.Str("vat_code"), ["items"] = CrmLines.Copy(so.Rows("items")),
            }, $"Created from sales order {ctx.Doc.Name}", ct);
            var note = so.Dec("billed_amount") > 0 ? " Part of this order is already billed; adjust quantities." : "";
            return FormActionResult.NavigateTo($"/app/SalesInvoice/{inv.Name}", $"Draft invoice {inv.Name} created.{note}");
        }
        catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException)
        {
            return FormActionResult.Fail($"Invoice creation failed: {ex.Message}");
        }
    }
}

/// <summary>Kontrak pelanggan: perpanjangan harus pelanggan yang sama; status dihitung saat dibuka.</summary>
public sealed class CustomerContractHook(IDocumentStore store, TimeProvider clock) : IBeforeValidate<CustomerContract>, IOnLoad<CustomerContract>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("CustomerContract", d.Str("renewal_of"), ct) is { } prev && prev.Data.Str("customer") != d.Str("customer"))
            throw Errors.One("renewal_of", $"{prev.Name} belongs to another customer");
        d.SetIfEmpty("sales_rep", await m.StrAsync("Customer", d.Str("customer"), "account_owner", ct));
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct) =>
        c.Doc.Data["contract_status"] = await StatusAsync(new Masters(store, c.Doc.TenantId), c.Doc, DateOnly.FromDateTime(clock.GetLocalNow().Date), ct);

    internal static async Task<string> StatusAsync(Masters m, Document k, DateOnly today, CancellationToken ct)
    {
        if (k.DocStatus == DocStatus.Draft) return "Draft";
        if (k.DocStatus == DocStatus.Cancelled) return "Cancelled";
        var renewed = (await m.QueryAsync("CustomerContract", [new FilterClause("renewal_of", "=", k.Name)], ct, take: 10)).Any(x => x.DocStatus == DocStatus.Submitted);
        if (renewed) return "Renewed";
        var end = k.Data.Date("end_date") ?? DateOnly.MaxValue;
        if (end < today) return "Expired";
        return end.AddDays(-(int)k.Data.Dec("notice_days")) <= today ? "Expiring soon" : "Active";
    }
}

/// <summary>
/// Laporan komisi per sales rep per bulan: Σ (total sebelum PPN × kurs) faktur penjualan submit milik sales rep − nota kredit atas faktur
/// miliknya, × tarif rencana (atau tarif khusus rep). Submit: Dr beban komisi / Cr utang komisi (pihak = karyawan, against = statement).
/// </summary>
public sealed class CommissionStatementHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<CommissionStatement>, IAfterSave<CommissionStatement>, IOnCancel<CommissionStatement>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var rep = d.Str("sales_rep"); var period = d.Str("period"); var company = d.Str("company");
        if (rep is null || period is null || company is null) return;
        var end = Payroll.PeriodEnd(period); var start = new DateOnly(end.Year, end.Month, 1);
        d.SetIfEmpty("posting_date", D.Iso(end));

        var plan = await m.RequireAsync("CommissionPlan", d.Str("commission_plan"), "commission_plan", "Commission plan", ct);
        if (plan.Data.Date("valid_from") > end || (plan.Data.Date("valid_to") is { } to && to < start))
            throw Errors.One("commission_plan", $"{plan.Name} is not valid in {period}");
        var reps = plan.Data.Rows("sales_reps");
        var mine = reps.FirstOrDefault(r => r.Str("sales_rep") == rep);
        if (reps.Count > 0 && mine is null) throw Errors.One("sales_rep", $"{rep} is not a participant of {plan.Name}");
        var rate = mine is not null && mine.Dec("rate") > 0 ? mine.Dec("rate") : plan.Data.Dec("rate");
        d["rate"] = rate;

        var dup = (await m.QueryAsync("CommissionStatement", [new FilterClause("sales_rep", "=", rep), new FilterClause("period", "=", period)], ct, take: 20))
            .FirstOrDefault(x => x.Name != c.Doc.Name && x.DocStatus != DocStatus.Cancelled && x.Data.Str("company") == company && x.Name != d.Str("amended_from"));
        if (dup is not null) throw Errors.One("period", $"{dup.Name} already covers {rep} for {period}");

        static decimal Base(Document x) => D.R2(x.Data.Dec("total") * (x.Data.Dec("exchange_rate") is > 0m and var r ? r : 1m));
        bool InPeriod(Document x) => x.DocStatus == DocStatus.Submitted && x.Data.Str("company") == company
                                     && x.Data.Date("posting_date") is { } p && p >= start && p <= end;
        var lines = new List<Dictionary<string, object?>>();
        var invoices = (await m.QueryAsync("SalesInvoice", [new FilterClause("sales_rep", "=", rep)], ct, take: 10_000)).ToList();
        foreach (var inv in invoices.Where(InPeriod).OrderBy(x => x.Data.Str("posting_date")))
            lines.Add(Line("SalesInvoice", inv, Base(inv), rate));
        var mineInvoices = invoices.Select(i => i.Name).ToHashSet();
        foreach (var cn in (await m.QueryAsync("SalesCreditNote", [new FilterClause("company", "=", company)], ct, take: 10_000))
                 .Where(x => InPeriod(x) && x.Data.Str("return_against") is { } orig && mineInvoices.Contains(orig)).OrderBy(x => x.Data.Str("posting_date")))
            lines.Add(Line("SalesCreditNote", cn, -Base(cn), rate));
        d["lines"] = lines;
        d["total_base"] = lines.Sum(l => (decimal)l["base_amount"]!);
        d["total_commission"] = lines.Sum(l => (decimal)l["commission"]!);
    }

    private static Dictionary<string, object?> Line(string type, Document x, decimal baseAmount, decimal rate) => new()
    {
        ["voucher_type"] = type, ["voucher_no"] = x.Name, ["customer"] = x.Data.Str("customer"), ["posting_date"] = x.Data.Str("posting_date"),
        ["base_amount"] = baseAmount, ["commission"] = PayrollCalc.Rupiah(baseAmount * rate / 100m),
    };

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var total = d.Dec("total_commission");
        if (total == 0) return; // tidak ada komisi: tidak ada jurnal
        if (total < 0) throw Errors.One("total_commission", "Commission is negative (credit notes exceed invoices); carry it to the next period instead");
        var plan = await m.RequireAsync("CommissionPlan", d.Str("commission_plan"), "commission_plan", "Commission plan", ct);
        var expense = plan.Data.Str("expense_account") ?? throw Errors.One("commission_plan", $"Set the commission expense account on {plan.Name}");
        var payable = plan.Data.Str("payable_account") ?? throw Errors.One("commission_plan", $"Set the commission payable account on {plan.Name}");
        var date = d.Date("posting_date") ?? throw Errors.One("posting_date", "The posting date is required");
        await Periods.CheckAsync(m, c.User, date, null, "posting_date", ct);
        var cc = await m.StrAsync("Employee", d.Str("sales_rep"), "cost_center", ct);
        var posting = new Posting(date, $"Commission {d.Str("sales_rep")} {d.Str("period")}");
        posting.Dr(expense, total, costCenter: cc);
        posting.Cr(payable, total, "Employee", d.Str("sales_rep"), "CommissionStatement", c.Doc.Name);
        var lines = posting.Build();
        await BudgetControl.CheckAsync(c, m, ledger, lines, d.Str("company"), ct);
        c.AddLedgers(lines);
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
}
