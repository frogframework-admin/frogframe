using Framework.Accounting;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

public sealed class Warehouse { }
public sealed class StockEntry { }
public sealed class StockReconciliation { }

/// <summary>Satu mutasi stok (masuk positif, keluar negatif) beserta nilainya dalam mata uang fungsional.</summary>
internal sealed record Movement(string Item, string Warehouse, DateOnly Date, DateTimeOffset Created, string DocType, string DocName, decimal Qty, decimal Value);

internal sealed class Bin
{
    public decimal Qty, Value;
    public DateOnly LastDate = DateOnly.MinValue;
    public decimal Rate => Qty > 0 ? Value / Qty : 0m;
}

/// <summary>
/// Persediaan perpetual dengan rata-rata bergerak (PSAK 14 rata-rata tertimbang). Tidak ada tabel buku stok terpisah: saldo dihitung dari
/// dokumen mutasi yang sudah submit (Stock Entry, Goods Receipt, faktur/nota dengan Update stock, Stock Opname). Nilai tiap baris dikunci
/// di dokumennya saat submit. Agar nilai rata-rata tidak perlu dihitung ulang: posting mundur sebelum mutasi terakhir item-gudang ditolak,
/// dan pembatalan hanya boleh bila belum ada mutasi sesudahnya.
/// </summary>
internal static class Stock
{
    public static readonly string[] Sources = ["StockEntry", "GoodsReceipt", "SalesInvoice", "SalesCreditNote", "PurchaseInvoice", "StockReconciliation"];

    public static bool HasStock(IDictionary<string, object?> row) => row.TryGetValue("stock_amount", out var v) && v is not null;

    public static DateOnly DateOf(Document d) =>
        d.Data.Date("posting_date") ?? d.Data.Date("receipt_date") ?? DateOnly.FromDateTime(d.Created.UtcDateTime);

    /// <summary>Mutasi yang tercatat di satu dokumen (dari nilai yang dikunci saat submit).</summary>
    public static IEnumerable<Movement> Extract(Document doc)
    {
        var d = doc.Data; var date = DateOf(doc);
        Movement Mv(string? item, string? wh, decimal qty, decimal value) => new(item ?? "", wh ?? "", date, doc.Created, doc.DocType, doc.Name, qty, value);
        switch (doc.DocType)
        {
            case "StockEntry":
                var purpose = d.Str("purpose");
                foreach (var r in d.Rows("items"))
                {
                    var s = purpose == "Material Receipt" ? null : r.Str("s_warehouse") ?? d.Str("from_warehouse");
                    var t = purpose == "Material Issue" ? null : r.Str("t_warehouse") ?? d.Str("to_warehouse");
                    if (s is not null) yield return Mv(r.Str("item"), s, -r.Dec("qty"), -r.Dec("amount"));
                    if (t is not null) yield return Mv(r.Str("item"), t, r.Dec("qty"), r.Dec("amount"));
                }
                break;
            case "GoodsReceipt":
                foreach (var r in d.Rows("items").Where(r => r.Bool("is_stock_item")))
                    yield return Mv(r.Str("item"), d.Str("warehouse"), r.Dec("received_qty"), r.Dec("amount"));
                break;
            case "SalesInvoice":
            case "SalesCreditNote":
            case "PurchaseInvoice":
                if (!d.Bool("update_stock")) break;
                var sign = doc.DocType == "SalesInvoice" ? -1m : 1m;
                foreach (var r in d.Rows("items").Where(HasStock))
                    yield return Mv(r.Str("item"), d.Str("warehouse"), sign * r.Dec("qty"), sign * r.Dec("stock_amount"));
                break;
            case "StockReconciliation":
                foreach (var r in d.Rows("items"))
                    yield return Mv(r.Str("item"), d.Str("warehouse"), r.Dec("qty_difference"), r.Dec("amount_difference"));
                break;
        }
    }

    /// <summary>Semua mutasi dari dokumen submit (kecuali satu dokumen), urut tanggal lalu waktu dibuat.</summary>
    public static async Task<List<Movement>> AllAsync(Masters m, string? exceptType, string? exceptName, CancellationToken ct)
    {
        var all = new List<Movement>();
        foreach (var type in Sources)
        {
            IReadOnlyList<FilterClause> filter = type is "SalesInvoice" or "SalesCreditNote" or "PurchaseInvoice" ? [new FilterClause("update_stock", "=", true)] : [];
            foreach (var doc in await m.QueryAsync(type, filter, ct, take: 200_000))
                if (doc.DocStatus == DocStatus.Submitted && !(doc.DocType == exceptType && doc.Name == exceptName))
                    all.AddRange(Extract(doc));
        }
        return all.Where(x => x.Item != "" && x.Warehouse != "").OrderBy(x => x.Date).ThenBy(x => x.Created).ToList();
    }

    public static Dictionary<(string Item, string Wh), Bin> Bins(IEnumerable<Movement> moves, DateOnly? asOf = null)
    {
        var bins = new Dictionary<(string, string), Bin>();
        foreach (var x in moves.Where(x => asOf is null || x.Date <= asOf))
        {
            if (!bins.TryGetValue((x.Item, x.Warehouse), out var b)) bins[(x.Item, x.Warehouse)] = b = new Bin();
            b.Qty += x.Qty; b.Value += x.Value;
            if (x.Date > b.LastDate) b.LastDate = x.Date;
        }
        return bins;
    }

    public static async Task<IDictionary<string, object?>> SettingsAsync(Masters m, CancellationToken ct) => await m.SingleAsync("StockSettings", ct);

    public static async Task<string> InventoryAccountAsync(Masters m, string warehouse, CancellationToken ct) =>
        await m.StrAsync("Warehouse", warehouse, "inventory_account", ct)
        ?? (await SettingsAsync(m, ct)).Str("inventory_account")
        ?? throw Errors.One("name", $"Set the inventory account on warehouse {warehouse} or in Stock Settings");

    public static async Task<string> SettingAccountAsync(Masters m, string field, string label, CancellationToken ct) =>
        (await SettingsAsync(m, ct)).Str(field) ?? throw Errors.One("name", $"Set '{label}' in Stock Settings");

    public static async Task<Document> StockItemAsync(Masters m, string? item, string path, CancellationToken ct)
    {
        var doc = await m.RequireAsync("Item", item, path, "Item", ct);
        if (!doc.Data.Bool("is_stock_item")) throw Errors.One(path, $"{doc.Name} is not a stock item (tick 'Maintain stock' on the item)");
        return doc;
    }

    /// <summary>Batal hanya bila tidak ada mutasi item-gudang yang sama sesudah dokumen ini.</summary>
    public static async Task CheckCancelAsync(Masters m, Document doc, CancellationToken ct)
    {
        var mine = Extract(doc).Select(x => (x.Item, x.Warehouse)).ToHashSet();
        if (mine.Count == 0) return;
        var date = DateOf(doc);
        var later = (await AllAsync(m, doc.DocType, doc.Name, ct))
            .FirstOrDefault(x => mine.Contains((x.Item, x.Warehouse)) && (x.Date > date || (x.Date == date && x.Created > doc.Created)));
        if (later is not null)
            throw Errors.One("name", $"{later.DocType} {later.DocName} moved {later.Item} in {later.Warehouse} after this document; cancel it first");
    }
}

/// <summary>Perencana valuasi untuk satu dokumen yang sedang di-submit: cek stok cukup & tanggal mundur, hitung nilai keluar rata-rata bergerak.</summary>
internal sealed class StockPlan
{
    private readonly Dictionary<(string, string), Bin> _bins;
    private readonly DateOnly _date;
    private readonly bool _allowNegative;
    public Errors Errors { get; } = new();

    private StockPlan(Dictionary<(string, string), Bin> bins, DateOnly date, bool allowNegative) { _bins = bins; _date = date; _allowNegative = allowNegative; }

    public static async Task<StockPlan> CreateAsync(Masters m, Document doc, CancellationToken ct) =>
        new(Stock.Bins(await Stock.AllAsync(m, doc.DocType, doc.Name, ct)), Stock.DateOf(doc), (await Stock.SettingsAsync(m, ct)).Bool("allow_negative_stock"));

    public Bin Get(string item, string wh) => _bins.TryGetValue((item, wh), out var b) ? b : _bins[(item, wh)] = new Bin();

    private bool Backdated(string item, string wh, string path)
    {
        var b = Get(item, wh);
        if (b.LastDate <= _date) return false;
        Errors.Add(path, $"{item} in {wh} already has stock movements on {D.Iso(b.LastDate)}; stock postings before that date are not allowed");
        return true;
    }

    /// <summary>Keluarkan qty; nilai = qty × rata-rata (saldo habis = seluruh nilai sisa). Mengembalikan nilai positif.</summary>
    public decimal Out(string item, string wh, decimal qty, string path)
    {
        if (qty <= 0 || Backdated(item, wh, path)) return 0m;
        var b = Get(item, wh);
        if (qty > b.Qty && !_allowNegative) { Errors.Add(path, $"Only {b.Qty:0.###} of {item} available in {wh}"); return 0m; }
        var value = qty == b.Qty ? b.Value : D.R2(qty * b.Rate);
        b.Qty -= qty; b.Value -= value; b.LastDate = _date;
        return value;
    }

    public void In(string item, string wh, decimal qty, decimal value, string path)
    {
        if (Backdated(item, wh, path)) return;
        var b = Get(item, wh);
        b.Qty += qty; b.Value += value; b.LastDate = _date;
    }
}

/// <summary>Posting sederhana untuk stok: abaikan bila semua baris bersih nol (mis. transfer dalam akun persediaan yang sama).</summary>
internal static class StockPosting
{
    public static IReadOnlyList<LedgerLine> Build(Posting p)
    {
        try { return p.Build(); }
        catch (ValidationFailedException) { return []; }
    }
}

/// <summary>Stock Entry: penerimaan, pengeluaran, transfer. Submit: valuasi + jurnal persediaan.</summary>
public sealed class StockEntryHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<StockEntry>, IAfterSave<StockEntry>, IOnCancel<StockEntry>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var purpose = d.Str("purpose");
        var errors = new Errors();
        var rows = d.Rows("items");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var p = $"items[{i}]";
            var item = await Stock.StockItemAsync(m, r.Str("item"), $"{p}.item", ct);
            r["item_name"] = item.Data.Str("item_name");
            r.SetIfEmpty("uom", item.Data.Str("uom"));
            var s = r.Str("s_warehouse") ?? d.Str("from_warehouse"); var t = r.Str("t_warehouse") ?? d.Str("to_warehouse");
            if (purpose != "Material Receipt" && s is null) errors.Add($"{p}.s_warehouse", "Set the source warehouse");
            if (purpose != "Material Issue" && t is null) errors.Add($"{p}.t_warehouse", "Set the target warehouse");
            if (purpose == "Material Transfer" && s == t) errors.Add($"{p}.t_warehouse", "Source and target warehouses must differ");
            if (purpose == "Material Receipt" && r.Dec("basic_rate") <= 0) errors.Add($"{p}.basic_rate", "Enter the cost per unit for a receipt");
        }
        errors.ThrowIfAny();
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var purpose = d.Str("purpose");
        var date = Stock.DateOf(c.Doc);
        await Periods.CheckAsync(m, c.User, date, null, "posting_date", ct);
        var plan = await StockPlan.CreateAsync(m, c.Doc, ct);
        var offset = d.Str("offset_account");
        var posting = new Posting(date, d.Str("remarks") ?? $"{purpose} {c.Doc.Name}");
        var rows = d.Rows("items");
        decimal total = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var p = $"items[{i}]";
            var item = r.Str("item")!; var qty = r.Dec("qty");
            var s = purpose == "Material Receipt" ? null : r.Str("s_warehouse") ?? d.Str("from_warehouse");
            var t = purpose == "Material Issue" ? null : r.Str("t_warehouse") ?? d.Str("to_warehouse");
            var cc = r.Str("cost_center") ?? d.Str("cost_center");
            var value = s is not null ? plan.Out(item, s, qty, $"{p}.qty") : D.R2(qty * r.Dec("basic_rate"));
            if (t is not null) plan.In(item, t, qty, value, $"{p}.qty");
            r["valuation_rate"] = qty > 0 ? Math.Round(value / qty, 4) : 0m;
            r["amount"] = value;
            total += value;
            var other = r.Str("expense_account") ?? offset ?? await Stock.SettingAccountAsync(m, "stock_adjustment_account", "Stock adjustment", ct);
            if (t is not null) posting.Dr(await Stock.InventoryAccountAsync(m, t, ct), value, costCenter: cc);
            else posting.Dr(other, value, costCenter: cc);
            if (s is not null) posting.Cr(await Stock.InventoryAccountAsync(m, s, ct), value, costCenter: cc);
            else posting.Cr(other, value, costCenter: cc);
        }
        plan.Errors.ThrowIfAny();
        d["total_value"] = total;
        var lines = StockPosting.Build(posting);
        if (purpose == "Material Issue") await BudgetControl.CheckAsync(c, m, ledger, lines, d.Str("company"), ct);
        c.AddLedgers(lines);
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        await Stock.CheckCancelAsync(m, c.Doc, ct);
        await Reversal.ApplyAsync(c, ledger, m, clock, ct);
    }
}

/// <summary>Stock Opname: qty & nilai sistem per item (dihitung ulang tiap simpan), selisih ke hitungan fisik; submit membukukan selisih nilai.</summary>
public sealed class StockReconciliationHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<StockReconciliation>, IAfterSave<StockReconciliation>, IOnCancel<StockReconciliation>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var wh = d.Str("warehouse");
        if (wh is null) return;
        var bins = Stock.Bins(await Stock.AllAsync(m, c.Doc.DocType, c.Doc.Name, ct));
        var rows = d.Rows("items");
        var errors = new Errors();
        foreach (var g in rows.GroupBy(r => r.Str("item")).Where(g => g.Key is not null && g.Count() > 1)) errors.Add("items", $"{g.Key} is listed more than once");
        decimal total = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var item = await Stock.StockItemAsync(m, r.Str("item"), $"items[{i}].item", ct);
            var b = bins.GetValueOrDefault((item.Name, wh)) ?? new Bin();
            var rate = r.Dec("new_rate") > 0 ? r.Dec("new_rate") : b.Rate;
            var newValue = D.R2(r.Dec("counted_qty") * rate);
            r["item_name"] = item.Data.Str("item_name");
            r["current_qty"] = b.Qty; r["current_rate"] = Math.Round(b.Rate, 4);
            r["qty_difference"] = r.Dec("counted_qty") - b.Qty;
            r["amount_difference"] = newValue - b.Value;
            total += newValue - b.Value;
            if (r.Dec("counted_qty") > 0 && rate <= 0) errors.Add($"items[{i}].new_rate", $"{item.Name} has no valuation rate yet; enter the cost per unit");
        }
        d["total_difference"] = total;
        errors.ThrowIfAny();
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var wh = d.Str("warehouse")!;
        var date = Stock.DateOf(c.Doc);
        await Periods.CheckAsync(m, c.User, date, null, "posting_date", ct);
        var plan = await StockPlan.CreateAsync(m, c.Doc, ct);
        var rows = d.Rows("items");
        for (var i = 0; i < rows.Count; i++) plan.In(rows[i].Str("item")!, wh, 0, 0, $"items[{i}].item"); // hanya cek tanggal mundur
        plan.Errors.ThrowIfAny();
        var total = rows.Sum(r => r.Dec("amount_difference"));
        if (total == 0) return;
        var inventory = await Stock.InventoryAccountAsync(m, wh, ct);
        var diffAccount = d.Str("difference_account") ?? await Stock.SettingAccountAsync(m, "stock_adjustment_account", "Stock adjustment", ct);
        var posting = new Posting(date, d.Str("remarks") ?? $"Stock count {wh} {c.Doc.Name}");
        posting.Dr(inventory, total); posting.Cr(diffAccount, total); // negatif otomatis dibalik oleh Posting
        c.AddLedgers(StockPosting.Build(posting));
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        await Stock.CheckCancelAsync(m, c.Doc, ct);
        await Reversal.ApplyAsync(c, ledger, m, clock, ct);
    }
}

/// <summary>Aksi "Fetch items" stock opname: semua item yang punya saldo di gudang, qty hitung = qty sistem.</summary>
public sealed class StockReconciliationFetchAction(IDocumentStore store) : IFormAction<StockReconciliation>
{
    public string ActionId => "erp.stockopname.fetch";
    public string Label => "Fetch items";
    public string Icon => "list";

    public bool IsVisible(UserContext user, Document doc) => doc.DocStatus == DocStatus.Draft;

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var wh = ctx.Doc.Data.Str("warehouse");
        if (wh is null) return FormActionResult.Fail("Choose the warehouse first");
        var bins = Stock.Bins(await Stock.AllAsync(new Masters(store, ctx.Doc.TenantId), ctx.Doc.DocType, ctx.Doc.Name, ct));
        var rows = bins.Where(kv => kv.Key.Wh == wh && kv.Value.Qty != 0).OrderBy(kv => kv.Key.Item, StringComparer.Ordinal)
            .Select(kv => new Dictionary<string, object?> { ["item"] = kv.Key.Item, ["counted_qty"] = kv.Value.Qty }).ToList();
        await ctx.Docs.UpdateAsync(ctx.User, ctx.Doc.DocType, ctx.Doc.Name, ctx.Doc.Version, new Dictionary<string, object?> { ["items"] = rows }, ct);
        return FormActionResult.Ok($"{rows.Count} items loaded; enter the counted quantities");
    }
}

/// <summary>Goods Receipt untuk item stok: masuk gudang pada harga netto PO (dikonversi ke mata uang fungsional); Dr persediaan / Cr barang diterima belum ditagih.</summary>
public sealed class GoodsReceiptStockHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<GoodsReceipt>, IAfterSave<GoodsReceipt>, IOnCancel<GoodsReceipt>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        if (await m.GetAsync("PurchaseOrder", d.Str("purchase_order"), ct) is not { } po) return;
        var lines = po.Data.Rows("items");
        var anyStock = false;
        foreach (var r in d.Rows("items"))
        {
            var line = (int)r.Dec("po_line");
            if (line < 1 || line > lines.Count) continue;
            var itemName = lines[line - 1].Str("item");
            var item = await m.GetAsync("Item", itemName, ct);
            r["item"] = itemName;
            r["is_stock_item"] = item?.Data.Bool("is_stock_item") == true;
            anyStock |= r.Bool("is_stock_item");
            if (anyStock && d.Str("warehouse") is null) d.SetIfEmpty("warehouse", item?.Data.Str("default_warehouse"));
        }
        if (anyStock) d.SetIfEmpty("warehouse", (await Stock.SettingsAsync(m, ct)).Str("default_warehouse"));
        if (anyStock && d.Str("warehouse") is null) throw Errors.One("warehouse", "Choose the warehouse that receives the stock items");
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var stockRows = d.Rows("items").Where(r => r.Bool("is_stock_item")).ToList();
        if (stockRows.Count == 0) return;
        var po = await m.RequireAsync("PurchaseOrder", d.Str("purchase_order"), "purchase_order", "Purchase order", ct);
        var date = Stock.DateOf(c.Doc);
        await Periods.CheckAsync(m, c.User, date, Periods.LockPayable, "receipt_date", ct);
        var company = await CompanyInfo.LoadAsync(m, po.Data.Str("company"), ct);
        var currency = po.Data.Str("currency") ?? company.BaseCurrency;
        var type = (await m.SingleAsync("AccountingSettings", ct)).Str("default_exchange_rate_type") ?? "BI Middle Rate";
        var fx = await Rates.GetAsync(m, currency, company.BaseCurrency, date, type, ct);
        var lines = po.Data.Rows("items");
        var wh = d.Str("warehouse")!;
        var plan = await StockPlan.CreateAsync(m, c.Doc, ct);
        decimal total = 0;
        foreach (var r in stockRows)
        {
            var line = lines[(int)r.Dec("po_line") - 1];
            var rate = Math.Round(Procure.UnitNet(line) * fx, 4);
            var value = D.R2(r.Dec("received_qty") * rate);
            r["valuation_rate"] = rate; r["amount"] = value;
            plan.In(r.Str("item")!, wh, r.Dec("received_qty"), value, "items");
            total += value;
        }
        plan.Errors.ThrowIfAny();
        var posting = new Posting(date, $"Goods receipt {c.Doc.Name} ({po.Name})");
        posting.Dr(await Stock.InventoryAccountAsync(m, wh, ct), total);
        posting.Cr(await Stock.SettingAccountAsync(m, "grni_account", "Stock received not billed", ct), total);
        c.AddLedgers(StockPosting.Build(posting));
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        await Stock.CheckCancelAsync(m, c.Doc, ct);
        await Reversal.ApplyAsync(c, ledger, m, clock, ct);
    }
}

/// <summary>
/// Faktur & nota penjualan dengan Update stock: barang keluar (faktur) / kembali (nota) pada rata-rata bergerak gudang;
/// Dr HPP / Cr persediaan (nota: kebalikannya). Pembalikan saat batal ikut jurnal faktur (Reversal hook faktur).
/// </summary>
public sealed class SalesStockHook(IDocumentStore store) : IBeforeValidate<SalesInvoice>, IAfterSave<SalesInvoice>, IOnCancel<SalesInvoice>,
    IBeforeValidate<SalesCreditNote>, IAfterSave<SalesCreditNote>, IOnCancel<SalesCreditNote>
{
    private async Task PrepareAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        if (!d.Bool("update_stock")) { foreach (var r in d.Rows("items")) { r["valuation_rate"] = null; r["stock_amount"] = null; } return; }
        var m = new Masters(store, c.Doc.TenantId);
        d.SetIfEmpty("warehouse", (await Stock.SettingsAsync(m, ct)).Str("default_warehouse"));
        if (d.Str("warehouse") is null) throw Errors.One("warehouse", "Choose the warehouse for 'Update stock'");
    }

    private async Task PostAsync(HookContext c, bool isReturn, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft || !c.Doc.Data.Bool("update_stock")) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var wh = d.Str("warehouse")!;
        var plan = await StockPlan.CreateAsync(m, c.Doc, ct);
        var original = isReturn ? await m.GetAsync("SalesInvoice", d.Str("return_against"), ct) : null;
        var cogs = await Stock.SettingAccountAsync(m, "cogs_account", "Cost of goods sold", ct);
        var inventory = await Stock.InventoryAccountAsync(m, wh, ct);
        var posting = new Posting(Stock.DateOf(c.Doc), $"Cost of goods {(isReturn ? "returned" : "sold")} {c.Doc.Name}");
        var rows = d.Rows("items");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var item = await m.GetAsync("Item", r.Str("item"), ct);
            if (item?.Data.Bool("is_stock_item") != true) { r["valuation_rate"] = null; r["stock_amount"] = null; continue; }
            var qty = r.Dec("qty");
            decimal value;
            if (!isReturn) value = plan.Out(item.Name, wh, qty, $"items[{i}].qty");
            else
            {
                // Retur dinilai dengan HPP faktur asal bila ada, selain itu rata-rata gudang saat ini.
                var origRate = original?.Data.Rows("items").FirstOrDefault(x => x.Str("item") == item.Name && Stock.HasStock(x))?.Dec("valuation_rate");
                value = D.R2(qty * (origRate is > 0m ? origRate.Value : plan.Get(item.Name, wh).Rate));
                plan.In(item.Name, wh, qty, value, $"items[{i}].qty");
            }
            r["valuation_rate"] = qty > 0 ? Math.Round(value / qty, 4) : 0m;
            r["stock_amount"] = value;
            var cc = r.Str("cost_center");
            if (!isReturn) { posting.Dr(cogs, value, costCenter: cc); posting.Cr(inventory, value, costCenter: cc); }
            else { posting.Dr(inventory, value, costCenter: cc); posting.Cr(cogs, value, costCenter: cc); }
        }
        plan.Errors.ThrowIfAny();
        c.AddLedgers(StockPosting.Build(posting));
    }

    Task IBeforeValidate<SalesInvoice>.BeforeValidateAsync(HookContext c, CancellationToken ct) => PrepareAsync(c, ct);
    Task IAfterSave<SalesInvoice>.AfterSaveAsync(HookContext c, CancellationToken ct) => PostAsync(c, false, ct);
    Task IOnCancel<SalesInvoice>.OnCancelAsync(HookContext c, CancellationToken ct) => Stock.CheckCancelAsync(new Masters(store, c.Doc.TenantId), c.Doc, ct);
    Task IBeforeValidate<SalesCreditNote>.BeforeValidateAsync(HookContext c, CancellationToken ct) => PrepareAsync(c, ct);
    Task IAfterSave<SalesCreditNote>.AfterSaveAsync(HookContext c, CancellationToken ct) => PostAsync(c, true, ct);
    Task IOnCancel<SalesCreditNote>.OnCancelAsync(HookContext c, CancellationToken ct) => Stock.CheckCancelAsync(new Masters(store, c.Doc.TenantId), c.Doc, ct);
}

/// <summary>
/// Faktur pembelian & item stok. Update stock: baris stok dibukukan ke akun persediaan gudang (barang masuk pada nilai baris × kurs).
/// Dengan PO tanpa Update stock: baris stok ke akun barang diterima belum ditagih (penerimaannya lewat Goods Receipt).
/// Nota debit (retur ke pemasok) tidak mengubah stok: keluarkan barang lewat Stock Entry Material Issue.
/// Didaftarkan sebelum PurchaseInvoiceHook agar akun baris sudah terisi saat hook faktur melengkapi akun lainnya.
/// </summary>
public sealed class PurchaseStockHook(IDocumentStore store) : IBeforeValidate<PurchaseInvoice>, IAfterSave<PurchaseInvoice>, IOnCancel<PurchaseInvoice>,
    IBeforeValidate<PurchaseDebitNote>
{
    async Task IBeforeValidate<PurchaseInvoice>.BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var update = d.Bool("update_stock");
        if (update)
        {
            d.SetIfEmpty("warehouse", (await Stock.SettingsAsync(m, ct)).Str("default_warehouse"));
            if (d.Str("warehouse") is null) throw Errors.One("warehouse", "Choose the warehouse for 'Update stock'");
            if (d.Str("purchase_order") is { } po && (await m.QueryAsync("GoodsReceipt", [new FilterClause("purchase_order", "=", po)], ct, take: 50))
                    .Any(g => g.DocStatus == DocStatus.Submitted && g.Data.Rows("items").Any(r => r.Bool("is_stock_item"))))
                throw Errors.One("update_stock", $"{po} is already received with goods receipts; untick 'Update stock' so the invoice clears 'stock received not billed'");
        }
        var rows = d.Rows("items");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var item = await m.GetAsync("Item", r.Str("item"), ct);
            if (item?.Data.Bool("is_stock_item") != true) { r["valuation_rate"] = null; r["stock_amount"] = null; continue; }
            if (update) r["expense_account"] = await Stock.InventoryAccountAsync(m, d.Str("warehouse")!, ct);
            else if (d.Str("purchase_order") is not null) r["expense_account"] = await Stock.SettingAccountAsync(m, "grni_account", "Stock received not billed", ct);
            else c.Messages.Add($"{item.Name} is a stock item but this invoice does not update stock; it is booked to {r.Str("expense_account") ?? "the expense account"}");
        }
    }

    async Task IAfterSave<PurchaseInvoice>.AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft || !c.Doc.Data.Bool("update_stock")) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var wh = d.Str("warehouse")!;
        var fx = d.Dec("exchange_rate") is > 0m and var x ? x : 1m;
        var plan = await StockPlan.CreateAsync(m, c.Doc, ct);
        var rows = d.Rows("items");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if ((await m.GetAsync("Item", r.Str("item"), ct))?.Data.Bool("is_stock_item") != true) continue;
            var value = D.R2(r.Dec("amount") * fx); // sama dengan nilai yang didebit ke akun persediaan oleh jurnal faktur
            r["stock_amount"] = value;
            r["valuation_rate"] = r.Dec("qty") > 0 ? Math.Round(value / r.Dec("qty"), 4) : 0m;
            plan.In(r.Str("item")!, wh, r.Dec("qty"), value, $"items[{i}].qty");
        }
        plan.Errors.ThrowIfAny();
    }

    Task IOnCancel<PurchaseInvoice>.OnCancelAsync(HookContext c, CancellationToken ct) => Stock.CheckCancelAsync(new Masters(store, c.Doc.TenantId), c.Doc, ct);

    Task IBeforeValidate<PurchaseDebitNote>.BeforeValidateAsync(HookContext c, CancellationToken ct) =>
        c.Doc.Data.Bool("update_stock")
            ? throw Errors.One("update_stock", "Debit notes do not move stock; issue the returned goods with a Material Issue stock entry")
            : Task.CompletedTask;
}
