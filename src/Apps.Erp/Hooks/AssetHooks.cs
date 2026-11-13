using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

/// <summary>
/// Buku aset. Aset yang sudah di-submit tidak bisa diubah, jadi "sudah disusutkan" tidak disimpan di Asset:
/// sumbernya baris DepreciationRun yang sudah di-submit (dan AssetDisposal untuk status dilepas).
/// </summary>
internal static class AssetBook
{
    public sealed record Posted(string Asset, string ScheduleDate, decimal Amount, string Run);

    public static async Task<List<Posted>> PostedAsync(Masters m, string? company, CancellationToken ct)
    {
        var filters = company is null ? new List<FilterClause>() : [new FilterClause("company", "=", company)];
        var runs = await m.QueryAsync("DepreciationRun", filters, ct, take: 5000);
        return runs.Where(r => r.DocStatus == DocStatus.Submitted)
            .SelectMany(r => r.Data.Rows("items").Select(i => new Posted(i.Str("asset") ?? "", i.Str("schedule_date") ?? "", i.Dec("depreciation_amount"), r.Name)))
            .ToList();
    }

    public static async Task<Document?> DisposalAsync(Masters m, string asset, CancellationToken ct) =>
        (await m.QueryAsync("AssetDisposal", [new FilterClause("asset", "=", asset)], ct, take: 20))
            .FirstOrDefault(x => x.DocStatus == DocStatus.Submitted);

    public static DateOnly MonthEnd(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
    public static int MonthsBetween(DateOnly a, DateOnly b) => (b.Year - a.Year) * 12 + b.Month - a.Month;

    /// <summary>
    /// Jadwal bulanan (akhir bulan) mulai bulan depreciation_start_date. Garis Lurus: rata, sisa pembulatan di bulan terakhir.
    /// Saldo Menurun: saldo menurun ganda bulanan (2/umur) dengan pindah ke garis lurus saat lebih besar; habis tepat di nilai sisa.
    /// </summary>
    public static List<Dictionary<string, object?>> Schedule(string method, int remainingMonths, int lifeMonths, DateOnly start,
        decimal gross, decimal salvage, decimal opening)
    {
        var rows = new List<Dictionary<string, object?>>();
        var depreciable = gross - salvage - opening;
        if (remainingMonths <= 0 || depreciable <= 0) return rows;
        var nbv = gross - opening;
        var accumulated = opening;
        var date = MonthEnd(start);
        var rate = 2m / lifeMonths;
        for (var k = 0; k < remainingMonths; k++)
        {
            var left = remainingMonths - k;
            decimal amount;
            if (k == remainingMonths - 1) amount = nbv - salvage;
            else if (method == "Declining Balance")
                amount = Math.Min(Math.Max(D.R2(nbv * rate), D.R2((nbv - salvage) / left)), nbv - salvage);
            else amount = D.R2(depreciable / remainingMonths);
            nbv -= amount; accumulated += amount;
            rows.Add(new()
            {
                ["schedule_date"] = D.Iso(date), ["depreciation_amount"] = amount, ["accumulated_depreciation"] = accumulated,
                ["depreciation_run"] = null, ["is_posted"] = false,
            });
            date = MonthEnd(date.AddDays(1));
        }
        return rows;
    }

    public sealed record AssetAccounts(string FixedAsset, string Accumulated, string Expense, string GainLoss);

    public static async Task<AssetAccounts> AccountsAsync(Masters m, string? category, string path, CancellationToken ct)
    {
        var cat = await m.RequireAsync("AssetCategory", category, path, "Asset category", ct);
        string Need(string f, string label) => cat.Data.Str(f) ?? throw Errors.One(path, $"{label} is not set on category {cat.Name}");
        return new(Need("fixed_asset_account", "Fixed asset account"), Need("accumulated_depreciation_account", "Accumulated depreciation account"),
            Need("depreciation_expense_account", "Depreciation expense account"), Need("disposal_gain_loss_account", "Gain/loss on disposal account"));
    }
}

public sealed class AssetHook(IDocumentStore store)
    : IBeforeValidate<Asset>, IAfterSave<Asset>, IOnCancel<Asset>, IOnLoad<Asset>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var cat = await m.RequireAsync("AssetCategory", d.Str("asset_category"), "asset_category", "Asset category", ct);
        var life = (int)cat.Data.Dec("useful_life_months");
        var method = cat.Data.Str("depreciation_method") ?? "Straight Line";
        var gross = d.Dec("gross_purchase_amount"); var salvage = d.Dec("salvage_value");
        var opening = d.Bool("is_existing_asset") ? d.Dec("opening_accumulated_depreciation") : 0m;
        if (!d.Bool("is_existing_asset")) d["opening_accumulated_depreciation"] = 0m;
        var available = d.Date("available_for_use_date"); var start = d.Date("depreciation_start_date");
        if (available is null || start is null || life <= 0) return; // wajib diisi dicek metadata

        var used = AssetBook.MonthsBetween(available.Value, start.Value);
        var remaining = life - Math.Max(0, used);
        if (remaining <= 0) errors.Add("depreciation_start_date", $"The useful life of {life} months has ended before the depreciation start date");
        if (gross - salvage - opening < 0) errors.Add("opening_accumulated_depreciation", "Opening accumulated depreciation exceeds the depreciable amount");
        if (d.Str("purchase_invoice") is { } pi && (await m.GetAsync("PurchaseInvoice", pi, ct))?.DocStatus != DocStatus.Submitted)
            errors.Add("purchase_invoice", $"{pi} is not submitted");
        errors.ThrowIfAny();
        d["schedule"] = AssetBook.Schedule(method, remaining, life, start.Value, gross, salvage, opening);
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft)
        {
            if (c.Doc.Data.Rows("schedule").Count == 0) throw Errors.One("schedule", "The depreciation schedule is empty");
            c.Doc.Data["status"] = "Active";
            if (c.Doc.Data.Str("purchase_invoice") is null && !c.Doc.Data.Bool("is_existing_asset"))
                c.Messages.Add("This asset is not linked to a purchase invoice; make sure its acquisition has been booked to the fixed asset account.");
        }
        return Task.CompletedTask;
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var posted = (await AssetBook.PostedAsync(new Masters(store, c.Doc.TenantId), c.Doc.Data.Str("company"), ct)).Where(p => p.Asset == c.Doc.Name).ToList();
        if (posted.Count > 0) throw Errors.One("name", $"The asset has been depreciated by {posted[0].Run}; cancel that depreciation run first");
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted) return;
        var m = new Masters(store, c.Doc.TenantId);
        var posted = (await AssetBook.PostedAsync(m, c.Doc.Data.Str("company"), ct)).Where(p => p.Asset == c.Doc.Name)
            .GroupBy(p => p.ScheduleDate).ToDictionary(g => g.Key, g => g.First().Run);
        var rows = c.Doc.Data.Rows("schedule");
        foreach (var r in rows)
            if (r.Str("schedule_date") is { } date && posted.TryGetValue(date, out var run)) { r["is_posted"] = true; r["depreciation_run"] = run; }
        c.Doc.Data["status"] = await AssetBook.DisposalAsync(m, c.Doc.Name, ct) is not null ? "Disposed"
            : rows.Count > 0 && rows.All(r => r.Bool("is_posted")) ? "Fully Depreciated" : "Active";
    }
}

/// <summary>Satu dokumen per periode: memposting semua jadwal penyusutan yang jatuh tempo dan belum diposting.</summary>
public sealed class DepreciationRunHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<DepreciationRun>, IAfterSave<DepreciationRun>, IOnCancel<DepreciationRun>
{
    private static async Task<List<Dictionary<string, object?>>> DueAsync(Masters m, string company, DateOnly until, string self, CancellationToken ct)
    {
        var posted = (await AssetBook.PostedAsync(m, company, ct)).Where(p => p.Run != self).Select(p => (p.Asset, p.ScheduleDate)).ToHashSet();
        var result = new List<Dictionary<string, object?>>();
        foreach (var a in (await m.QueryAsync("Asset", [new FilterClause("company", "=", company)], ct, take: 10000)).Where(a => a.DocStatus == DocStatus.Submitted))
        {
            if (await AssetBook.DisposalAsync(m, a.Name, ct) is not null) continue;
            foreach (var s in a.Data.Rows("schedule"))
            {
                var date = s.Str("schedule_date");
                if (date is null || s.Date("schedule_date") > until || posted.Contains((a.Name, date))) continue;
                result.Add(new()
                {
                    ["asset"] = a.Name, ["asset_name"] = a.Data.Str("asset_name"), ["schedule_date"] = date,
                    ["cost_center"] = a.Data.Str("cost_center"), ["depreciation_amount"] = s.Dec("depreciation_amount"),
                });
            }
        }
        return result;
    }

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var company = (await CompanyInfo.LoadAsync(m, d.Str("company"), ct)).Name;
        var period = await m.RequireAsync("AccountingPeriod", d.Str("accounting_period"), "accounting_period", "Period", ct);
        d.SetIfEmpty("posting_date", period.Data.Str("end_date"));
        var date = d.Date("posting_date")!.Value;
        if (d.Rows("items").Count == 0) d["items"] = await DueAsync(m, company, date, c.Doc.Name, ct);
        if (d.Rows("items").Count == 0) throw Errors.One("items", $"No depreciation is due up to {D.Iso(date)}");
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var date = d.Date("posting_date")!.Value;
        await Periods.CheckAsync(m, c.User, date, Periods.LockFixedAsset, "posting_date", ct);
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var posted = (await AssetBook.PostedAsync(m, company.Name, ct)).Where(p => p.Run != c.Doc.Name).Select(p => (p.Asset, p.ScheduleDate)).ToHashSet();

        var posting = new Posting(date, $"Depreciation {d.Str("accounting_period")}");
        var errors = new Errors();
        var items = d.Rows("items");
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var asset = await m.GetAsync("Asset", it.Str("asset"), ct);
            if (asset is null || asset.DocStatus != DocStatus.Submitted) { errors.Add($"items[{i}].asset", $"Asset {it.Str("asset")} is not active"); continue; }
            if (posted.Contains((asset.Name, it.Str("schedule_date") ?? ""))) { errors.Add($"items[{i}].asset", $"Depreciation of {asset.Name} for {it.Str("schedule_date")} is already posted"); continue; }
            var acc = await AssetBook.AccountsAsync(m, asset.Data.Str("asset_category"), $"items[{i}].asset", ct);
            var cc = it.Str("cost_center") ?? asset.Data.Str("cost_center");
            posting.Dr(acc.Expense, it.Dec("depreciation_amount"), costCenter: cc, lineRemarks: $"Depreciation {asset.Name}");
            posting.Cr(acc.Accumulated, it.Dec("depreciation_amount"), costCenter: cc, lineRemarks: $"Depreciation {asset.Name}");
        }
        errors.ThrowIfAny();
        c.Ledger.AddRange(posting.Build());
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
}

/// <summary>
/// Pelepasan aset: Dr akumulasi + Dr laba/rugi pelepasan (nilai buku) / Cr harga perolehan.
/// Hasil penjualan dicatat lewat Faktur Penjualan dengan akun pendapatan = akun laba/rugi pelepasan kategori,
/// sehingga saldo akun itu = harga jual − nilai buku.
/// </summary>
public sealed class AssetDisposalHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<AssetDisposal>, IAfterSave<AssetDisposal>, IOnCancel<AssetDisposal>
{
    private static async Task<(Document Asset, decimal Accumulated, decimal BookValue)> ValueAsync(Masters m, HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var asset = await m.RequireAsync("Asset", d.Str("asset"), "asset", "Asset", ct);
        if (asset.DocStatus != DocStatus.Submitted) throw Errors.One("asset", $"Asset {asset.Name} is not active");
        if (await AssetBook.DisposalAsync(m, asset.Name, ct) is { } other && other.Name != c.Doc.Name)
            throw Errors.One("asset", $"Asset {asset.Name} was already disposed by {other.Name}");
        var date = d.Date("disposal_date") ?? throw Errors.One("disposal_date", "The disposal date is required");
        var posted = (await AssetBook.PostedAsync(m, asset.Data.Str("company"), ct)).Where(p => p.Asset == asset.Name).ToList();
        var postedDates = posted.Select(p => p.ScheduleDate).ToHashSet();
        var missing = asset.Data.Rows("schedule").FirstOrDefault(s => s.Date("schedule_date") <= date && !postedDates.Contains(s.Str("schedule_date") ?? ""));
        if (missing is not null)
            throw Errors.One("disposal_date", $"Depreciation for {missing.Str("schedule_date")} is not posted; run a Depreciation Run up to the disposal date");
        var accumulated = asset.Data.Dec("opening_accumulated_depreciation") + posted.Sum(p => p.Amount);
        return (asset, accumulated, asset.Data.Dec("gross_purchase_amount") - accumulated);
    }

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var (_, _, bookValue) = await ValueAsync(new Masters(store, c.Doc.TenantId), c, ct);
        c.Doc.Data["book_value"] = bookValue;
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var (asset, accumulated, bookValue) = await ValueAsync(m, c, ct);
        var date = c.Doc.Data.Date("disposal_date")!.Value;
        await Periods.CheckAsync(m, c.User, date, Periods.LockFixedAsset, "disposal_date", ct);
        var acc = await AssetBook.AccountsAsync(m, asset.Data.Str("asset_category"), "asset", ct);
        var cc = asset.Data.Str("cost_center");
        c.Doc.Data["book_value"] = bookValue;

        var posting = new Posting(date, $"Disposal of asset {asset.Name} ({c.Doc.Data.Str("disposal_type")})");
        posting.Dr(acc.Accumulated, accumulated, costCenter: cc);
        posting.Dr(acc.GainLoss, bookValue, costCenter: cc);
        posting.Cr(acc.FixedAsset, asset.Data.Dec("gross_purchase_amount"), costCenter: cc);
        c.Ledger.AddRange(posting.Build());
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
}
