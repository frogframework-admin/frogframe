using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

public sealed class PurchaseRequest { }
public sealed class PurchaseOrder { }
public sealed class GoodsReceipt { }

internal static class Procure
{
    public static decimal LineNet(IDictionary<string, object?> r) => r.Dec("qty") * r.Dec("rate") * (1 - r.Dec("discount_percent") / 100m);
    public static decimal UnitNet(IDictionary<string, object?> r) => r.Dec("rate") * (1 - r.Dec("discount_percent") / 100m);

    /// <summary>Qty diterima per baris PO (indeks 1-based) dari goods receipt yang sudah submit, kecuali satu dokumen.</summary>
    public static async Task<Dictionary<int, decimal>> ReceivedAsync(Masters m, string po, string? except, CancellationToken ct)
    {
        var map = new Dictionary<int, decimal>();
        foreach (var gr in (await m.QueryAsync("GoodsReceipt", [new FilterClause("purchase_order", "=", po)], ct, take: 2000))
                 .Where(g => g.DocStatus == DocStatus.Submitted && g.Name != except))
            foreach (var r in gr.Data.Rows("items"))
                map[(int)r.Dec("po_line")] = map.GetValueOrDefault((int)r.Dec("po_line")) + r.Dec("received_qty");
        return map;
    }

    public static async Task<decimal> ReceivedValueAsync(Masters m, Document po, CancellationToken ct)
    {
        var lines = po.Data.Rows("items");
        return (await ReceivedAsync(m, po.Name, null, ct)).Where(kv => kv.Key >= 1 && kv.Key <= lines.Count)
            .Sum(kv => D.R2(kv.Value * UnitNet(lines[kv.Key - 1])));
    }

    public static async Task<decimal> BilledAsync(Masters m, string po, string? except, CancellationToken ct) =>
        (await m.QueryAsync("PurchaseInvoice", [new FilterClause("purchase_order", "=", po)], ct, take: 2000))
            .Where(i => i.DocStatus == DocStatus.Submitted && i.Name != except).Sum(i => i.Data.Dec("total"));

    public static string Status(decimal done, decimal total, string none, string part, string full) =>
        done <= 0 ? none : done < total ? part : full;
}

/// <summary>Purchase Request: perusahaan & cost center dari pemohon; status pemesanan dihitung saat dibuka.</summary>
public sealed class PurchaseRequestHook(IDocumentStore store) : IBeforeValidate<PurchaseRequest>, IOnLoad<PurchaseRequest>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("Employee", d.Str("requested_by"), ct) is { } emp)
        {
            d.SetIfEmpty("company", emp.Data.Str("company"));
            d.SetIfEmpty("cost_center", emp.Data.Str("cost_center"));
        }
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        var ordered = (await new Masters(store, c.Doc.TenantId).QueryAsync("PurchaseOrder", [new FilterClause("purchase_request", "=", c.Doc.Name)], ct, take: 200))
            .Where(p => p.DocStatus == DocStatus.Submitted).ToList();
        var estimate = c.Doc.Data.Dec("total_estimate");
        c.Doc.Data["order_status"] = ordered.Count == 0 ? "Not ordered"
            : estimate > 0 && ordered.Sum(p => p.Data.Dec("total")) < estimate * 0.999m ? "Partly ordered" : "Ordered";
    }
}

/// <summary>Purchase Order: dari purchase request yang disetujui; termin & PPN masukan default; status terima & tagih dihitung saat dibuka.</summary>
public sealed class PurchaseOrderHook(IDocumentStore store) : IBeforeValidate<PurchaseOrder>, IOnLoad<PurchaseOrder>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("PurchaseRequest", d.Str("purchase_request"), ct) is { } pr)
        {
            if (c.Doc.DocStatus == DocStatus.Draft && pr.WorkflowState != Approval.Approved)
                throw Errors.One("purchase_request", $"{pr.Name} is {pr.WorkflowState}, not Approved");
            d.SetIfEmpty("company", pr.Data.Str("company"));
            d.SetIfEmpty("vendor", pr.Data.Str("suggested_vendor"));
            if (d.Rows("items").Count == 0)
                d["items"] = pr.Data.Rows("items").Select(r => new Dictionary<string, object?>
                {
                    ["item"] = r.Str("item"), ["item_name"] = r.Str("item_name"), ["description"] = r.Str("description"), ["qty"] = r.Dec("qty"),
                    ["uom"] = r.Str("uom"), ["rate"] = r.Dec("estimated_rate"), ["is_vat_applicable"] = true, ["cost_center"] = pr.Data.Str("cost_center"),
                }).ToList();
        }
        if (await m.GetAsync("Vendor", d.Str("vendor"), ct) is { } vendor)
        {
            d.SetIfEmpty("payment_term", vendor.Data.Str("payment_term"));
            if (d.Str("vat_code") is null && vendor.Data.Bool("is_pkp"))
                d["vat_code"] = (await m.SingleAsync("FinanceSettings", ct)).Str("default_input_vat");
            if (vendor.Data.Bool("on_hold")) c.Messages.Add($"Supplier {vendor.Name} is on hold: invoices can be recorded but payments are blocked");
        }
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        if (c.Doc.DocStatus != DocStatus.Submitted) { d["receipt_status"] = "Not received"; d["billing_status"] = "Not billed"; return; }
        var m = new Masters(store, c.Doc.TenantId);
        var total = d.Dec("total");
        var received = await Procure.ReceivedValueAsync(m, c.Doc, ct);
        var billed = await Procure.BilledAsync(m, c.Doc.Name, null, ct);
        d["received_amount"] = received; d["billed_amount"] = billed;
        d["receipt_status"] = Procure.Status(received, total * 0.999m, "Not received", "Partly received", "Fully received");
        d["billing_status"] = Procure.Status(billed, total * 0.999m, "Not billed", "Partly billed", "Fully billed");
    }
}

/// <summary>Goods Receipt: baris dari sisa PO bila kosong; qty diterima tidak boleh melebihi sisa per baris.</summary>
public sealed class GoodsReceiptHook(IDocumentStore store) : IBeforeValidate<GoodsReceipt>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var po = await m.RequireAsync("PurchaseOrder", d.Str("purchase_order"), "purchase_order", "Purchase order", ct);
        if (po.DocStatus != DocStatus.Submitted) throw Errors.One("purchase_order", $"{po.Name} is not submitted or has been cancelled");
        var lines = po.Data.Rows("items");
        var received = await Procure.ReceivedAsync(m, po.Name, c.Doc.Name, ct);
        decimal Pending(int i) => lines[i - 1].Dec("qty") - received.GetValueOrDefault(i);
        if (d.Rows("items").Count == 0)
            d["items"] = Enumerable.Range(1, lines.Count).Where(i => Pending(i) > 0)
                .Select(i => new Dictionary<string, object?> { ["po_line"] = (long)i, ["received_qty"] = Pending(i) }).ToList();
        var errors = new Errors();
        var rows = d.Rows("items");
        if (rows.Count == 0) errors.Add("items", $"Everything on {po.Name} has been received");
        for (var k = 0; k < rows.Count; k++)
        {
            var r = rows[k]; var line = (int)r.Dec("po_line");
            if (line < 1 || line > lines.Count) { errors.Add($"items[{k}].po_line", $"{po.Name} has no line {line}"); continue; }
            r["item_name"] = lines[line - 1].Str("item_name");
            r["ordered_qty"] = lines[line - 1].Dec("qty");
            r["pending_qty"] = Pending(line);
            if (r.Dec("received_qty") > Pending(line)) errors.Add($"items[{k}].received_qty", $"Line {line}: only {Pending(line):0.##} not yet received");
        }
        errors.ThrowIfAny();
    }
}

/// <summary>Purchase Invoice dengan PO: pemasok harus sama, baris disalin dari PO bila kosong; saat submit dicek 3-way match (total tagihan ≤ nilai diterima).</summary>
public sealed class PurchaseInvoicePoHook(IDocumentStore store) : IBeforeValidate<PurchaseInvoice>, IAfterSave<PurchaseInvoice>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("PurchaseOrder", d.Str("purchase_order"), ct) is not { } po) return;
        if (po.Data.Str("vendor") != d.Str("vendor")) throw Errors.One("purchase_order", $"{po.Name} belongs to another supplier");
        d.SetIfEmpty("payment_term", po.Data.Str("payment_term"));
        d.SetIfEmpty("vat_code", po.Data.Str("vat_code"));
        if (d.Rows("items").Count == 0)
            d["items"] = po.Data.Rows("items").Select(r => new Dictionary<string, object?>
            {
                ["item"] = r.Str("item"), ["item_name"] = r.Str("item_name"), ["description"] = r.Str("description"), ["qty"] = r.Dec("qty"),
                ["uom"] = r.Str("uom"), ["rate"] = r.Dec("rate"), ["discount_percent"] = r.Dec("discount_percent"),
                ["is_vat_applicable"] = r.Bool("is_vat_applicable"), ["cost_center"] = r.Str("cost_center"),
            }).ToList();
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        if (await m.GetAsync("PurchaseOrder", c.Doc.Data.Str("purchase_order"), ct) is not { } po) return;
        var mode = (await m.SingleAsync("FinanceSettings", ct)).Str("three_way_match") ?? "Warn";
        if (mode == "Off") return;
        var received = await Procure.ReceivedValueAsync(m, po, ct);
        var billed = await Procure.BilledAsync(m, po.Name, c.Doc.Name, ct) + c.Doc.Data.Dec("total");
        if (billed <= received + 1m) return;
        var msg = $"3-way match: {po.Name} billed {billed:N0} exceeds the received value {received:N0}";
        if (mode == "Stop") throw Errors.One("purchase_order", msg + "; record the goods receipt first");
        c.Messages.Add(msg);
    }
}

/// <summary>Aksi "Create purchase order" dari purchase request yang disetujui (role Purchasing).</summary>
public sealed class PurchaseRequestMakeOrderAction : IFormAction<PurchaseRequest>
{
    public string ActionId => "erp.purchaserequest.make_order";
    public string Label => "Create purchase order";
    public string Icon => "shopping-cart";

    public bool IsVisible(UserContext user, Document doc) => doc.WorkflowState == Approval.Approved && (user.HasRole("Purchasing") || user.IsAdmin);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (ctx.Doc.Data.Str("suggested_vendor") is not { } vendor) return FormActionResult.Fail("Choose the suggested supplier on the request first, or create the order manually");
        try
        {
            var po = await ctx.Docs.InsertAsync(ctx.User, "PurchaseOrder", new Dictionary<string, object?>
            {
                ["purchase_request"] = ctx.Doc.Name, ["vendor"] = vendor, ["company"] = ctx.Doc.Data.Str("company"),
                ["delivery_date"] = ctx.Doc.Data.Str("required_by"),
            }, $"Created from {ctx.Doc.Name}", ct);
            return FormActionResult.NavigateTo($"/app/PurchaseOrder/{po.Name}", $"Draft purchase order {po.Name} created; check the rates");
        }
        catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException)
        {
            return FormActionResult.Fail($"Purchase order creation failed: {ex.Message}");
        }
    }
}

/// <summary>Aksi "Receive" pada PO: draft goods receipt berisi semua sisa qty.</summary>
public sealed class PurchaseOrderReceiveAction : IFormAction<PurchaseOrder>
{
    public string ActionId => "erp.purchaseorder.receive";
    public string Label => "Receive";
    public string Icon => "package-check";

    public bool IsVisible(UserContext user, Document doc) =>
        doc.DocStatus == DocStatus.Submitted && doc.Data.Str("receipt_status") != "Fully received" && (user.HasRole("Purchasing") || user.IsAdmin);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        try
        {
            var gr = await ctx.Docs.InsertAsync(ctx.User, "GoodsReceipt", new Dictionary<string, object?> { ["purchase_order"] = ctx.Doc.Name },
                $"Created from {ctx.Doc.Name}", ct);
            return FormActionResult.NavigateTo($"/app/GoodsReceipt/{gr.Name}", $"Draft goods receipt {gr.Name} created with all pending quantities");
        }
        catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException)
        {
            return FormActionResult.Fail($"Goods receipt creation failed: {ex.Message}");
        }
    }
}
