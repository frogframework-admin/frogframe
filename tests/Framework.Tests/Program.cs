using System.Reflection;
using System.Text.Json;
using Apps.Sample.Domain;
using Framework.Documents;
using Framework.Expressions;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Framework.Printing;
using Framework.Web.Printing;
using Framework.Web.Admin;
using Framework.Files;
using Framework.Dashboards;
using Framework.Web.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Framework.Persistence.PostgreSql;
using System.Collections.Concurrent;
using Npgsql;

// Runner kecil: setiap method bertanda [Test] dijalankan pada aplikasi in-process baru (data seed segar).
// Jalankan: dotnet run --project tests/Framework.Tests  [filter-nama-test]
if (args.FirstOrDefault() == "--bench") return await Bench.RunAsync(args);

var tests = typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .Where(m => m.GetCustomAttribute<TestAttribute>() is not null).ToList();
var filter = args.FirstOrDefault();
int pass = 0, fail = 0;
Console.WriteLine(Tests.PgConnection is null
    ? "Penyimpanan: in-memory (set DOCFRAME_TEST_PG untuk menguji PostgreSQL)\n"
    : "Penyimpanan: PostgreSQL\n");
int skipped = 0;
foreach (var m in tests.Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
{
    if (m.GetCustomAttribute<ReportingTestAttribute>() is not null && Tests.PgReadOnly is null)
    {
        Console.WriteLine($"  – {m.Name.Replace('_', ' ')} (dilewati: butuh PostgreSQL + DOCFRAME_TEST_PG_RO)");
        skipped++;
        continue;
    }
    if (m.GetCustomAttribute<PgTestAttribute>() is not null && Tests.PgConnection is null)
    {
        Console.WriteLine($"  – {m.Name.Replace('_', ' ')} (dilewati: butuh PostgreSQL)");
        skipped++;
        continue;
    }
    try
    {
        var t = await Tests.CreateAsync();
        try { await (Task)m.Invoke(t, null)!; }
        finally { await t.DisposeAsync(); } // tutup pool koneksi milik provider test ini
        Console.WriteLine($"  ✓ {m.Name.Replace('_', ' ')}");
        pass++;
    }
    catch (Exception ex)
    {
        var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
        Console.WriteLine($"  ✗ {m.Name.Replace('_', ' ')}\n      {inner.GetType().Name}: {inner.Message}");
        fail++;
    }
}
Console.WriteLine($"\n{pass} lulus, {fail} gagal{(skipped > 0 ? $", {skipped} dilewati" : "")}");
return fail == 0 ? 0 : 1;

[AttributeUsage(AttributeTargets.Method)] sealed class TestAttribute : Attribute;
/// <summary>Test yang hanya berjalan bila DOCFRAME_TEST_PG berisi connection string PostgreSQL.</summary>
[AttributeUsage(AttributeTargets.Method)] sealed class PgTestAttribute : Attribute;
/// <summary>Butuh PostgreSQL + koneksi read-only (DOCFRAME_TEST_PG_RO) untuk dashboard SQL.</summary>
[AttributeUsage(AttributeTargets.Method)] sealed class ReportingTestAttribute : Attribute;

sealed class AssertException(string message) : Exception(message);

static class Assert
{
    public static void True(bool c, string msg) { if (!c) throw new AssertException(msg); }
    public static void Equal<T>(T expected, T actual, string what) { if (!Equals(expected, actual)) throw new AssertException($"{what}: diharapkan {expected}, didapat {actual}"); }
    public static async Task<TEx> Throws<TEx>(Func<Task> act, string what) where TEx : Exception
    {
        try { await act(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{what}: diharapkan {typeof(TEx).Name}, didapat {ex.GetType().Name} ({ex.Message})"); }
        throw new AssertException($"{what}: diharapkan {typeof(TEx).Name}, tetapi tidak ada exception");
    }
    public static TEx Throws<TEx>(Action act, string what) where TEx : Exception
    {
        try { act(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{what}: diharapkan {typeof(TEx).Name}, didapat {ex.GetType().Name}"); }
        throw new AssertException($"{what}: diharapkan {typeof(TEx).Name}, tetapi tidak ada exception");
    }
}

sealed partial class Tests
{
    private IServiceProvider sp = default!;
    private DocumentService Docs => sp.GetRequiredService<DocumentService>();
    private Dictionary<string, UserContext> users = new();
    /// <summary>UserContext pengguna demo, dibangun dari akun Identity lewat pemetaan klaim yang sama dengan login.</summary>
    private UserContext U(string id) => users[id];
    public const string DemoPassword = "DocFrame#2026";
    private const string PR = "PurchaseRequest";

    public static readonly string? PgConnection = Environment.GetEnvironmentVariable("DOCFRAME_TEST_PG");
    public static readonly string? PgReadOnly = PgConnection is null ? null : Environment.GetEnvironmentVariable("DOCFRAME_TEST_PG_RO");
    /// <summary>Database analitik terpisah (pemilik) + user read-only di sana, untuk menguji dashboard di luar database aplikasi.</summary>
    public static readonly string? PgAnalytics = PgReadOnly is null ? null : Environment.GetEnvironmentVariable("DOCFRAME_TEST_PG_ANALYTICS");
    public static readonly string? PgAnalyticsRo = PgAnalytics is null ? null : Environment.GetEnvironmentVariable("DOCFRAME_TEST_PG_ANALYTICS_RO");

    /// <summary>Membuang service provider (menutup pool koneksi PostgreSQL milik test ini).</summary>
    public ValueTask DisposeAsync() => sp is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;

    // Dipakai mode benchmark (Bench.cs).
    internal static Task ResetDatabaseForBenchAsync() => ResetDatabaseAsync();
    internal static Task<IServiceProvider> BuildProviderForBenchAsync() => BuildProviderAsync();
    internal static Task SeedForBenchAsync(IServiceProvider sp) => SeedAsync(sp);

    public static async Task<Tests> CreateAsync()
    {
        if (PgConnection is not null) await ResetDatabaseAsync();
        var t = new Tests { sp = await BuildProviderAsync() };
        await SeedAsync(t.sp);
        t.users = await LoadUsersAsync(t.sp);
        return t;
    }

    private static async Task SeedAsync(IServiceProvider sp)
    {
        await DemoAccounts.SeedAsync(sp, DemoPassword);
        await Seeder.RunAsync(sp);
    }

    private static async Task<Dictionary<string, UserContext>> LoadUsersAsync(IServiceProvider sp)
    {
        var dir = sp.GetRequiredService<IUserDirectory>();
        return (await dir.ListAsync("acme")).Concat(await dir.ListAsync("globex")).ToDictionary(u => u.Id);
    }

    /// <summary>Satu "instance aplikasi". Dengan PostgreSQL, beberapa provider berbagi database yang sama.</summary>
    private static async Task<IServiceProvider> BuildProviderAsync(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Print:Letterheads:acme:CompanyName"] = "PT Acme Nusantara Teknologi",
        }).Build());
        services.AddDocFramework(o => o.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Metadata")));
        services.AddDocHandler<PurchaseRequestHandler>();
        services.AddFieldValidator<NpwpValidator>();
        services.AddEventHandler<WorkflowNotificationHandler>();
        services.AddReport<SpendByDepartmentReport>();
        services.AddApiMethod<SisaAnggaranMethod>();
        services.AddApiMethod<AjukanMassalMethod>();
        services.AddDocFrameworkIdentity();
        if (PgConnection is not null) services.AddDocFrameworkPostgreSql(PgConnection);
        if (PgReadOnly is not null) services.AddDocFrameworkReporting(PgReadOnly, o => o.StatementTimeoutSeconds = 2);
        extra?.Invoke(services);
        // ValidateScopes = perilaku Development: menangkap service ber-scope yang di-resolve dari root provider.
        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        if (PgConnection is not null) await sp.MigrateDocFrameworkDatabaseAsync();
        if (PgReadOnly is not null) await sp.SyncReportingSchemaAsync();
        return sp;
    }

    private async Task UseHookProviderAsync(Action<IServiceCollection> configure)
    {
        await DisposeAsync();
        if (PgConnection is not null) await ResetDatabaseAsync();
        sp = await BuildProviderAsync(configure);
        await SeedAsync(sp);
        users = await LoadUsersAsync(sp);
    }

    private static async Task ResetDatabaseAsync()
    {
        await using var db = NpgsqlDataSource.Create(PgConnection!);
        // Kunci advisory yang sama dengan ReportingSchemaSync: tunggu sinkronisasi view (dipicu publish di test sebelumnya) selesai.
        await using var cmd = db.CreateCommand("SELECT pg_advisory_lock(90595631526516); DROP SCHEMA IF EXISTS reporting CASCADE; DROP SCHEMA public CASCADE; CREATE SCHEMA public; SELECT pg_advisory_unlock(90595631526516);");
        await cmd.ExecuteNonQueryAsync();
    }

    private static Dictionary<string, object?> P(params (string K, object? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);
    private static List<Dictionary<string, object?>> Items(decimal qty, decimal rate) =>
        [new() { ["item_name"] = "Uji", ["qty"] = qty, ["rate"] = rate, ["uom"] = "pcs" }];

    // ---------------- Ekspresi ----------------

    [Test]
    public Task Ekspresi_dievaluasi_dengan_proyeksi_list_dan_operator_in()
    {
        var fns = FunctionRegistry.CreateDefault();
        var ev = new ExpressionEvaluator(fns);
        var data = new Dictionary<string, object?>
        {
            ["status"] = "Tinggi",
            ["items"] = new List<Dictionary<string, object?>> { new() { ["amount"] = 1500m }, new() { ["amount"] = 2500m } },
        };
        var ctx = new EvalContext { Scope = new DictionaryScope(data) };
        Assert.Equal(4000m, ev.Evaluate(new ExpressionParser(fns).Parse("sum(items.amount)"), ctx), "sum");
        Assert.Equal(true, ev.Evaluate(new ExpressionParser(fns).Parse("status in ['Normal', 'Tinggi'] && len(items) == 2"), ctx), "in");
        Assert.Equal(false, ev.Evaluate(new ExpressionParser(fns).Parse("!(1 + 2 * 3 == 7)"), ctx), "presedensi");
        return Task.CompletedTask;
    }

    [Test]
    public Task Ekspresi_menolak_fungsi_di_luar_allowlist_dan_ekspresi_berlebihan()
    {
        var fns = FunctionRegistry.CreateDefault();
        Assert.Throws<ExpressionException>(() => new ExpressionParser(fns).Parse("System.IO.File.Delete('x')"), "fungsi asing");
        Assert.Throws<ExpressionException>(() => new ExpressionParser(fns).Parse(new string('(', 60) + "1" + new string(')', 60)), "kedalaman");
        Assert.Throws<ExpressionException>(() => new ExpressionParser(fns).Parse(string.Join(" + ", Enumerable.Repeat("1", 300))), "panjang/node");
        var big = new ExpressionParser(fns, new ExpressionLimits(MaxLength: 5000, MaxNodes: 5000, MaxDepth: 5000))
            .Parse(string.Join(" + ", Enumerable.Repeat("1", 1000)));
        Assert.Throws<ExpressionException>(() => new ExpressionEvaluator(fns).Evaluate(big,
            new EvalContext { Scope = new DictionaryScope(new Dictionary<string, object?>()), Limits = new ExpressionLimits(MaxSteps: 100) }), "batas langkah");
        return Task.CompletedTask;
    }

    // ---------------- Compiler ----------------

    [Test]
    public Task Compiler_mendeteksi_siklus_compute_link_salah_dan_nama_tak_dikenal()
    {
        var compiler = sp.GetRequiredService<MetadataCompiler>();
        var bad = MetadataJson.ParseDocType("""
        { "name": "Bad", "naming": { "pattern": "B-{###}" }, "fields": [
          { "name": "a", "type": "Float", "compute": "b + 1" },
          { "name": "b", "type": "Float", "compute": "a * 2" },
          { "name": "c", "type": "Link", "linkTo": "TidakAda" },
          { "name": "d", "type": "Data", "visibleWhen": "rahasia == 1" } ] }
        """);
        var ex = Assert.Throws<MetadataCompilationException>(() => compiler.Compile([bad], []), "compile");
        Assert.True(ex.Errors.Any(e => e.Message.Contains("melingkar")), "siklus terdeteksi");
        Assert.True(ex.Errors.Any(e => e.Message.Contains("TidakAda")), "link salah terdeteksi");
        Assert.True(ex.Errors.Any(e => e.Message.Contains("rahasia")), "nama tak dikenal terdeteksi");
        return Task.CompletedTask;
    }

    [Test]
    public Task Compiler_memvalidasi_visual_hooks()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var valid = MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get("Vendor").Def));
        valid.Hooks.Add(new DocHookDef
        {
            Name = "blocked-event", Event = "AfterSave",
            When = new DocHookConditionDef { Field = "blacklisted", Operator = "Equals", Value = System.Text.Json.JsonSerializer.SerializeToElement(true) },
            Actions = [new DocHookActionDef { Type = "EmitEvent", EventType = "vendor.blocked" }]
        });
        Assert.True(reg.CompileDraft(valid).Snapshot is not null, "valid visual hook compiles");
        Assert.True(reg.Preview(valid).Changes.Any(c => c.Path == "hooks" && c.Risk == ChangeRisk.Risky), "hook changes are surfaced as risky publish changes");

        valid.Hooks[0].When!.Field = "missing_field";
        valid.Hooks[0].Actions[0].Type = "ExecuteCode";
        var errors = reg.CompileDraft(valid).Errors;
        Assert.True(errors.Any(e => e.Path == "hooks.blocked-event.when.field"), "unknown condition field rejected");
        Assert.True(errors.Any(e => e.Path == "hooks.blocked-event.actions.type"), "unknown action rejected");

        var invalidStage = MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get("Vendor").Def));
        invalidStage.Hooks.Add(new DocHookDef
        {
            Name = "early-event", Event = "BeforeInsert",
            Actions = [new DocHookActionDef { Type = "EmitEvent", EventType = "vendor.early" }]
        });
        Assert.True(reg.CompileDraft(invalidStage).Errors.Any(e => e.Path == "hooks.early-event.actions"), "EmitEvent outside AfterSave rejected");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Visual_hook_reject_menghentikan_insert_dan_emit_event_transaksional()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var def = MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get("Vendor").Def));
        def.Version++;
        def.Hooks.Add(new DocHookDef
        {
            Name = "reject-blocked", Event = "BeforeInsert",
            When = new DocHookConditionDef { Field = "blacklisted", Operator = "Equals", Value = System.Text.Json.JsonSerializer.SerializeToElement(true) },
            Actions = [new DocHookActionDef { Type = "Reject", Field = "blacklisted", Message = "Vendor diblokir" }]
        });
        reg.Publish(def, "admin", acknowledgeRisk: true);
        var before = (await Docs.QueryForReportAsync(U("rina"), "Vendor", [], DocActions.Read)).Count;
        var rejected = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("rina"), "Vendor",
            P(("vendor_name", "Vendor Ditolak"), ("blacklisted", true))), "visual reject");
        Assert.Equal("Vendor diblokir", rejected.Errors[0].Message, "validation message");
        Assert.Equal(before, (await Docs.QueryForReportAsync(U("rina"), "Vendor", [], DocActions.Read)).Count, "reject tidak menyimpan dokumen");

        def = MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get("Vendor").Def));
        def.Version++;
        def.Hooks.Add(new DocHookDef
        {
            Name = "created-event", Event = "AfterSave",
            Actions = [new DocHookActionDef { Type = "EmitEvent", EventType = "vendor.visual_created" }]
        });
        reg.Publish(def, "admin", acknowledgeRisk: true);
        var outbox = sp.GetRequiredService<IOutboxStore>();
        var outboxBefore = outbox.Snapshot().Count;
        await Docs.InsertAsync(U("rina"), "Vendor", P(("vendor_name", "Vendor Visual")));
        Assert.Equal(outboxBefore + 2, outbox.Snapshot().Count, "event visual dan event dokumen berada di outbox");
        Assert.True(outbox.Snapshot().Any(e => e.Type == "vendor.visual_created"), "event visual tersimpan");
    }

    // ---------------- Validasi & compute ----------------

    [Test]
    public async Task Validasi_server_required_kondisional_dan_aturan_lintas_field()
    {
        var ex = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("andi"), PR, P(
            ("priority", "Tinggi"), ("items", Items(1, 100)), ("request_date", "2026-10-10"), ("required_by", "2026-10-01"))), "insert");
        Assert.True(ex.Errors.Any(e => e.Path == "justification"), "justifikasi wajib bila prioritas Tinggi");
        Assert.True(ex.Errors.Any(e => e.Path == "required_by"), "aturan tanggal");

        var rows = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("andi"), PR, P(("items", Items(0, -5)))), "baris");
        Assert.True(rows.Errors.Any(e => e.Path == "items[0].qty"), "qty minimal per baris");
        Assert.True(rows.Errors.Any(e => e.Path == "items[0].rate"), "rate minimal per baris");

        var pattern = await Assert.Throws<ValidationFailedException>(() => Docs.UpdateAsync(U("rina"), PR, "PR-2026-00001", 1, P(("budget_code", "salah"))), "pattern");
        Assert.True(pattern.Errors.Any(e => e.Path == "budget_code"), "pattern kode anggaran");
    }

    [Test]
    public async Task Nilai_compute_dari_client_tidak_dipercaya()
    {
        var doc = await Docs.InsertAsync(U("andi"), PR, P(("items", Items(3, 1000)), ("total", 1m)));
        Assert.Equal(3000m, doc.Data["total"], "total dihitung ulang server");
        Assert.Equal("IT", doc.Data["department"], "fetchFrom departemen");
    }

    // ---------------- Keamanan ----------------

    [Test]
    public async Task IDOR_dokumen_di_luar_scope_dilaporkan_tidak_ditemukan()
    {
        await Assert.Throws<DocumentNotFoundException>(() => Docs.GetAsync(U("sari"), PR, "PR-2026-00001"), "sari baca milik andi");
        await Assert.Throws<DocumentNotFoundException>(() => Docs.UpdateAsync(U("sari"), PR, "PR-2026-00001", 1, P(("priority", "Rendah"))), "sari ubah milik andi");
        await Assert.Throws<DocumentNotFoundException>(() => Docs.GetAsync(U("maya"), PR, "PR-2026-00001"), "manager departemen lain");
        var budi = await Docs.GetAsync(U("budi"), PR, "PR-2026-00001");
        Assert.Equal("PR-2026-00001", budi.Name, "manager departemen sama boleh baca");
        await Assert.Throws<PermissionDeniedException>(() => Docs.UpdateAsync(U("budi"), PR, "PR-2026-00001", 1, P(("priority", "Rendah"))), "manager hanya baca");
    }

    [Test]
    public async Task Isolasi_tenant_pada_baca_list_dan_link()
    {
        var tono = U("tono");
        await Assert.Throws<DocumentNotFoundException>(() => Docs.GetAsync(tono, PR, "PR-2026-00002"), "baca lintas tenant");
        var list = await Docs.ListAsync(tono, PR, new ListRequest());
        Assert.Equal(1, list.Total, "tono hanya melihat data globex");
        var links = await Docs.SearchLinkAsync(tono, "Vendor", null);
        Assert.True(links.Count == 1 && links[0].Title == "Globex Supplies", "lookup Link hanya tenant sendiri");
        var acmeList = await Docs.ListAsync(U("dewi"), PR, new ListRequest(PageSize: 100));
        Assert.True(acmeList.Items.All(i => !Equals(i["_title"], "Tono Wijaya")), "acme tidak melihat globex");
    }

    [Test]
    public async Task Mass_assignment_field_sistem_tak_dikenal_dan_permlevel_ditolak()
    {
        await Assert.Throws<PermissionDeniedException>(() => Docs.UpdateAsync(U("andi"), PR, "PR-2026-00001", 1, P(("workflow_state", "Disetujui"))), "workflow_state");
        await Assert.Throws<PermissionDeniedException>(() => Docs.UpdateAsync(U("andi"), PR, "PR-2026-00001", 1, P(("owner", "sari"))), "owner");
        await Assert.Throws<InvalidPayloadException>(() => Docs.UpdateAsync(U("andi"), PR, "PR-2026-00001", 1, P(("is_admin", true))), "field asing");
        await Assert.Throws<PermissionDeniedException>(() => Docs.UpdateAsync(U("andi"), PR, "PR-2026-00001", 1, P(("budget_code", "IT-2026"))), "permlevel 1");
        var ok = await Docs.UpdateAsync(U("rina"), PR, "PR-2026-00001", 1, P(("budget_code", "IT-2026")));
        Assert.Equal("IT-2026", ok.Data["budget_code"], "procurement boleh menulis permlevel 1");
        var asAndi = await Docs.GetAsync(U("andi"), PR, "PR-2026-00001");
        Assert.True(!asAndi.Data.ContainsKey("budget_code"), "field permlevel 1 tidak dikirim ke pemohon");
        var audit = await Docs.GetAuditAsync(U("andi"), PR, "PR-2026-00001");
        Assert.True(audit.SelectMany(a => a.Changes).All(c => c.Field != "Kode anggaran"), "audit menyembunyikan field terbatas");
    }

    [Test]
    public async Task Filter_dan_sort_hanya_dari_allowlist()
    {
        await Assert.Throws<InvalidPayloadException>(() => Docs.ListAsync(U("andi"), PR,
            new ListRequest([new FilterClause("procurement_notes", "like", "a")])), "filter field terbatas");
        await Assert.Throws<InvalidPayloadException>(() => Docs.ListAsync(U("andi"), PR,
            new ListRequest([new FilterClause("priority", "; DROP TABLE", "a")])), "operator asing");
        var r = await Docs.ListAsync(U("dewi"), PR, new ListRequest([new FilterClause("priority", "=", "Tinggi")], SortField: "total", SortDirection: "desc"));
        Assert.True(r.Total > 0 && r.Items.All(i => Equals(i["priority"], "Tinggi")), "filter priority");
    }

    [Test]
    public async Task Konflik_versi_terdeteksi()
    {
        await Docs.UpdateAsync(U("andi"), PR, "PR-2026-00001", 1, P(("priority", "Rendah")));
        await Assert.Throws<ConcurrencyException>(() => Docs.UpdateAsync(U("andi"), PR, "PR-2026-00001", 1, P(("priority", "Normal"))), "versi basi");
    }

    // ---------------- Workflow & lifecycle ----------------

    [Test]
    public async Task Workflow_role_kondisi_dan_dokumen_terkunci_setelah_diajukan()
    {
        var andi = U("andi");
        var doc = await Docs.InsertAsync(andi, PR, P(("vendor", "VND-0001"), ("items", Items(2, 3_000_000))));
        await Assert.Throws<PermissionDeniedException>(() => Docs.RunActionAsync(U("budi"), PR, doc.Name, "Ajukan", doc.Version, null), "atasan tidak bisa mengajukan milik orang lain");
        doc = await Docs.RunActionAsync(andi, PR, doc.Name, "Ajukan", doc.Version, null);
        Assert.Equal("Menunggu Atasan", doc.WorkflowState, "state");
        Assert.Equal(DocStatus.Submitted, doc.DocStatus, "docstatus");
        await Assert.Throws<InvalidStateException>(() => Docs.UpdateAsync(andi, PR, doc.Name, doc.Version, P(("priority", "Tinggi"))), "submitted terkunci");
        await Assert.Throws<PermissionDeniedException>(() => Docs.RunActionAsync(andi, PR, doc.Name, "Setujui", doc.Version, null), "pemohon tidak bisa menyetujui");
        await Assert.Throws<PermissionDeniedException>(() => Docs.RunActionAsync(U("budi"), PR, doc.Name, "Setujui & teruskan ke Finance", doc.Version, null), "total kecil tidak diteruskan ke Finance");
        doc = await Docs.RunActionAsync(U("budi"), PR, doc.Name, "Setujui", doc.Version, null);
        Assert.Equal("Disetujui", doc.WorkflowState, "disetujui");
        var audit = await Docs.GetAuditAsync(andi, PR, doc.Name);
        Assert.True(audit.Any(a => a.FromState == "Menunggu Atasan" && a.ToState == "Disetujui" && a.Actor == "Budi Santoso"), "audit transisi");
    }

    [Test]
    public async Task Hook_submit_dan_validator_domain_dijalankan()
    {
        var andi = U("andi");
        var noVendor = await Docs.InsertAsync(andi, PR, P(("items", Items(1, 50_000))));
        var ex = await Assert.Throws<ValidationFailedException>(() => Docs.RunActionAsync(andi, PR, noVendor.Name, "Ajukan", noVendor.Version, null), "ajukan tanpa vendor");
        Assert.True(ex.Errors.Any(e => e.Path == "vendor"), "BeforeSubmit menolak");
        var blocked = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(andi, PR, P(("vendor", "VND-0004"), ("items", Items(1, 1)))), "vendor diblokir");
        Assert.True(blocked.Errors.Any(e => e.Message.Contains("diblokir")), "handler C#");
        var other = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(andi, PR, P(("requester", "EMP-0002"), ("items", Items(1, 1)))), "atas nama orang lain");
        Assert.True(other.Errors.Any(e => e.Path == "requester"), "pemohon sendiri");
        var npwp = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("rina"), "Vendor", P(("vendor_name", "X"), ("npwp", "12.34"))), "npwp");
        Assert.True(npwp.Errors.Any(e => e.Path == "npwp"), "validator terdaftar");
    }

    [Test]
    public async Task Tolak_wajib_komentar_lalu_amend_membuat_draft_baru()
    {
        var doc = await Docs.GetAsync(U("dewi"), PR, "PR-2026-00003");
        await Assert.Throws<ValidationFailedException>(() => Docs.RunActionAsync(U("dewi"), PR, doc.Name, "Tolak", doc.Version, " "), "tanpa komentar");
        doc = await Docs.RunActionAsync(U("dewi"), PR, doc.Name, "Tolak", doc.Version, "Pecah per kuartal");
        Assert.Equal(DocStatus.Cancelled, doc.DocStatus, "ditolak = cancelled");
        var amended = await Docs.RunActionAsync(U("andi"), PR, doc.Name, DocActions.Amend, doc.Version, null);
        Assert.Equal("PR-2026-00003-1", amended.Name, "nama amandemen");
        Assert.Equal("PR-2026-00003", amended.AmendedFrom, "rujukan asal");
        Assert.Equal("Draft", amended.WorkflowState, "kembali draft");
    }

    // ---------------- Outbox & notifikasi ----------------

    [Test]
    public async Task Outbox_mengirim_notifikasi_secara_idempoten()
    {
        // Worker memproses per batch (maks. 50); proses sampai outbox kosong.
        var processor = sp.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 20 && (await processor.ProcessDueAsync()).Processed > 0; i++) { }
        var inbox = sp.GetRequiredService<INotificationStore>();
        Assert.True(inbox.ForUser("acme", "budi").Any(n => n.DocName == "PR-2026-00002"), "atasan IT menerima notifikasi");
        Assert.True(inbox.ForUser("acme", "maya").All(n => n.DocName != "PR-2026-00002"), "atasan Marketing tidak menerima");
        Assert.True(inbox.ForUser("acme", "sari").Any(n => n.Subject.Contains("ditolak")), "pemohon menerima penolakan");

        int Count() => users.Values.Sum(u => inbox.ForUser(u.TenantId, u.Id).Count);
        var before = Count();
        var msg = sp.GetRequiredService<IOutboxStore>().Snapshot().First(m => m.Type == "workflow.transitioned");
        await sp.GetServices<IEventHandler>().Single().HandleAsync(msg, default);
        Assert.Equal(before, Count(), "pemrosesan ulang tidak menggandakan");
    }

    [Test]
    public async Task Outbox_retry_lalu_dead_letter_saat_handler_gagal()
    {
        var store = sp.GetRequiredService<IOutboxStore>();
        var clock = new ManualClock();
        var processor = new OutboxProcessor(store, [new FailingHandler()], clock);
        for (var i = 0; i < OutboxProcessor.MaxAttempts * 10; i++)
        {
            var r = await processor.ProcessDueAsync();
            if (r.Failed + r.DeadLettered == 0) clock.Advance(TimeSpan.FromMinutes(5)); // batch habis → majukan waktu ke percobaan berikutnya
            if (store.Snapshot().All(m => m.DeadLettered)) break;
        }
        var msgs = store.Snapshot();
        Assert.True(msgs.All(m => m.DeadLettered && m.Attempts == OutboxProcessor.MaxAttempts), "semua pesan masuk dead-letter setelah batas percobaan");
    }

    private sealed class FailingHandler : IEventHandler
    {
        public bool CanHandle(string eventType) => true;
        public Task HandleAsync(OutboxMessage message, CancellationToken ct) => throw new InvalidOperationException("SMTP mati");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan t) => now += t;
    }

    // ---------------- Metadata versioning ----------------

    [Test]
    public async Task Publish_metadata_mengklasifikasi_risiko_dan_membuat_versi_baru()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        DocTypeDef Clone() => MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get("Vendor").Def));

        var safe = Clone(); safe.Version = 2; safe.Fields[0].Label = "Nama perusahaan";
        Assert.Equal(ChangeRisk.Safe, reg.Preview(safe).Risk, "ubah label aman");

        var risky = Clone(); risky.Version = 2; risky.Fields.RemoveAll(f => f.Name == "npwp");
        Assert.Equal(ChangeRisk.Risky, reg.Preview(risky).Risk, "hapus field berisiko");
        Assert.Throws<InvalidOperationException>(() => reg.Publish(risky, "admin", acknowledgeRisk: false), "butuh konfirmasi");

        Assert.True(!reg.Preview(Clone()).Valid, "versi harus naik");
        var broken = Clone(); broken.Version = 2; broken.Fields[0].Type = "TipeAsing";
        Assert.True(!reg.Preview(broken).Valid, "draft tak valid ditolak");

        reg.Publish(safe, "admin", acknowledgeRisk: false);
        Assert.Equal(2, reg.Current.Get("Vendor").Version, "versi aktif");
        Assert.Equal(MetadataStatus.Deprecated, reg.History("Vendor").Single(v => v.Version == 1).Status, "v1 usang");
        var v = await Docs.InsertAsync(U("rina"), "Vendor", P(("vendor_name", "PT Baru")));
        Assert.Equal(2, v.SchemaVersion, "dokumen baru mencatat skema v2");
    }

    [Test]
    public Task Migrasi_doctype_mengubah_payload_tanpa_mengubah_sumber()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var sourceMeta = reg.Current.Get("Vendor");
        var draft = MetadataJson.ParseDocType(MetadataJson.Serialize(sourceMeta.Def));
        draft.Version = sourceMeta.Version + 1;
        draft.Fields[0].Name = "supplier_name";
        draft.ListView.TitleField = "supplier_name";
        draft.ListView.SearchFields = draft.ListView.SearchFields.Select(f => f == "vendor_name" ? "supplier_name" : f).ToList();
        draft.DataMigration = new DataMigrationPlanDef
        {
            FromVersion = sourceMeta.Version,
            Operations = [
                new DataMigrationOperationDef { Type = "RenameField", FromField = "vendor_name", ToField = "supplier_name" },
                new DataMigrationOperationDef { Type = "SetDefault", Field = "category", Value = JsonDocument.Parse("\"ATK\"").RootElement.Clone() }
            ]
        };
        var source = new Document
        {
            TenantId = "acme", DocType = "Vendor", Name = "VND-TEST", Owner = "admin", Created = DateTimeOffset.UtcNow,
            Modified = DateTimeOffset.UtcNow, ModifiedBy = "admin", SchemaVersion = sourceMeta.Version,
            Data = new Dictionary<string, object?> { ["vendor_name"] = "Test supplier" }
        };

        var target = reg.CompileDraft(draft).Snapshot!.Get("Vendor");
        var (migrated, changes) = DocTypeDataMigrator.Transform(source, target, draft.DataMigration.Operations, "admin", DateTimeOffset.UtcNow);
        Assert.Equal("Test supplier", migrated.Data["supplier_name"], "rename value");
        Assert.Equal("ATK", migrated.Data["category"], "default applied");
        Assert.True(!migrated.Data.ContainsKey("vendor_name"), "source field removed");
        Assert.Equal(sourceMeta.Version + 1, migrated.SchemaVersion, "target schema version");
        Assert.Equal(source.Version + 1, migrated.Version, "document version increments");
        Assert.True(source.Data.ContainsKey("vendor_name") && !source.Data.ContainsKey("supplier_name"), "source document unchanged");
        Assert.Equal(2, changes.Count, "audit changes");
        return Task.CompletedTask;
    }

    [Test, PgTest]
    public async Task Pg_migrasi_doctype_memvalidasi_jumlah_sebelum_transaksi_dan_menerbitkan_bersama()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var source = reg.Current.Get("Vendor");
        var draft = MetadataJson.ParseDocType(MetadataJson.Serialize(source.Def));
        draft.Version = source.Version + 1;
        draft.Fields.Add(new FieldDef { Name = "migration_marker", Type = "Data", Label = "Migration marker" });
        draft.DataMigration = new DataMigrationPlanDef
        {
            FromVersion = source.Version,
            Operations = [new DataMigrationOperationDef
            {
                Type = "SetDefault", Field = "migration_marker", Value = JsonDocument.Parse("\"migrated\"").RootElement.Clone()
            }]
        };

        var preview = reg.PreviewDataMigration(draft);
        Assert.True(preview.Valid && preview.Documents > 0, "preview succeeds for seeded vendor documents");
        var store = sp.GetRequiredService<IDocumentStore>();
        var before = await store.GetAsync("acme", "Vendor", "VND-0001");
        Assert.True(before is not null && !before.Data.ContainsKey("migration_marker"), "source row not changed by dry-run");

        await Assert.Throws<InvalidOperationException>(
            () => Task.Run(() => reg.Publish(draft, "admin", acknowledgeRisk: true, preview.Documents + 1)),
            "changed dry-run count aborts publish");
        Assert.Equal(source.Version, reg.Current.Get("Vendor").Version, "failed migration leaves active metadata unchanged");
        var afterFailedPublish = await store.GetAsync("acme", "Vendor", "VND-0001");
        Assert.True(afterFailedPublish is not null && !afterFailedPublish.Data.ContainsKey("migration_marker"), "failed publish rolls back document transforms");

        reg.Publish(draft, "admin", acknowledgeRisk: true, preview.Documents);
        var migrated = await store.GetAsync("acme", "Vendor", "VND-0001");
        Assert.Equal(source.Version + 1, reg.Current.Get("Vendor").Version, "new metadata becomes active");
        Assert.True(migrated is not null && Equals(migrated.Data["migration_marker"], "migrated"), "document transformed");
        Assert.Equal(source.Version + 1, migrated!.SchemaVersion, "document records target schema version");
        Assert.True(reg.Current.Get("Vendor").Def.DataMigration is null, "applied plan is not replayed after publish");
        Assert.True((await store.GetAuditAsync("acme", "Vendor", "VND-0001")).Any(a => a.Action == "Migrate"), "migration audit is written");
    }

    // ---------------- Report ----------------

    [Test]
    public async Task Report_menerapkan_scope_dan_permission_yang_sama()
    {
        var reports = sp.GetRequiredService<ReportService>();
        var maya = await reports.RunAsync(U("maya"), "spend-by-department", new Dictionary<string, string?>());
        Assert.True(maya.Rows.Count > 0 && maya.Rows.All(r => Equals(r["department"], "Marketing")), "manager hanya departemennya");
        await Assert.Throws<PermissionDeniedException>(() => reports.ExportCsvAsync(U("maya"), "spend-by-department", new Dictionary<string, string?>()), "manager tanpa izin export");
        await Assert.Throws<PermissionDeniedException>(() => reports.RunAsync(U("andi"), "spend-by-department", new Dictionary<string, string?>()), "pemohon tanpa izin report");
        var csv = await reports.ExportCsvAsync(U("dewi"), "spend-by-department", new Dictionary<string, string?>());
        Assert.True(csv.StartsWith("Departemen,") && csv.Contains("IT,"), "CSV finance");
    }

    // ---------------- Cetak ----------------

    [Test]
    public Task Terbilang_rupiah_bahasa_indonesia()
    {
        Assert.Equal("Dua juta enam ratus lima puluh ribu rupiah", Terbilang.Rupiah(2_650_000m), "2.650.000");
        Assert.Equal("Seribu satu rupiah", Terbilang.Rupiah(1_001m), "1.001");
        Assert.Equal("Seratus sebelas rupiah", Terbilang.Rupiah(111m), "111");
        Assert.Equal("Satu miliar lima ratus juta rupiah", Terbilang.Rupiah(1_500_000_000m), "1,5 miliar");
        Assert.Equal("Seratus dua puluh sembilan juta rupiah", Terbilang.Rupiah(129_000_000m), "129 juta");
        Assert.Equal("Nol rupiah", Terbilang.Rupiah(0m), "nol");
        Assert.Equal("Dua belas ribu rupiah lima puluh sen", Terbilang.Rupiah(12_000.5m), "sen");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Cetak_tanda_tangan_dari_riwayat_dan_tanda_air_sesuai_status()
    {
        var printer = sp.GetRequiredService<PrintService>();
        var approved = await printer.BuildAsync(U("sari"), PR, "PR-2026-00004");
        Assert.Equal(null, approved.Watermark, "dokumen disetujui tanpa tanda air");
        Assert.Equal(2, approved.Signatures.Count, "slot Finance opsional disembunyikan bila tidak dilalui");
        Assert.Equal("Sari Wulandari", approved.Signatures[0].Name, "diajukan oleh");
        Assert.Equal("Maya Lestari", approved.Signatures[1].Name, "disetujui atasan");
        Assert.True(approved.AmountInWords!.StartsWith("Empat juta"), "terbilang total");
        Assert.Equal("PT Acme Nusantara Teknologi", approved.Letterhead.CompanyName, "kop surat tenant");

        var pending = await printer.BuildAsync(U("andi"), PR, "PR-2026-00003");
        Assert.Equal("BELUM DISETUJUI", pending.Watermark, "menunggu Finance");
        Assert.Equal(3, pending.Signatures.Count, "slot Finance tampil selama masih menunggu");
        Assert.True(pending.Signatures[2].Name is null, "Finance belum menandatangani");

        var draft = await printer.BuildAsync(U("andi"), PR, "PR-2026-00001");
        Assert.Equal("DRAFT", draft.Watermark, "draft");
        Assert.True(draft.Sections.SelectMany(x => x.Fields).Any(f => f.Value.Contains("PT Sinar Komputindo (VND-0001)")), "Link dicetak dengan judul");
    }

    [Test]
    public async Task Cetak_menegakkan_permission_dan_tercatat_di_audit()
    {
        var printer = sp.GetRequiredService<PrintService>();
        await Assert.Throws<DocumentNotFoundException>(() => printer.BuildAsync(U("sari"), PR, "PR-2026-00001"), "IDOR cetak");
        // Nomor sama di tenant lain = dokumen lain: Tono mendapat dokumen globex miliknya, bukan milik acme.
        var globex = await printer.BuildAsync(U("tono"), PR, "PR-2026-00001");
        Assert.True(globex.Tables[0].Rows[0][0].Contains("Globex") && globex.Letterhead.CompanyName != "PT Acme Nusantara Teknologi", "isolasi tenant saat cetak");
        await Assert.Throws<DocumentNotFoundException>(() => printer.BuildAsync(U("tono"), PR, "PR-2026-00004"), "dokumen acme tidak ada bagi globex");
        await Docs.LogPrintAsync(U("andi"), PR, "PR-2026-00001", "PDF");
        var audit = await Docs.GetAuditAsync(U("andi"), PR, "PR-2026-00001");
        Assert.True(audit.Any(a => a.Action == "Dicetak" && a.Comment == "Format PDF" && a.Actor == "Andi Pratama"), "audit cetak");

        // DocType tanpa printFormat memakai section form; field yang tidak boleh dibaca tidak ikut tercetak.
        var emp = await printer.BuildAsync(U("andi"), "Employee", "EMP-0001");
        Assert.True(emp.Sections.SelectMany(x => x.Fields).Any(f => f.Label == "Nama lengkap"), "format default dari layout");
    }

    [Test]
    public async Task Html_cetak_mandiri_berisi_gaya_dan_isi()
    {
        var model = await sp.GetRequiredService<PrintService>().BuildAsync(U("sari"), PR, "PR-2026-00004");
        var html = await sp.GetRequiredService<PrintHtmlRenderer>().RenderAsync(model);
        Assert.True(html.StartsWith("<!DOCTYPE html>") && html.Contains("@page"), "dokumen HTML dengan CSS A4");
        Assert.True(html.Contains("PR-2026-00004") && (html.Contains("In words:") || html.Contains("Terbilang:")) && html.Contains("Maya Lestari"), "isi dokumen");
        Assert.True(!html.Contains("http://") && !html.Contains("https://"), "tanpa sumber eksternal");
    }

    [Test]
    public Task Compiler_memvalidasi_print_format()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var bad = MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get(PR).Def));
        bad.Version = 2;
        bad.PrintFormat!.Sections![0].Fields.Add("tidak_ada");
        bad.PrintFormat.Signatures[0].ToStates.Add("State Asing");
        bad.PrintFormat.AmountInWordsField = "priority";
        var preview = reg.Preview(bad);
        Assert.True(!preview.Valid, "draft ditolak");
        Assert.True(preview.Errors.Any(e => e.Message.Contains("tidak_ada")), "field tak dikenal");
        Assert.True(preview.Errors.Any(e => e.Message.Contains("State Asing")), "state tak dikenal");
        Assert.True(preview.Errors.Any(e => e.Path == "printFormat.amountInWordsField"), "terbilang harus angka");
        return Task.CompletedTask;
    }

    // ---------------- Editor DocType visual ----------------

    [Test]
    public Task Editor_konversi_bolak_balik_tanpa_perubahan()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        foreach (var meta in reg.Current.DocTypes.Values)
        {
            var back = DocTypeDesign.From(meta.Def).ToDef(meta.Version);
            var changes = MetadataDiff.Compare(meta.Def, back);
            Assert.True(changes.Count == 0, $"{meta.Name}: desain tanpa edit tidak boleh menghasilkan perubahan ({string.Join("; ", changes.Select(c => c.Path + " " + c.Description))})");
            Assert.True(back.Fields.Select(f => f.Name).SequenceEqual(meta.Def.Fields.Select(f => f.Name)), $"{meta.Name}: urutan field sama");
        }
        var hooked = DocTypeDesign.From(reg.Current.Get("Vendor").Def);
        hooked.Def.Hooks.Add(new DocHookDef
        {
            Name = "roundtrip", Event = "AfterSave",
            When = new DocHookConditionDef { Field = "blacklisted", Value = System.Text.Json.JsonSerializer.SerializeToElement(true) },
            Actions = [new DocHookActionDef { Type = "EmitEvent", EventType = "vendor.roundtrip" }]
        });
        var hookedBack = DocTypeDesign.From(hooked.ToDef(hooked.Def.Version)).ToDef(hooked.Def.Version);
        Assert.Equal(MetadataJson.Serialize(hooked.Def), MetadataJson.Serialize(hookedBack), "hook metadata round-trip");
        return Task.CompletedTask;
    }

    [Test]
    public Task Editor_operasi_tambah_pindah_kolom_hapus_dan_klasifikasi_risiko()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var active = reg.Current.Get(PR).Def;
        var d = DocTypeDesign.From(active);
        var tab = d.Tabs.Single(t => t.Title == "Pengajuan");
        var kebutuhan = tab.Sections.Single(s => s.Title == "Kebutuhan");
        var pemohon = tab.Sections.Single(s => s.Title == "Pemohon");

        // Tambah field baru di kolom 2, posisi pertama
        var f = d.AddField("Currency", kebutuhan, 1, 0, reg.Current);
        f.Name = "estimated_shipping"; f.Label = "Ongkos kirim";
        var def = d.ToDef(2);
        var added = def.Fields.Single(x => x.Name == "estimated_shipping");
        Assert.Equal("Kebutuhan", added.Section, "section field baru");
        Assert.Equal(2, added.Column, "kolom field baru");
        Assert.Equal("Pengajuan", added.Tab, "tab field baru");
        var (snap, errs) = reg.CompileDraft(def);
        Assert.True(snap is not null && errs.Count == 0, "draft dengan field baru valid: " + string.Join("; ", errs));
        Assert.Equal(ChangeRisk.Safe, reg.Preview(def).Risk, "menambah field opsional = aman");

        // Pindah 'priority' ke awal section Pemohon kolom 1
        var priority = d.AllFields.Single(x => x.Name == "priority");
        d.Move(priority, pemohon, 0, 0);
        def = d.ToDef(2);
        var moved = def.Fields.Single(x => x.Name == "priority");
        Assert.True(moved.Section == "Pemohon" && moved.Column == 1, "priority pindah ke section Pemohon kolom 1");
        var layout = reg.CompileDraft(def).Snapshot!.Get(PR).Layout;
        var firstInColumn = layout.Single(t => t.Title == "Pengajuan").Sections.Single(s => s.Title == "Pemohon").Columns[0].Fields[0];
        Assert.Equal("priority", firstInColumn.Name, "di form, priority tampil paling atas kolom 1 section Pemohon");

        // Pindah ke bawah di kolom yang sama: indeks setelah elemen dihapus tetap benar
        var col0 = pemohon.Columns[0];
        var first = col0[0]; var third = col0[2];
        d.Move(first, pemohon, 0, 3);
        Assert.True(col0.IndexOf(first) == 2 && col0.IndexOf(third) == 1, "geser ke posisi sesudahnya");

        // Ubah 2 kolom → 1 kolom: field kolom 2 digabung, tidak hilang
        var count = pemohon.Columns.Sum(c => c.Count);
        d.SetColumns(pemohon, 1);
        Assert.Equal(count, pemohon.Columns[0].Count, "jumlah field tetap saat kolom digabung");

        // Tab baru: field di dalamnya mendapat judul tab eksplisit
        var newTab = d.AddTab();
        newTab.Title = "Lampiran";
        d.Move(f, newTab.Sections[0], 0, null);
        Assert.Equal("Lampiran", d.ToDef(2).Fields.Single(x => x.Name == "estimated_shipping").Tab, "field di tab baru");
        Assert.True(!d.RemoveTab(newTab), "tab berisi field tidak bisa dihapus");

        // Hapus field lama = berisiko
        d.Remove(d.AllFields.Single(x => x.Name == "required_by"));
        var pv = reg.Preview(d.ToDef(2));
        Assert.Equal(ChangeRisk.Risky, pv.Risk, "menghapus field lama berisiko");
        Assert.True(pv.Changes.Any(c => c.Path == "fields.required_by" && c.Risk == ChangeRisk.Risky), "diff menyebut field yang dihapus");
        return Task.CompletedTask;
    }

    [Test]
    public Task Editor_error_ekspresi_tertangkap_saat_kompilasi_draft()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var d = DocTypeDesign.From(reg.Current.Get(PR).Def);
        var f = d.AllFields.Single(x => x.Name == "justification");
        f.VisibleWhen = "priority == ";             // tidak lengkap
        d.AllFields.Single(x => x.Name == "vendor").RequiredWhen = "kolom_asing > 0";
        var (snap, errs) = reg.CompileDraft(d.ToDef(2));
        Assert.True(snap is null, "draft tidak valid");
        Assert.True(errs.Any(e => e.Path == "fields.justification.visibleWhen"), "error ekspresi tak lengkap pada path field");
        Assert.True(errs.Any(e => e.Path == "fields.vendor.requiredWhen" && e.Message.Contains("kolom_asing")), "nama tak dikenal");
        Assert.Equal(1, reg.Current.Get(PR).Version, "versi aktif tidak berubah");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Editor_publish_lalu_dokumen_memakai_field_baru()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var d = DocTypeDesign.From(reg.Current.Get("Vendor").Def);
        var sec = d.Tabs[0].Sections[0];
        var f = d.AddField("Select", sec, 0, null, reg.Current);
        f.Name = "rating"; f.Label = "Penilaian"; f.Options = ["A", "B", "C"]; f.InListView = true;
        reg.Publish(d.ToDef(2), "admin", acknowledgeRisk: false);
        Assert.Equal(2, reg.Current.Get("Vendor").Version, "versi 2 aktif");
        var v = await Docs.InsertAsync(U("rina"), "Vendor", P(("vendor_name", "PT Baru"), ("rating", "A")));
        Assert.Equal("A", v.Data["rating"], "field baru tersimpan");
        await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("rina"), "Vendor", P(("vendor_name", "PT X"), ("rating", "Z"))), "opsi di luar daftar ditolak");
        var list = await Docs.ListAsync(U("rina"), "Vendor", new ListRequest());
        Assert.True(list.Columns.Contains("rating"), "kolom daftar ikut berubah");
    }

    // ---------------- Login (ASP.NET Core Identity) ----------------

    [Test]
    public async Task Login_klaim_dipetakan_ke_UserContext()
    {
        await using var scope = sp.CreateAsyncScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UserAccount>>();
        var principal = await factory.CreateAsync((await um.FindByNameAsync("budi"))!);
        var ctx = ClaimsUserMapper.ToUserContext(principal)!;
        Assert.Equal("budi", ctx.Id, "id dari klaim NameIdentifier");
        Assert.Equal("acme", ctx.TenantId, "tenant dari klaim");
        Assert.Equal("Budi Santoso", ctx.FullName, "nama lengkap");
        Assert.True(ctx.Roles.SetEquals(["Employee", "Manager"]), "role dari klaim");
        Assert.Equal("IT", ctx.Attr("department"), "departemen");
        Assert.Equal("EMP-0003", ctx.Attr("employee"), "karyawan");
        Assert.True(principal.FindFirst("AspNet.Identity.SecurityStamp") is not null, "security stamp ada di klaim (untuk validasi ulang sesi)");
        var fromDirectory = U("budi");
        Assert.True(fromDirectory.Id == ctx.Id && fromDirectory.TenantId == ctx.TenantId && fromDirectory.Roles.SetEquals(ctx.Roles)
            && fromDirectory.Attributes.OrderBy(k => k.Key).SequenceEqual(ctx.Attributes.OrderBy(k => k.Key)), "direktori & login menghasilkan UserContext sama");
        // Permission tetap bekerja dengan UserContext dari klaim
        var doc = await Docs.GetAsync(ctx, PR, "PR-2026-00001");
        Assert.Equal("PR-2026-00001", doc.Name, "manager IT membaca pengajuan IT");
    }

    [Test]
    public async Task Login_password_hash_dan_lockout_setelah_5_kali_gagal()
    {
        await using var scope = sp.CreateAsyncScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<UserAccount>>();
        var andi = (await um.FindByNameAsync("andi"))!;
        Assert.True(andi.PasswordHash is { Length: > 40 } h && !h.Contains(DemoPassword), "password disimpan sebagai hash");
        Assert.True((await signIn.CheckPasswordSignInAsync(andi, DemoPassword, lockoutOnFailure: true)).Succeeded, "password benar");
        for (var i = 0; i < 5; i++)
        {
            andi = (await um.FindByNameAsync("andi"))!;
            Assert.True(!(await signIn.CheckPasswordSignInAsync(andi, "salah-" + i, lockoutOnFailure: true)).Succeeded, "password salah ditolak");
        }
        andi = (await um.FindByNameAsync("andi"))!;
        var locked = await signIn.CheckPasswordSignInAsync(andi, DemoPassword, lockoutOnFailure: true);
        Assert.True(locked.IsLockedOut, "setelah 5 kali gagal akun terkunci, password benar pun ditolak");
    }

    [Test]
    public async Task Login_akun_nonaktif_ditolak_dan_hilang_dari_direktori()
    {
        await using var scope = sp.CreateAsyncScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<UserAccount>>();
        var sari = (await um.FindByNameAsync("sari"))!;
        sari.IsActive = false;
        await um.UpdateAsync(sari);
        await um.UpdateSecurityStampAsync(sari);
        var result = await signIn.CheckPasswordSignInAsync((await um.FindByNameAsync("sari"))!, DemoPassword, lockoutOnFailure: false);
        Assert.True(result.IsNotAllowed, "akun nonaktif tidak boleh login walau password benar");
        var dir = sp.GetRequiredService<IUserDirectory>();
        Assert.True(await dir.FindAsync("sari") is null, "tidak ada di direktori");
        Assert.True((await dir.ListAsync("acme")).All(u => u.Id != "sari"), "tidak menerima notifikasi");
    }

    [Test]
    public Task Login_klaim_tidak_lengkap_gagal_tertutup()
    {
        Assert.True(ClaimsUserMapper.ToUserContext(new ClaimsPrincipal(new ClaimsIdentity())) is null, "tidak terautentikasi");
        var noTenant = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "x"), new Claim(ClaimTypes.Role, UserContext.SystemManager)], "test"));
        Assert.True(ClaimsUserMapper.ToUserContext(noTenant) is null, "tanpa klaim tenant → ditolak, walau mengaku System Manager");
        Assert.True(ClaimsUserMapper.ToUserContext(null) is null, "tanpa principal");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Login_perubahan_role_mengganti_security_stamp_dan_klaim()
    {
        await using var scope = sp.CreateAsyncScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UserAccount>>();
        var rina = (await um.FindByNameAsync("rina"))!;
        var oldStamp = rina.SecurityStamp;
        rina.Roles = ["Employee"];
        await um.UpdateAsync(rina);
        await um.UpdateSecurityStampAsync(rina);
        var fresh = (await um.FindByNameAsync("rina"))!;
        Assert.True(fresh.SecurityStamp != oldStamp, "stamp berubah → sesi lama tidak berlaku saat validasi ulang");
        var ctx = ClaimsUserMapper.ToUserContext(await factory.CreateAsync(fresh))!;
        Assert.True(!ctx.Roles.Contains("Procurement"), "role yang dicabut tidak ada di klaim baru");
        await Assert.Throws<PermissionDeniedException>(() => Docs.InsertAsync(ctx, "Vendor", P(("vendor_name", "X"))), "izin ikut dicabut");
    }

    // ---------------- Workflow & DocType baru dari UI ----------------

    private static WorkflowDef VendorWorkflow(int version = 1) => new()
    {
        Name = "VendorReview", DocType = "Vendor", Version = version, InitialState = "Draft",
        States = [new() { Name = "Draft", DocStatus = 0 }, new() { Name = "Ditinjau", DocStatus = 1, Tone = "pending" }, new() { Name = "Aktif", DocStatus = 1, Tone = "positive" }],
        Transitions =
        [
            new() { From = "Draft", To = "Ditinjau", Action = "Kirim tinjauan", AllowedRoles = ["Procurement"] },
            new() { From = "Ditinjau", To = "Aktif", Action = "Aktifkan", AllowedRoles = ["Finance"] },
        ],
    };

    [Test]
    public Task Workflow_diff_mengklasifikasikan_risiko()
    {
        var oldWf = VendorWorkflow();
        var added = VendorWorkflow(2);
        added.States.Add(new() { Name = "Ditolak", DocStatus = 2, Tone = "negative" }); added.Transitions.Add(new() { From = "Ditinjau", To = "Ditolak", Action = "Tolak", AllowedRoles = ["Finance"], RequireComment = true });
        Assert.Equal(ChangeRisk.Safe, WorkflowDiff.Compare(oldWf, added).Max(c => c.Risk), "menambah transisi = aman");
        var roles = VendorWorkflow(2); roles.Transitions[1].AllowedRoles = ["Employee"];
        Assert.Equal(ChangeRisk.Risky, WorkflowDiff.Compare(oldWf, roles).Max(c => c.Risk), "mengubah role transisi = berisiko");
        var removed = VendorWorkflow(2); removed.States.RemoveAll(x => x.Name == "Aktif"); removed.Transitions.RemoveAt(1);
        var ch = WorkflowDiff.Compare(oldWf, removed);
        Assert.True(ch.Any(c => c.Path == "states.Aktif" && c.Risk == ChangeRisk.Risky), "menghapus state = berisiko");
        Assert.True(ch.Any(c => c.Risk == ChangeRisk.NeedsTransform && c.Description.Contains("Aktifkan")), "menghapus transisi = perlu transformasi");
        var init = VendorWorkflow(2); init.InitialState = "Ditinjau"; init.States[1].DocStatus = 0;
        Assert.True(WorkflowDiff.Compare(oldWf, init).Any(c => c.Path == "initialState" && c.Risk == ChangeRisk.NeedsTransform), "state awal berubah");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Workflow_baru_dipasang_ke_DocType_dan_langsung_berjalan()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var wf = VendorWorkflow();
        var pv = reg.PreviewWorkflow(wf, attach: true);
        Assert.True(pv.Valid, "draft valid: " + string.Join("; ", pv.Errors));
        Assert.True(pv.Changes.Any(c => c.Description.Contains("Workflow baru")) && pv.Risk == ChangeRisk.Risky, "memasang workflow ke DocType berisi data = berisiko");
        Assert.Throws<InvalidOperationException>(() => reg.PublishWorkflow(wf, "admin", acknowledgeRisk: false, attach: true), "butuh konfirmasi");
        reg.PublishWorkflow(wf, "admin", acknowledgeRisk: true, attach: true);
        Assert.Equal("VendorReview", reg.Current.Get("Vendor").Def.Workflow, "DocType memakai workflow");
        Assert.Equal(2, reg.Current.Get("Vendor").Version, "DocType naik versi bersama");
        Assert.Equal(1, reg.WorkflowHistory("VendorReview").Count, "riwayat workflow");

        // Dokumen lama (draft, belum punya state) langsung memakai state awal
        var old = await Docs.GetAsync(U("rina"), "Vendor", "VND-0001");
        Assert.True(old.WorkflowState is null, "dokumen lama belum punya state");
        var actions = await Docs.AvailableActionsAsync(U("rina"), old);
        Assert.True(actions.Any(a => a.Action == "Kirim tinjauan"), "aksi workflow tersedia untuk dokumen lama");
        var moved = await Docs.RunActionAsync(U("rina"), "Vendor", old.Name, "Kirim tinjauan", old.Version, null);
        Assert.Equal("Ditinjau", moved.WorkflowState, "transisi dari state awal");
        var audit = await Docs.GetAuditAsync(U("rina"), "Vendor", old.Name);
        Assert.True(audit.Any(a => a.FromState == "Draft" && a.ToState == "Ditinjau"), "audit mencatat state awal");
        await Assert.Throws<PermissionDeniedException>(() => Docs.RunActionAsync(U("rina"), "Vendor", old.Name, "Aktifkan", moved.Version, null), "role lain tidak bisa");

        var fresh = await Docs.InsertAsync(U("rina"), "Vendor", P(("vendor_name", "PT Baru")));
        Assert.Equal("Draft", fresh.WorkflowState, "dokumen baru mulai dari state awal");

        // Versi berikutnya: versi harus naik
        Assert.True(!reg.PreviewWorkflow(VendorWorkflow(1), attach: false).Valid, "versi sama ditolak");
        var v2 = VendorWorkflow(2); v2.States.Add(new() { Name = "Ditolak", DocStatus = 2, Tone = "negative" }); v2.Transitions.Add(new() { From = "Ditinjau", To = "Ditolak", Action = "Tolak", AllowedRoles = ["Finance"], RequireComment = true });
        reg.PublishWorkflow(v2, "admin", acknowledgeRisk: false, attach: false);
        Assert.Equal(2, reg.Current.Get("Vendor").Workflow!.Def.Version, "workflow v2 aktif tanpa mengubah DocType");
        Assert.Equal(2, reg.Current.Get("Vendor").Version, "DocType tidak ikut naik versi");
    }

    [Test]
    public Task Workflow_dapat_diterbitkan_terpisah_lalu_dihubungkan()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var wf = new WorkflowDef
        {
            Name = "StandaloneReview", Version = 1, InitialState = "Draft",
            States = [new() { Name = "Draft", DocStatus = 0 }, new() { Name = "Reviewed", DocStatus = 1 }],
            Transitions = [new() { From = "Draft", To = "Reviewed", Action = "Review", AllowedRoles = ["Employee"], Condition = "owner == user.id" }],
        };

        var standalonePreview = reg.PreviewWorkflow(wf, attach: false);
        Assert.True(standalonePreview.Valid, "workflow tanpa DocType valid: " + string.Join("; ", standalonePreview.Errors));
        var published = reg.PublishWorkflow(wf, "admin", acknowledgeRisk: false, attach: false);
        Assert.True(published.DocType is null, "workflow diterbitkan tanpa DocType");
        Assert.True(reg.Current.Get("Vendor").Def.Workflow != wf.Name, "DocType tidak berubah saat publish independen");

        var linked = MetadataJson.ParseWorkflow(MetadataJson.Serialize(reg.Workflows.Single(x => x.Name == wf.Name)));
        linked.Version = 2;
        linked.DocType = "Vendor";
        var attachPreview = reg.PreviewWorkflow(linked, attach: true);
        Assert.True(attachPreview.Valid, "workflow dapat dipasang ke DocType: " + string.Join("; ", attachPreview.Errors));
        reg.PublishWorkflow(linked, "admin", acknowledgeRisk: true, attach: true);

        Assert.Equal("Vendor", reg.Workflows.Single(x => x.Name == wf.Name).DocType, "workflow sekarang terikat ke Vendor");
        Assert.Equal(wf.Name, reg.Current.Get("Vendor").Def.Workflow, "DocType menggunakan workflow yang dipilih");
        Assert.Equal(2, reg.WorkflowHistory(wf.Name).Count, "riwayat workflow menyimpan kedua versi");
        return Task.CompletedTask;
    }

    [Test]
    public Task Workflow_validasi_aksi_ganda_dan_nama()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var wf = VendorWorkflow();
        wf.Transitions.Add(new() { From = "Draft", To = "Aktif", Action = "Kirim tinjauan", AllowedRoles = ["Procurement"] });
        var (_, errs) = reg.CompileWorkflowDraft(wf, attach: true);
        Assert.True(errs.Any(e => e.Message.Contains("lebih dari sekali")), "aksi sama dari state sama ditolak");
        var bad = VendorWorkflow(); bad.Name = "Nama dengan spasi";
        Assert.True(reg.CompileWorkflowDraft(bad, attach: false).Errors.Any(e => e.Path == "name"), "nama workflow divalidasi");
        return Task.CompletedTask;
    }

    [Test]
    public async Task DocType_baru_dibuat_dari_nol_lalu_dipakai()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var def = new DocTypeDef
        {
            Name = "PengajuanCuti", Version = 1, Title = "Pengajuan Cuti", Module = "HR",
            Naming = new NamingDef { Pattern = "PC-{YYYY}-{#####}" },
            Fields =
            [
                new FieldDef { Name = "judul", Type = "Data", Label = "Judul", RequiredWhen = "true", InListView = true },
                new FieldDef { Name = "tanggal", Type = "Date", Label = "Tanggal", RequiredWhen = "true", InListView = true },
            ],
            Permissions = [new PermissionDef { Role = "Employee", Actions = ["read", "create", "write", "delete"], Scope = "owner" }],
            ListView = new ListViewDef { TitleField = "judul", SearchFields = ["name", "judul"] },
        };
        var pv = reg.Preview(def);
        Assert.True(pv.Valid && pv.Changes.Single().Description == "DocType baru", "DocType baru valid & aman");
        reg.Publish(def, "admin", acknowledgeRisk: false);
        var doc = await Docs.InsertAsync(U("andi"), "PengajuanCuti", P(("judul", "Cuti tahunan"), ("tanggal", "2026-12-24")));
        Assert.True(doc.Name.StartsWith("PC-") && doc.Name.EndsWith("00001"), "nomor dokumen dari pola");
        Assert.Equal(1, (await Docs.ListAsync(U("andi"), "PengajuanCuti", new ListRequest())).Total, "pemilik melihat dokumennya");
        Assert.Equal(0, (await Docs.ListAsync(U("sari"), "PengajuanCuti", new ListRequest())).Total, "orang lain tidak (scope milik sendiri)");
        await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("andi"), "PengajuanCuti", P(("judul", ""))), "field wajib");
        Assert.True(reg.History("PengajuanCuti").Count == 1, "riwayat versi tercatat");
    }

    // ---------------- Struktur organisasi & gambar ----------------

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Test]
    public async Task Departemen_dinamai_dari_kode_unik_dan_tetap()
    {
        var admin = U("admin");
        var hr = await Docs.InsertAsync(admin, "Department", P(("code", "HR"), ("department_name", "Sumber Daya Manusia"), ("parent_department", "Direksi")));
        Assert.Equal("HR", hr.Name, "nama dokumen = kode");
        var dup = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(admin, "Department", P(("code", "HR"), ("department_name", "Lain"))), "kode ganda");
        Assert.True(dup.Errors.Any(e => e.Path == "code" && (e.Message.Contains("already in use") || e.Message.Contains("sudah dipakai"))), "pesan kode sudah dipakai");
        var bad = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(admin, "Department", P(("code", "../x"), ("department_name", "X"))), "kode tidak aman");
        Assert.True(bad.Errors.Any(e => e.Path == "code"), "format kode divalidasi");
        var fixedCode = await Assert.Throws<ValidationFailedException>(() => Docs.UpdateAsync(admin, "Department", "HR", hr.Version, P(("code", "SDM"))), "kode tidak bisa diubah");
        Assert.True(fixedCode.Errors.Any(e => e.Message.Contains("tidak bisa diubah")), "pesan kode tetap");
        var emp = await Docs.GetAsync(U("andi"), "Employee", "EMP-0001");
        Assert.Equal("IT", emp.Data["department"], "departemen karyawan tetap cocok dengan kode (row scope pengajuan tidak berubah)");
    }

    [Test]
    public async Task Pohon_departemen_dan_atasan_tidak_boleh_bersiklus()
    {
        var admin = U("admin");
        var it = await Docs.GetAsync(admin, "Department", "IT");
        var ex = await Assert.Throws<ValidationFailedException>(() => Docs.UpdateAsync(admin, "Department", "IT", it.Version, P(("parent_department", "Infrastruktur"))), "siklus departemen");
        Assert.True(ex.Errors.Any(e => e.Message.Contains("siklus") && e.Message.Contains("IT → Infrastruktur → IT")), "pesan menyebut rantai siklus");
        var self = await Assert.Throws<ValidationFailedException>(() => Docs.UpdateAsync(admin, "Department", "IT", it.Version, P(("parent_department", "IT"))), "induk diri sendiri");
        Assert.True(self.Errors.Any(e => e.Message.Contains("sendiri")), "pesan diri sendiri");

        var hendra = await Docs.GetAsync(admin, "Employee", "EMP-0008");   // puncak: andi → budi → hendra
        var cyc = await Assert.Throws<ValidationFailedException>(() => Docs.UpdateAsync(admin, "Employee", "EMP-0008", hendra.Version, P(("atasan_id", "EMP-0001"))), "siklus atasan");
        Assert.True(cyc.Errors.Any(e => e.Path == "atasan_id" && e.Message.Contains("siklus")), "atasan bersiklus ditolak");
        var andi = await Docs.GetAsync(admin, "Employee", "EMP-0001");
        Assert.Equal("Budi Santoso", andi.Data["atasan_name"], "nama atasan diambil otomatis (fetchFrom)");
        var moved = await Docs.UpdateAsync(admin, "Employee", "EMP-0001", andi.Version, P(("atasan_id", "EMP-0004")));
        Assert.Equal("Maya Lestari", moved.Data["atasan_name"], "ganti atasan yang sah diterima");
    }

    [Test]
    public async Task Gambar_divalidasi_dari_isi_file_dan_terikat_pemilik()
    {
        var files = sp.GetRequiredService<FileService>();
        var admin = U("admin");
        await Assert.Throws<FileRejectedException>(() => files.UploadImageAsync(admin, "Employee", "foto", new MemoryStream("<svg onload=alert(1)>"u8.ToArray())), "bukan gambar (SVG/teks) ditolak");
        await Assert.Throws<FileRejectedException>(() => files.UploadImageAsync(admin, "Employee", "foto", new MemoryStream(TinyPng.Concat(new byte[FileService.MaxImageBytes]).ToArray())), "lebih dari 2 MB ditolak");
        await Assert.Throws<FileRejectedException>(() => files.UploadImageAsync(admin, "Employee", "jabatan", new MemoryStream(TinyPng)), "field bukan Image ditolak");
        await Assert.Throws<PermissionDeniedException>(() => files.UploadImageAsync(U("andi"), "Employee", "foto", new MemoryStream(TinyPng)), "tanpa izin ubah Employee ditolak");

        var id = await files.UploadImageAsync(admin, "Employee", "foto", new MemoryStream(TinyPng));
        var store = sp.GetRequiredService<IFileStore>();
        Assert.Equal("image/png", (await store.GetInfoAsync("acme", id))!.ContentType, "tipe dikenali dari isi");
        Assert.True(await store.GetInfoAsync("globex", id) is null, "tenant lain tidak bisa melihat file");

        // File milik orang lain (belum tertaut) tidak bisa dipakai
        var rinaFile = await files.UploadImageAsync(U("rina"), "Product", "gambar", new MemoryStream(TinyPng));
        var steal = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("rina"), "Product", P(("product_name", "Curian"), ("gambar", id))), "pakai file orang lain");
        Assert.True(steal.Errors.Any(e => e.Path == "gambar"), "ditolak di field gambar");
        var prod = await Docs.InsertAsync(U("rina"), "Product", P(("product_name", "Dengan gambar"), ("gambar", rinaFile)));
        var info = (await store.GetInfoAsync("acme", rinaFile))!;
        Assert.True(info.DocType == "Product" && info.DocName == prod.Name && info.Field == "gambar", "file ditautkan ke dokumen setelah disimpan");

        var emp = await Docs.GetAsync(admin, "Employee", "EMP-0002");
        var updated = await Docs.UpdateAsync(admin, "Employee", "EMP-0002", emp.Version, P(("foto", id)));
        Assert.Equal(id, updated.Data["foto"], "foto karyawan tersimpan");
        await Assert.Throws<ValidationFailedException>(() => Docs.UpdateAsync(admin, "Employee", "EMP-0002", updated.Version, P(("foto", "f_" + new string('0', 32)))), "id file palsu ditolak");
    }

    // ---------------- Dashboard SQL ----------------

    [Test]
    public Task Dashboard_parameter_dideteksi_di_luar_string_dan_komentar()
    {
        var a = SqlGuard.Analyze("SELECT dept, COUNT(*) AS n FROM reporting.employee -- @Abaikan\nWHERE status = @Status AND note <> '@BukanParam' AND tgl >= @StartDate AND x = @status /* @Juga */ AND tags @> ARRAY['a'] GROUP BY dept;");
        Assert.True(a.Ok, "query valid: " + a.Error);
        Assert.True(a.Parameters.SequenceEqual(["Status", "StartDate"]), "parameter: " + string.Join(",", a.Parameters));
        Assert.True(a.ParameterizedSql.Contains("status = $1") && a.ParameterizedSql.Contains("x = $1") && a.ParameterizedSql.Contains("tgl >= $2"), "nama sama → placeholder sama");
        Assert.True(a.ParameterizedSql.Contains("'@BukanParam'") && a.ParameterizedSql.Contains("@>"), "string literal & operator @> tidak disentuh");
        Assert.True(SqlGuard.Analyze("WITH x AS (SELECT 1) SELECT * FROM x").Ok, "WITH diizinkan");
        Assert.True(!SqlGuard.Analyze("SELECT 1; DROP TABLE docframe_users").Ok, "multi-statement ditolak");
        Assert.True(!SqlGuard.Analyze("DELETE FROM reporting.employee").Ok, "bukan SELECT ditolak");
        Assert.True(!SqlGuard.Analyze("SELECT $$x$$").Ok, "dollar-quoting ditolak");
        Assert.True(!SqlGuard.Analyze("SELECT 'tidak ditutup").Ok, "string tak ditutup ditolak");
        var (_, ps) = DashboardService.DetectParameters("SELECT * FROM t WHERE a >= @StartDate AND b = @IsAktif AND c > @MinTotal AND d = @Status");
        Assert.True(ps[0].Type == ParamTypes.Date && ps[0].Binding == ParamBindings.GlobalStart && ps[1].Type == ParamTypes.Boolean && ps[2].Type == ParamTypes.Number && ps[3].Type == ParamTypes.Text, "tipe & binding awal ditebak dari nama");
        return Task.CompletedTask;
    }

    private IReportingQueryExecutor Exec => sp.GetRequiredService<IReportingQueryExecutor>();
    private async Task<QueryResultSet> Sql(string tenant, string sql, params object?[] args)
    {
        var a = SqlGuard.Analyze(sql);
        if (!a.Ok) throw new QueryRejectedException(a.Error!);
        var typed = args.Select(v => new SqlParam(v, v switch { decimal => ParamTypes.Number, DateOnly => ParamTypes.Date, bool => ParamTypes.Boolean, _ => ParamTypes.Text })).ToList();
        return await Exec.ExecuteAsync(tenant, a.ParameterizedSql, typed, 100);
    }

    [Test, ReportingTest]
    public async Task Dashboard_isolasi_tenant_tidak_bisa_dibobol()
    {
        var acme = await Sql("acme", "SELECT COUNT(*) AS n FROM reporting.purchase_request");
        var globex = await Sql("globex", "SELECT COUNT(*) AS n FROM reporting.purchase_request");
        Assert.Equal(5m, acme.Rows[0][0], "acme melihat 5 pengajuan");
        Assert.Equal(1m, globex.Rows[0][0], "globex melihat 1 pengajuan");
        var escape = await Sql("acme", "SELECT set_config('app.tenant_id', 'globex', false) AS x, (SELECT COUNT(*) FROM reporting.purchase_request WHERE name IN (SELECT name FROM reporting.purchase_request)) AS n");
        Assert.Equal(5m, escape.Rows[0][1], "set_config tidak mengubah tenant (tenant dari tabel sesi)");
        await Assert.Throws<QueryRejectedException>(() => Sql("acme", "SELECT tenant_id FROM public.docframe_reporting_sessions"), "tabel sesi tidak bisa dibaca");
        await Assert.Throws<QueryRejectedException>(() => Sql("acme", "WITH u AS (UPDATE public.docframe_reporting_sessions SET tenant_id = 'globex' RETURNING 1) SELECT COUNT(*) FROM u"), "tabel sesi tidak bisa diubah");
        var noSession = await Sql("acme", "SELECT COUNT(*) AS n FROM reporting.employee");
        Assert.Equal(8m, noSession.Rows[0][0], "view karyawan ter-scope tenant");
    }

    [Test, ReportingTest]
    public async Task Dashboard_hanya_baca_dan_tabel_inti_tertutup()
    {
        var users = await Assert.Throws<QueryRejectedException>(() => Sql("acme", "SELECT user_name, password_hash FROM public.docframe_users"), "tabel akun");
        Assert.True(users.Message.Contains("Akses ditolak"), "pesan akses ditolak");
        await Assert.Throws<QueryRejectedException>(() => Sql("acme", "SELECT COUNT(*) FROM public.docframe_documents"), "tabel dokumen langsung");
        await Assert.Throws<QueryRejectedException>(() => Sql("acme", "WITH d AS (DELETE FROM public.docframe_documents RETURNING 1) SELECT COUNT(*) FROM d"), "DELETE di CTE");
        var still = await Docs.ListAsync(U("dewi"), PR, new ListRequest());
        Assert.Equal(5, still.Total, "data tidak berubah");
        var big = await Sql("acme", "SELECT g FROM generate_series(1, 500) g");
        Assert.True(big.Rows.Count == 100 && big.Truncated, "dibungkus LIMIT (100 baris, ditandai terpotong)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var slow = await Assert.Throws<QueryRejectedException>(() => Sql("acme", "SELECT pg_sleep(10)"), "query lambat");
        Assert.True(slow.Message.Contains("batas waktu") && sw.Elapsed < TimeSpan.FromSeconds(6), "dibatalkan oleh statement_timeout");
    }

    [Test, ReportingTest]
    public async Task Dashboard_parameter_dikirim_native_bukan_digabung()
    {
        const string sql = "SELECT COUNT(*) AS n FROM reporting.purchase_request WHERE workflow_state = @Status";
        Assert.Equal(1m, (await Sql("acme", sql, "Disetujui")).Rows[0][0], "nilai normal");
        Assert.Equal(0m, (await Sql("acme", sql, "Draft' OR '1'='1")).Rows[0][0], "injeksi diperlakukan sebagai teks biasa");
        var dated = await Sql("acme", "SELECT COUNT(*) AS n FROM reporting.purchase_request WHERE request_date >= @Start", DateOnly.FromDateTime(DateTime.Today.AddDays(-3)));
        Assert.True((decimal)dated.Rows[0][0]! is >= 1 and < 5, "parameter tanggal bertipe date");
    }

    [Test, ReportingTest]
    public async Task Dashboard_koneksi_yang_bukan_read_only_ditolak()
    {
        await using var notReadOnly = new Framework.Persistence.PostgreSql.ReadOnlyDataSource(Npgsql.NpgsqlDataSource.Create(PgConnection!));
        var unsafeExec = new Framework.Persistence.PostgreSql.PostgresReportingQueryExecutor(
            new Framework.Persistence.PostgreSql.AnalyticsDataSource(sp.GetRequiredService<Npgsql.NpgsqlDataSource>()),
            notReadOnly,
            new Framework.Persistence.PostgreSql.ReportingOptions(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<Framework.Persistence.PostgreSql.PostgresReportingQueryExecutor>());
        var ex = await Assert.Throws<ReportingUnavailableException>(() => unsafeExec.ExecuteAsync("acme", "SELECT 1", Array.Empty<SqlParam>(), 10), "connection string utama dipakai sebagai read-only");
        Assert.True(ex.Message.Contains("bukan read-only"), "pesan jelas: " + ex.Message);
    }

    [Test, ReportingTest]
    public async Task Dashboard_widget_disimpan_dipin_dan_parameter_diikat_ke_filter()
    {
        var dash = sp.GetRequiredService<DashboardService>();
        var admin = U("admin");
        var widget = new WidgetDef
        {
            Name = "Nilai per status", Description = "uji",
            Config = new WidgetConfig
            {
                Sql = "SELECT workflow_state AS status, SUM(total) AS nilai FROM reporting.purchase_request WHERE request_date >= @StartDate AND (@Dept IS NULL OR department = @Dept) GROUP BY workflow_state",
                Parameters =
                [
                    new() { Name = "StartDate", Type = ParamTypes.Date, DefaultValue = "2000-01-01", Binding = ParamBindings.GlobalStart },
                    new() { Name = "Dept", Type = ParamTypes.Text, DefaultValue = "", Binding = ParamBindings.GlobalDepartment },
                ],
                Visual = new VisualConfig { Type = ChartTypes.Bar, XColumn = "status", YColumns = ["nilai"] },
                Roles = ["Finance", "Manager"],
            },
        };
        var bad = new WidgetDef { Name = "Salah", Config = new WidgetConfig { Sql = widget.Config.Sql, Parameters = widget.Config.Parameters,
            Visual = new VisualConfig { Type = ChartTypes.Bar, XColumn = "nilai", YColumns = ["status"] } } };
        var vis = await Assert.Throws<QueryRejectedException>(() => dash.SaveAsync(admin, bad, pin: false), "sumbu Y teks");
        Assert.True(vis.Message.Contains("bukan angka"), "sumbu Y wajib numerik: " + vis.Message);
        await Assert.Throws<PermissionDeniedException>(() => dash.SaveAsync(U("dewi"), widget, pin: false), "hanya System Manager merancang");

        var saved = await dash.SaveAsync(admin, widget, pin: true);
        Assert.True(saved.Id.StartsWith("w_") && saved.Version == 1, "tersimpan");
        Assert.True((await dash.GetLayoutAsync(admin)).Items.Any(i => i.WidgetId == saved.Id), "di-pin ke dashboard");
        var reloaded = (await sp.GetRequiredService<IWidgetStore>().GetAsync("acme", saved.Id))!;
        Assert.True(reloaded.Config.Visual.YColumns.Single() == "nilai" && reloaded.Config.Parameters.Count == 2, "konfigurasi JSON utuh");

        Assert.True((await dash.ListAsync(U("andi"))).All(w => w.Id != saved.Id), "role lain tidak melihat widget");
        var all = await dash.RunWidgetAsync(U("dewi"), reloaded, new DashboardFilters(null, null, null));
        var it = await dash.RunWidgetAsync(U("dewi"), reloaded, new DashboardFilters(null, null, "Marketing"));
        Assert.True(all.Error is null && it.Error is null, "berjalan: " + (all.Error ?? it.Error));
        decimal Sum(WidgetRun r) => r.Result!.Rows.Sum(x => (decimal)x[1]!);
        Assert.True(Sum(it) < Sum(all) && it.Result!.Rows.Count >= 1, "filter global departemen diikat ke parameter");
        var future = await dash.RunWidgetAsync(U("dewi"), reloaded, new DashboardFilters(DateOnly.FromDateTime(DateTime.Today.AddYears(1)), null, null));
        Assert.Equal(0, future.Result!.Rows.Count, "filter tanggal mulai diikat");
        var denied = await dash.RunWidgetAsync(U("andi"), reloaded, new DashboardFilters(null, null, null));
        Assert.True(denied.Result is null && denied.Error is not null, "role tanpa akses tidak bisa menjalankan");
    }

    [Test, ReportingTest]
    public async Task Dashboard_view_diperbarui_setelah_metadata_dipublikasikan()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var v2 = MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get("Vendor").Def));
        v2.Version = 2; v2.Fields.Add(new FieldDef { Name = "rating", Type = "Int", Label = "Rating" });
        reg.Publish(v2, "admin", acknowledgeRisk: false);
        await sp.SyncReportingSchemaAsync();
        var r = await Sql("acme", "SELECT COUNT(*) AS n, MAX(rating) AS r FROM reporting.vendor");
        Assert.True(r.Columns.Any(c => c.Name == "r" && c.Kind == "number"), "kolom baru tersedia sebagai angka");
        var restricted = await Assert.Throws<QueryRejectedException>(() => Sql("acme", "SELECT budget_code FROM reporting.purchase_request"), "field level izin 1");
        Assert.True(restricted.Message.Contains("budget_code"), "field terbatas tidak diekspos");
    }

    // ---------------- Performa & fondasi Dunia 2 ----------------

    [Test]
    public async Task Outbox_muatan_membawa_snapshot_versi_dan_status()
    {
        var doc = await Docs.GetAsync(U("andi"), PR, "PR-2026-00001");
        var updated = await Docs.UpdateAsync(U("andi"), PR, doc.Name, doc.Version, P(("justification", "Snapshot uji outbox")));
        var msg = sp.GetRequiredService<IOutboxStore>().Snapshot()
            .Single(m => m.Type == "document.updated" && m.Payload["name"] == doc.Name && m.Payload["version"] == updated.Version.ToString());
        Assert.Equal(DocumentService.EventPayloadVersion, msg.Payload["payloadVersion"], "versi bentuk muatan");
        Assert.Equal("0", msg.Payload["docStatus"], "status dokumen saat kejadian");
        Assert.True(DateTimeOffset.TryParse(msg.Payload["occurredAt"], out _), "waktu kejadian ISO 8601");
        using var data = System.Text.Json.JsonDocument.Parse(msg.Payload["data"]!);
        Assert.Equal("Snapshot uji outbox", data.RootElement.GetProperty("justification").GetString(), "snapshot memuat nilai pada versi itu");
        Assert.True(data.RootElement.GetProperty("items").GetArrayLength() > 0, "child table ikut di snapshot");

        // Versi berikutnya tidak mengubah snapshot versi sebelumnya (riwayat tepat).
        await Docs.UpdateAsync(U("andi"), PR, doc.Name, updated.Version, P(("justification", "Berubah lagi")));
        using var again = System.Text.Json.JsonDocument.Parse(sp.GetRequiredService<IOutboxStore>().Snapshot()
            .Single(m => m.Payload["name"] == doc.Name && m.Payload["version"] == updated.Version.ToString() && m.Type == "document.updated").Payload["data"]!);
        Assert.Equal("Snapshot uji outbox", again.RootElement.GetProperty("justification").GetString(), "snapshot lama tetap");
    }

    [Test]
    public async Task Outbox_retensi_hanya_menghapus_yang_sudah_diproses()
    {
        var store = sp.GetRequiredService<IOutboxStore>();
        var clock = new ManualClock();
        var processor = new OutboxProcessor(store, sp.GetServices<IEventHandler>(), clock);
        for (var i = 0; i < 20 && (await processor.ProcessDueAsync()).Processed > 0; i++) { }
        var doc = await Docs.GetAsync(U("andi"), PR, "PR-2026-00001");
        await Docs.UpdateAsync(U("andi"), PR, doc.Name, doc.Version, P(("justification", "Belum diproses")));

        Assert.Equal(0, await processor.PruneAsync(TimeSpan.FromDays(7)), "belum lewat masa simpan → tidak ada yang dihapus");
        clock.Advance(TimeSpan.FromDays(8));
        var pruned = await processor.PruneAsync(TimeSpan.FromDays(7));
        Assert.True(pruned > 0, "pesan yang sudah diproses lebih dari 7 hari dihapus");
        var left = store.Snapshot();
        Assert.True(left.All(m => m.ProcessedAt is null), "yang tersisa hanya pesan yang belum diproses");
        Assert.True(left.Any(m => m.Payload["name"] == doc.Name && m.Type == "document.updated"), "pesan belum diproses tidak ikut terhapus");
    }

    [Test, PgTest]
    public async Task Pg_indeks_dibuat_dari_metadata_dan_dipakai_planner()
    {
        var sync = sp.GetRequiredService<DocumentIndexSync>();
        await sync.SyncAsync();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var names = new List<string>();
        await using (var cmd = db.CreateCommand("SELECT indexname FROM pg_indexes WHERE tablename = 'docframe_documents' AND indexname LIKE 'ix\\_df\\_%'"))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) names.Add(r.GetString(0));
        var desired = DocumentIndexSync.Desired(sp.GetRequiredService<MetadataRegistry>().Current);
        Assert.True(desired.All(d => names.Contains(d.Name)), "semua indeks dari metadata dibuat: " + string.Join(", ", names));
        Assert.True(desired.Any(d => d.DocType == PR && d.Field == "priority" && d.Kind == "btree"), "filter priority → B-tree");
        Assert.True(desired.Any(d => d.DocType == PR && d.Field == "requester_name" && d.Kind == "trgm"), "pencarian → trigram");

        // Ekspresi query harus identik dengan ekspresi indeks. Di dalam transaksi yang di-rollback, hanya indeks yang diuji
        // yang disisakan; buktinya bukan sekadar nama indeks muncul (indeks parsial bisa dipindai penuh tanpa kondisi),
        // melainkan ekspresi field muncul sebagai "Index Cond" — artinya planner mencocokkan ekspresi query dengan indeks.
        var meta = sp.GetRequiredService<MetadataRegistry>().Current.Get(PR);
        async Task<string> IndexCond(string keep, string where)
        {
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var others = new List<string>();
            await using (var q = new NpgsqlCommand("SELECT indexname FROM pg_indexes WHERE tablename = 'docframe_documents' AND indexname <> $1 AND indexname NOT LIKE '%pkey'", conn, tx))
            {
                q.Parameters.AddWithValue(keep);
                await using var r = await q.ExecuteReaderAsync();
                while (await r.ReadAsync()) others.Add(r.GetString(0));
            }
            await using (var prep = new NpgsqlCommand("ALTER TABLE docframe_documents DROP CONSTRAINT docframe_documents_pkey; " +
                string.Concat(others.Select(o => $"DROP INDEX \"{o}\"; ")) + "SET LOCAL enable_seqscan = off;", conn, tx))
                await prep.ExecuteNonQueryAsync();
            await using var ex = new NpgsqlCommand($"EXPLAIN SELECT name FROM docframe_documents d WHERE d.tenant_id = 'acme' AND d.doctype = '{PR}' AND {where}", conn, tx);
            var lines = new List<string>();
            await using (var r = await ex.ExecuteReaderAsync())
                while (await r.ReadAsync()) lines.Add(r.GetString(0));
            await tx.RollbackAsync();
            return string.Join("\n", lines.Where(l => l.Contains("Index Cond")));
        }
        string Idx(string field, string kind) => desired.Single(d => d.DocType == PR && d.Field == field && d.Kind == kind).Name;
        var priorityCond = await IndexCond(Idx("priority", "btree"), $"{DocumentIndexSync.Expression(meta.ByName["priority"], "d.")} = 'Tinggi'");
        Assert.True(priorityCond.Contains("'priority'"), "filter memakai indeks priority sebagai Index Cond: " + priorityCond);
        var searchCond = await IndexCond(Idx("requester_name", "trgm"), $"{DocumentIndexSync.Expression(meta.ByName["requester_name"], "d.")} ILIKE '%Andi%'");
        Assert.True(searchCond.Contains("'requester_name'") && searchCond.Contains("~~*"), "pencarian memakai indeks trigram sebagai Index Cond: " + searchCond);

        // Sinkron ulang idempoten; indeks yang tak lagi dibutuhkan metadata dibuang.
        await sync.SyncAsync();
        await using (var junk = db.CreateCommand("CREATE INDEX ix_df_sisa_lama ON docframe_documents (tenant_id)")) await junk.ExecuteNonQueryAsync();
        await sync.SyncAsync();
        await using var chk = db.CreateCommand("SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_df_sisa_lama'");
        Assert.Equal(0L, await chk.ExecuteScalarAsync(), "indeks terkelola yang tidak lagi dipakai dibuang");
    }

    [Test, PgTest]
    public async Task Pg_hitungan_total_dibatasi()
    {
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        await using (var cmd = db.CreateCommand(
            "INSERT INTO docframe_documents (tenant_id, doctype, name, owner, docstatus, version, schema_version, data, created, modified, modified_by) " +
            $"SELECT 'acme', 'Vendor', 'CAP-' || i, 'rina', 0, 1, 1, jsonb_build_object('vendor_name', 'V' || i), now(), now(), 'rina' FROM generate_series(1, {QueryLimits.CountCap + 50}) i"))
            await cmd.ExecuteNonQueryAsync();
        var list = await Docs.ListAsync(U("rina"), "Vendor", new ListRequest(PageSize: 20));
        Assert.True(list.TotalCapped && list.Total == QueryLimits.CountCap, $"total dibatasi ({list.Total}, capped={list.TotalCapped})");
        Assert.Equal(20, list.Items.Count, "halaman tetap terisi");
        var few = await Docs.ListAsync(U("rina"), "Vendor", new ListRequest(PageSize: 20, Search: "CAP-1000"));
        Assert.True(!few.TotalCapped && few.Total >= 1 && few.Total < 100, "hasil kecil dihitung persis");
    }

    [Test, ReportingTest]
    public async Task Dashboard_di_database_analitik_terpisah_tetap_terisolasi_per_tenant()
    {
        if (PgAnalytics is null || PgAnalyticsRo is null) { Console.Write("    (lewati: set DOCFRAME_TEST_PG_ANALYTICS[_RO]) "); return; }
        var app = sp.GetRequiredService<NpgsqlDataSource>();
        await using var analytics = NpgsqlDataSource.Create(PgAnalytics);
        // Simulasi replika logis: instance analitik punya salinan docframe_documents (di produksi diisi oleh subscription).
        async Task Run(NpgsqlDataSource ds, string sql) { await using var c = ds.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }
        await Run(analytics, "DROP SCHEMA IF EXISTS reporting CASCADE; DROP TABLE IF EXISTS docframe_documents, docframe_reporting_sessions; " +
            "CREATE TABLE docframe_documents (tenant_id text NOT NULL, doctype text NOT NULL, name text NOT NULL, owner text NOT NULL, docstatus smallint NOT NULL, " +
            "workflow_state text, created timestamptz NOT NULL, modified timestamptz NOT NULL, data jsonb NOT NULL, PRIMARY KEY (tenant_id, doctype, name))");
        await using (var read = app.CreateCommand("SELECT tenant_id, doctype, name, owner, docstatus, workflow_state, created, modified, data::text FROM docframe_documents"))
        await using (var r = await read.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                await using var ins = analytics.CreateCommand("INSERT INTO docframe_documents VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb)");
                for (var i = 0; i < 9; i++) ins.Parameters.AddWithValue(r.IsDBNull(i) ? DBNull.Value : r.GetValue(i));
                await ins.ExecuteNonQueryAsync();
            }

        var options = new ReportingOptions { AnalyticsConnectionString = PgAnalytics, StatementTimeoutSeconds = 2 };
        var ads = new AnalyticsDataSource(analytics);
        var logs = sp.GetRequiredService<ILoggerFactory>();
        await new ReportingSchemaSync(ads, sp.GetRequiredService<IMetadataProvider>(), options, logs.CreateLogger<ReportingSchemaSync>()).SyncAsync();
        await using var ro = new ReadOnlyDataSource(NpgsqlDataSource.Create(PgAnalyticsRo));
        var exec = new PostgresReportingQueryExecutor(ads, ro, options, logs.CreateLogger<PostgresReportingQueryExecutor>());

        async Task<long> InsertsOnAppSessions()
        {
            await using var c = app.CreateCommand("SELECT n_tup_ins FROM pg_stat_user_tables WHERE relname = 'docframe_reporting_sessions'");
            return Convert.ToInt64(await c.ExecuteScalarAsync() ?? 0L);
        }
        var before = await InsertsOnAppSessions();
        async Task<decimal> Count(string tenant, string sql) => (decimal)(await exec.ExecuteAsync(tenant, SqlGuard.Analyze(sql).ParameterizedSql, [], 10)).Rows[0][0]!;
        Assert.Equal(5m, await Count("acme", "SELECT COUNT(*) FROM reporting.purchase_request"), "acme di instance analitik");
        Assert.Equal(1m, await Count("globex", "SELECT COUNT(*) FROM reporting.purchase_request"), "globex di instance analitik");
        Assert.Equal(5m, await Count("acme", "SELECT (SELECT COUNT(*) FROM reporting.purchase_request) FROM (SELECT set_config('app.tenant_id', 'globex', false)) x"), "set_config tetap tidak bisa membobol tenant");
        await Assert.Throws<QueryRejectedException>(() => exec.ExecuteAsync("acme", "SELECT tenant_id FROM public.docframe_reporting_sessions", [], 10), "sesi di instance analitik tetap tertutup");
        await Task.Delay(700); // statistik pg_stat dilaporkan berkala
        Assert.Equal(before, await InsertsOnAppSessions(), "sesi ditulis di instance analitik, bukan di database aplikasi");
    }

    [Test, ReportingTest]
    public async Task Dashboard_koneksi_read_only_gagal_tampil_sebagai_pesan_bukan_crash()
    {
        // Gejala di lapangan: role docframe_ro belum dibuat / password beda → dulu exception lolos dan
        // menghentikan halaman dashboard (kartu tertinggal tanpa grafik).
        var badRo = new Npgsql.NpgsqlConnectionStringBuilder(PgReadOnly!) { Password = "password-salah" }.ConnectionString;
        await using var ro = new ReadOnlyDataSource(NpgsqlDataSource.Create(badRo));
        var exec = new PostgresReportingQueryExecutor(new AnalyticsDataSource(sp.GetRequiredService<NpgsqlDataSource>()), ro,
            new ReportingOptions(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresReportingQueryExecutor>());
        var dash = new DashboardService(new InMemoryWidgetStore(), exec, TimeProvider.System);
        var widget = new WidgetDef { Name = "Uji", Config = new WidgetConfig { Sql = "SELECT 1 AS n", Visual = new VisualConfig { Type = ChartTypes.Number, YColumns = ["n"] } } };

        var run = await dash.RunWidgetAsync(U("admin"), widget, new DashboardFilters(null, null, null));
        Assert.True(run.Result is null && run.Error is not null, "widget mengembalikan pesan error, tidak melempar exception");
        Assert.True(run.Error!.Contains("docframe_ro") && run.Error.Contains("reporting-readonly-user.sql"), "pesan menyebut user & cara memperbaiki: " + run.Error);
        var preview = await Assert.Throws<ReportingUnavailableException>(() => dash.PreviewAsync(U("admin"), "SELECT 1 AS n", []), "pratinjau di pembuat widget");
        Assert.True(preview.Message.Contains("Login"), "pratinjau juga memberi pesan yang jelas");
    }

    // ---------------- Khusus PostgreSQL ----------------
    [Test, PgTest]
    public async Task Pg_workflow_dan_doctype_baru_bertahan_setelah_restart()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        reg.PublishWorkflow(VendorWorkflow(), "admin", acknowledgeRisk: true, attach: true);
        var restarted = await BuildProviderAsync();
        var reg2 = restarted.GetRequiredService<MetadataRegistry>();
        Assert.Equal("VendorReview", reg2.Current.Get("Vendor").Def.Workflow, "DocType tetap memakai workflow");
        Assert.Equal(3, reg2.Current.Get("Vendor").Workflow!.Def.States.Count, "definisi workflow dimuat dari database");
        Assert.Equal(1, reg2.WorkflowHistory("VendorReview").Count, "riwayat workflow tersimpan");
    }


    [Test, PgTest]
    public async Task Pg_data_dan_metadata_bertahan_setelah_restart()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var v2 = MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get("Vendor").Def));
        v2.Version = 2; v2.Fields[0].Label = "Nama perusahaan";
        reg.Publish(v2, "admin", acknowledgeRisk: false);
        var vendor = await Docs.InsertAsync(U("rina"), "Vendor", P(("vendor_name", "PT Setelah Restart")));

        // "Restart": provider baru pada database yang sama, seeder dijalankan lagi.
        var restarted = await BuildProviderAsync();
        await SeedAsync(restarted);
        var reg2 = restarted.GetRequiredService<MetadataRegistry>();
        Assert.Equal(2, reg2.Current.Get("Vendor").Version, "versi metadata hasil publish tetap aktif");
        Assert.Equal("Nama perusahaan", reg2.Current.Get("Vendor").ByName["vendor_name"].Label, "definisi v2 dimuat dari database");
        var docs2 = restarted.GetRequiredService<DocumentService>();
        var again = await docs2.GetAsync(U("rina"), "Vendor", vendor.Name);
        Assert.Equal("PT Setelah Restart", again.Data["vendor_name"], "dokumen tersimpan");
        var employees = await docs2.ListAsync(U("admin"), "Employee", new ListRequest());
        Assert.Equal(8, employees.Total, "seeder tidak mengisi ulang data");
        var pr = await docs2.GetAsync(U("andi"), PR, "PR-2026-00001");
        Assert.True(pr.Data["total"] is decimal && ((List<Dictionary<string, object?>>)pr.Data["items"]!)[0]["qty"] is decimal, "tipe data dipulihkan sesuai metadata");
        Assert.True(pr.Data["required_by"] is string s && s.Length == 10, "tanggal tetap yyyy-MM-dd");
    }

    [Test, PgTest]
    public async Task Pg_update_bersamaan_dari_dua_instance()
    {
        var other = (await BuildProviderAsync()).GetRequiredService<DocumentService>();
        var a = Docs.UpdateAsync(U("andi"), PR, "PR-2026-00001", 1, P(("priority", "Rendah")));
        var b = other.UpdateAsync(U("andi"), PR, "PR-2026-00001", 1, P(("priority", "Tinggi"), ("justification", "x")));
        var results = await Task.WhenAll(Wrap(a), Wrap(b));
        Assert.Equal(1, results.Count(r => r is null), "tepat satu update berhasil");
        Assert.Equal(1, results.Count(r => r is ConcurrencyException), "update lainnya ditolak sebagai konflik versi");
        static async Task<Exception?> Wrap(Task t) { try { await t; return null; } catch (Exception ex) { return ex; } }
    }

    [Test, PgTest]
    public async Task Pg_nomor_seri_unik_saat_insert_paralel()
    {
        var other = (await BuildProviderAsync()).GetRequiredService<DocumentService>();
        var tasks = Enumerable.Range(0, 30).Select(i => (i % 2 == 0 ? Docs : other)
            .InsertAsync(U("rina"), "Vendor", P(("vendor_name", $"Paralel {i}"))));
        var docs = await Task.WhenAll(tasks);
        Assert.Equal(30, docs.Select(d => d.Name).Distinct().Count(), "30 nomor unik dari dua instance");
    }

    [Test, PgTest]
    public async Task Pg_outbox_tidak_diproses_ganda_oleh_dua_worker()
    {
        var seen = new ConcurrentBag<Guid>();
        var counter = new CountingHandler(seen);
        var sp2 = await BuildProviderAsync();
        var w1 = new OutboxProcessor(sp.GetRequiredService<IOutboxStore>(), [counter], TimeProvider.System);
        var w2 = new OutboxProcessor(sp2.GetRequiredService<IOutboxStore>(), [counter], TimeProvider.System);
        var total = sp.GetRequiredService<IOutboxStore>().Snapshot().Count;
        for (var round = 0; round < 5; round++) await Task.WhenAll(w1.ProcessDueAsync(), w2.ProcessDueAsync());
        Assert.Equal(total, seen.Count, "setiap pesan diproses tepat sekali");
        Assert.Equal(total, seen.Distinct().Count(), "tidak ada pesan ganda");
    }

    private sealed class CountingHandler(ConcurrentBag<Guid> seen) : IEventHandler
    {
        public bool CanHandle(string eventType) => true;
        public async Task HandleAsync(OutboxMessage m, CancellationToken ct) { await Task.Delay(2, ct); seen.Add(m.Id); }
    }

    [Test, PgTest]
    public async Task Pg_injeksi_sql_diperlakukan_sebagai_teks()
    {
        var evil = "'; DROP TABLE docframe_documents; --";
        var r1 = await Docs.ListAsync(U("dewi"), PR, new ListRequest(Search: evil));
        Assert.Equal(0, r1.Total, "pencarian berbahaya tidak cocok apa pun");
        var r2 = await Docs.ListAsync(U("dewi"), PR, new ListRequest([new FilterClause("priority", "=", "x' OR '1'='1")]));
        Assert.Equal(0, r2.Total, "filter berbahaya tidak cocok apa pun");
        var r3 = await Docs.ListAsync(U("dewi"), PR, new ListRequest(Search: "100%_"));
        Assert.Equal(0, r3.Total, "wildcard LIKE di-escape");
        var all = await Docs.ListAsync(U("dewi"), PR, new ListRequest());
        Assert.True(all.Total >= 5, "tabel tetap utuh");
    }

    [Test, PgTest]
    public async Task Pg_transaksi_atomik_rollback_saat_konflik()
    {
        var store = sp.GetRequiredService<IDocumentStore>();
        var existing = (await store.GetAsync("acme", PR, "PR-2026-00001"))!;
        var fresh = existing.Clone();
        var newDoc = new Document
        {
            DocType = "Vendor", Name = "VND-ATOMIK", TenantId = "acme", Owner = "rina", Created = DateTimeOffset.UtcNow,
            Modified = DateTimeOffset.UtcNow, ModifiedBy = "rina", Version = 1, SchemaVersion = 1, Data = new() { ["vendor_name"] = "Atomik" }
        };
        var uow = new UnitOfWork();
        uow.Upserts.Add((newDoc, null));
        uow.Upserts.Add((fresh, 99)); // versi salah → konflik
        uow.Audits.Add(new AuditEntry { TenantId = "acme", DocType = "Vendor", DocName = "VND-ATOMIK", Action = "Dibuat", Actor = "x", At = DateTimeOffset.UtcNow });
        await Assert.Throws<ConcurrencyException>(() => store.CommitAsync(uow), "commit gagal");
        Assert.True(await store.GetAsync("acme", "Vendor", "VND-ATOMIK") is null, "dokumen baru ikut dibatalkan");
        Assert.Equal(0, (await store.GetAuditAsync("acme", "Vendor", "VND-ATOMIK")).Count, "audit ikut dibatalkan");
    }
}
