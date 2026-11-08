using Apps.Sample.Hooks;
using Framework.Actions;
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

sealed class HookDeleteProbe : IBeforeDelete<PurchaseRequest>, IAfterDelete<PurchaseRequest>
{
    public static readonly List<string> Calls = [];
    public static bool ShouldReject;
    public Task BeforeDeleteAsync(HookContext context, CancellationToken ct)
    {
        Calls.Add("before_delete");
        if (ShouldReject) throw new ValidationFailedException([new FieldError("name", "Cannot delete this document.")]);
        return Task.CompletedTask;
    }
    public Task AfterDeleteAsync(HookContext context, CancellationToken ct)
    {
        Calls.Add("after_delete");
        return Task.CompletedTask;
    }
}

sealed class HookCancelAmendProbe : IOnCancel<PurchaseRequest>, IOnAmend<PurchaseRequest>
{
    public static readonly List<string> Calls = [];
    public static string? AmendedDraftName;
    public Task OnCancelAsync(HookContext context, CancellationToken ct)
    {
        Calls.Add($"cancel:{context.Doc.Name}");
        return Task.CompletedTask;
    }
    public Task OnAmendAsync(HookContext context, Document amendedDraft, CancellationToken ct)
    {
        Calls.Add($"amend:{context.Doc.Name}->{amendedDraft.Name}");
        AmendedDraftName = amendedDraft.Name;
        return Task.CompletedTask;
    }
}

sealed class HookMessageAndFieldProbe : IBeforeInsert<PurchaseRequest>
{
    public static object? FetchedFieldValue;
    public async Task BeforeInsertAsync(HookContext context, CancellationToken ct)
    {
        context.Messages.Add("Notice: Item priority automatically evaluated.");
        FetchedFieldValue = await context.Docs.GetFieldAsync("Vendor", "VND-0001", "vendor_name", ct);
    }
}

sealed class DuplicatePurchaseRequestAction : IFormAction<PurchaseRequest>
{
    public string ActionId => "duplicate";
    public string Label => "Duplicate Request";
    public string Icon => "copy";
    public string Tone => "neutral";

    public bool IsVisible(UserContext user, Document doc) =>
        doc.DocStatus != DocStatus.Draft && user.Roles.Contains("Employee");

    public async Task<FormActionResult> ExecuteAsync(FormActionContext context, CancellationToken ct)
    {
        var cloneData = new Dictionary<string, object?>(context.Doc.Data);
        cloneData.Remove("name");
        var copy = await context.Docs.InsertAsync(context.User, context.Doc.DocType, cloneData, ct: ct);
        return FormActionResult.Ok($"Successfully duplicated to {copy.Name}");
    }
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

    [Test]
    public async Task Hook_BeforeDelete_dan_AfterDelete_dijalankan_saat_hapus_dokumen()
    {
        HookDeleteProbe.Calls.Clear();
        HookDeleteProbe.ShouldReject = true;
        await UseHookProviderAsync(s => s.AddDocHook<PurchaseRequest, HookDeleteProbe>());

        var doc = await Docs.InsertAsync(U("andi"), PR, P(("items", Items(1, 1000)), ("request_date", "2026-10-10"), ("required_by", "2026-10-11")));
        Assert.Equal(DocStatus.Draft, doc.DocStatus, "dokumen draft");

        // BeforeDelete rejects
        var err = await Assert.Throws<ValidationFailedException>(() => Docs.DeleteAsync(U("andi"), PR, doc.Name, doc.Version), "BeforeDelete reject");
        Assert.True(err.Errors.Any(e => e.Message.Contains("Cannot delete this document")), "pesan penolakan");
        Assert.Equal("before_delete", string.Join(',', HookDeleteProbe.Calls), "AfterDelete tidak dipanggil bila ditolak");

        // Allow deletion
        HookDeleteProbe.Calls.Clear();
        HookDeleteProbe.ShouldReject = false;
        await Docs.DeleteAsync(U("andi"), PR, doc.Name, doc.Version);
        Assert.Equal("before_delete,after_delete", string.Join(',', HookDeleteProbe.Calls), "BeforeDelete dan AfterDelete berhasil dipanggil");
    }

    [Test]
    public async Task Hook_OnCancel_dan_OnAmend_dijalankan_pada_siklus_pembatalan()
    {
        HookCancelAmendProbe.Calls.Clear();
        await UseHookProviderAsync(s => s.AddDocHook<PurchaseRequest, HookCancelAmendProbe>());

        var doc = await Docs.InsertAsync(U("andi"), PR, P(("items", Items(1, 500_000)), ("vendor", "VND-0001"), ("request_date", "2026-10-10"), ("required_by", "2026-10-11")));
        // Transition: Submit -> Reject (DocStatus = Cancelled)
        await Docs.RunActionAsync(U("andi"), PR, doc.Name, "Ajukan", 1, "ajukan pengajuan");
        await Docs.RunActionAsync(U("budi"), PR, doc.Name, "Tolak", 2, "batal / ditolak atasan");

        Assert.True(HookCancelAmendProbe.Calls.Contains($"cancel:{doc.Name}"), "OnCancel dipanggil saat dokumen dibatalkan");

        // Amend
        var amendDraft = await Docs.RunActionAsync(U("andi"), PR, doc.Name, DocActions.Amend, 3, null);
        Assert.True(HookCancelAmendProbe.Calls.Any(c => c.StartsWith($"amend:{doc.Name}->")), "OnAmend dipanggil saat amandemen");
        Assert.Equal(amendDraft.Name, HookCancelAmendProbe.AmendedDraftName, "nama draft amandemen sesuai");
    }

    [Test]
    public async Task Hook_GetFieldAsync_dan_Messages_berhasil_dijalankan()
    {
        await UseHookProviderAsync(s => s.AddDocHook<PurchaseRequest, HookMessageAndFieldProbe>());

        var doc = await Docs.InsertAsync(U("andi"), PR, P(("items", Items(1, 1000)), ("request_date", "2026-10-10"), ("required_by", "2026-10-11")));
        Assert.True(doc.TransientMessages.Contains("Notice: Item priority automatically evaluated."), "TransientMessages terisi dari hook");
        Assert.Equal("Pt Sinar Komputindo", HookMessageAndFieldProbe.FetchedFieldValue?.ToString(), "GetFieldAsync berhasil membaca vendor_name dari Vendor");
    }

    [Test]
    public async Task FormAction_kondisi_tampil_dan_eksekusi_berhasil()
    {
        await UseHookProviderAsync(s => s.AddFormAction<PurchaseRequest, DuplicatePurchaseRequestAction>());

        var actions = sp.GetServices<IFormAction>().ToList();
        var action = actions.OfType<DuplicatePurchaseRequestAction>().FirstOrDefault();
        Assert.True(action is not null, "action terdaftar di DI");

        // Draft document -> not visible
        var doc = await Docs.InsertAsync(U("andi"), PR, P(("items", Items(1, 1000)), ("vendor", "VND-0001"), ("request_date", "2026-10-10"), ("required_by", "2026-10-11")));
        Assert.True(!action!.IsVisible(U("andi"), doc), "Draft tidak menampilkan tombol duplikasi");

        // Submit -> visible
        await Docs.RunActionAsync(U("andi"), PR, doc.Name, "Ajukan", 1, "ajukan");
        var submitted = await Docs.GetAsync(U("andi"), PR, doc.Name);
        Assert.True(action.IsVisible(U("andi"), submitted), "Submitted menampilkan tombol duplikasi");

        // Execute action
        var ctx = new FormActionContext
        {
            User = U("andi"),
            Meta = sp.GetRequiredService<MetadataRegistry>().Current.Get(PR),
            Doc = submitted,
            Docs = Docs,
            Services = sp
        };
        var result = await action.ExecuteAsync(ctx, CancellationToken.None);
        Assert.True(result.Success, "eksekusi berhasil");
        Assert.True(result.Message?.Contains("Successfully duplicated") == true, "pesan hasil eksekusi");
    }
}
