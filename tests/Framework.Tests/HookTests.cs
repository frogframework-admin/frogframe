using Apps.Sample.Hooks;
using Framework.Documents;
using Framework.Hooks;
using Framework.Imports;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;

sealed class HookOrderProbe : IBeforeValidate<PurchaseRequest>, IBeforeInsert<PurchaseRequest>, IAfterSave<PurchaseRequest>, IOnLoad<PurchaseRequest>, IBeforeTransition<PurchaseRequest>
{
    public static readonly List<string> Calls = [];
    public static bool Enabled;
    public Task BeforeValidateAsync(HookContext context, CancellationToken ct) { if (Enabled) Calls.Add("validate"); return Task.CompletedTask; }
    public Task BeforeInsertAsync(HookContext context, CancellationToken ct) { if (Enabled) Calls.Add("insert"); return Task.CompletedTask; }
    public Task AfterSaveAsync(HookContext context, CancellationToken ct) { if (Enabled) Calls.Add("save"); return Task.CompletedTask; }
    public Task OnLoadAsync(HookContext context, CancellationToken ct) { if (Enabled) Calls.Add("load"); return Task.CompletedTask; }
    public Task BeforeTransitionAsync(HookContext context, CancellationToken ct) { if (Enabled) Calls.Add("transition"); return Task.CompletedTask; }
}

sealed class HookFailureProbe : IAfterSave<PurchaseRequest>
{
    public static bool Enabled;
    public Task AfterSaveAsync(HookContext context, CancellationToken ct) => Enabled && Equals(context.Doc.Data.GetValueOrDefault("justification"), "fail hook")
        ? throw new InvalidOperationException("hook failure") : Task.CompletedTask;
}

sealed class HookOutboxProbe : IAfterSave<PurchaseRequest>
{
    public static bool Enabled;
    public Task AfterSaveAsync(HookContext context, CancellationToken ct)
    {
        if (!Enabled) return Task.CompletedTask;
        context.Events.Add(new OutboxMessage { TenantId = context.User.TenantId, Type = "hook.test",
            IdempotencyKey = $"hook.test:{context.Doc.Name}", Payload = new() { ["name"] = context.Doc.Name }, CreatedAt = DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }
}

sealed class HookReadProbe : IOnLoad<PurchaseRequest>
{
    public static bool Enabled;
    public Task OnLoadAsync(HookContext context, CancellationToken ct) => Enabled && context.Doc.Name != "PR-2026-00001"
        ? context.Docs.GetAsync("PurchaseRequest", "PR-2026-00001", ct) : Task.CompletedTask;
}

sealed class HookCycleProbe : IAfterSave<PurchaseRequest>
{
    public static bool Enabled;
    public Task AfterSaveAsync(HookContext context, CancellationToken ct) => Enabled
        ? context.Docs.GetAsync("PurchaseRequest", context.Doc.Name, ct) : Task.CompletedTask;
}

sealed partial class Tests
{
    [Test]
    public async Task Hook_tahapan_berurutan_dan_OnLoad_hanya_pada_Get_tunggal()
    {
        HookOrderProbe.Enabled = false;
        await UseHookProviderAsync(s => s.AddDocHook<PurchaseRequest, HookOrderProbe>());
        HookOrderProbe.Calls.Clear();
        HookOrderProbe.Enabled = true;
        var doc = await Docs.InsertAsync(U("andi"), PR, P(("items", Items(1, 1000)), ("request_date", "2026-10-10"), ("required_by", "2026-10-11")));
        Assert.Equal("validate,insert,save", string.Join(',', HookOrderProbe.Calls), "urutan hook insert");
        HookOrderProbe.Calls.Clear();
        _ = await Docs.ListAsync(U("andi"), PR, new ListRequest(PageSize: 5));
        Assert.Equal(0, HookOrderProbe.Calls.Count, "List tidak memanggil OnLoad");
        _ = await Docs.GetAsync(U("andi"), PR, doc.Name);
        Assert.Equal("load", string.Join(',', HookOrderProbe.Calls), "Get tunggal memanggil OnLoad");
    }

    [Test]
    public async Task Hook_gagal_di_AfterSave_membatalkan_insert_dan_batch_atomik()
    {
        await UseHookProviderAsync(s => s.AddDocHook<PurchaseRequest, HookFailureProbe>());
        HookFailureProbe.Enabled = true;
        var before = await CountPR();
        await Assert.Throws<DocumentHookException>(() => Docs.InsertAsync(U("andi"), PR,
            P(("items", Items(1, 1000)), ("request_date", "2026-10-10"), ("required_by", "2026-10-11"), ("justification", "fail hook"))), "insert AfterSave");
        Assert.Equal(before, await CountPR(), "insert gagal tidak tersimpan");

        var rows = new IDictionary<string, object?>[]
        {
            P(("items", Items(1, 1000)), ("request_date", "2026-10-10"), ("required_by", "2026-10-11"), ("justification", "first succeeds")),
            P(("items", Items(1, 2000)), ("request_date", "2026-10-10"), ("required_by", "2026-10-11"), ("justification", "fail hook")),
        };
        var batch = await Assert.Throws<BatchRowException>(() => Docs.InsertBatchAsync(U("andi"), PR, rows, "batch"), "batch row failure");
        Assert.Equal(1, batch.Index, "baris gagal");
        Assert.Equal(before, await CountPR(), "batch gagal tidak menyimpan baris sebelumnya");
    }

    [Test]
    public async Task Hook_simulasi_import_tidak_menyimpan_dokumen_atau_outbox()
    {
        await UseHookProviderAsync(s => s.AddDocHook<PurchaseRequest, HookOutboxProbe>());
        HookOutboxProbe.Enabled = true;
        var before = await CountPR();
        var outbox = sp.GetRequiredService<IOutboxStore>().Snapshot().Count;
        var session = Imports.Open(U("andi"), PR, "hooks.xlsx", FilledPurchaseRequests(includeBadRows: false));
        var preview = await Imports.SimulateAsync(U("andi"), session);
        Assert.Equal(2, preview.Ok, "baris valid saat simulasi");
        Assert.Equal(before, await CountPR(), "simulasi tidak menyimpan dokumen");
        Assert.Equal(outbox, sp.GetRequiredService<IOutboxStore>().Snapshot().Count, "simulasi tidak menyimpan event");
    }

    [Test]
    public async Task Hook_membatasi_pembacaan_dan_mendeteksi_siklus_dokumen()
    {
        await UseHookProviderAsync(s => s.AddDocHook<PurchaseRequest, HookReadProbe>());
        HookReadProbe.Enabled = true;
        var sariDoc = (await Docs.QueryForReportAsync(U("sari"), PR, [], DocActions.Read)).First();
        var denied = await Assert.Throws<DocumentHookException>(() => Docs.GetAsync(U("sari"), PR, sariDoc.Name), "OnLoad membaca target yang tidak boleh diakses");
        Assert.True(denied.InnerException is DocumentNotFoundException, "OnLoad bukan bypass izin baca");

        HookReadProbe.Enabled = false;
        await UseHookProviderAsync(s => s.AddDocHook<PurchaseRequest, HookCycleProbe>());
        HookCycleProbe.Enabled = true;
        var before = await CountPR();
        await Assert.Throws<HookRecursionException>(() => Docs.InsertAsync(U("andi"), PR,
            P(("items", Items(1, 1000)), ("request_date", "2026-10-10"), ("required_by", "2026-10-11"))), "AfterSave membaca dokumen saat ini");
        Assert.Equal(before, await CountPR(), "siklus tidak melakukan commit");
    }
}
