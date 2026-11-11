using System.Text.Json;
using Framework.Communication;
using Framework.Documents;
using Framework.Integrations;
using Framework.Metadata;
using Framework.Security;
using Framework.Scripting;
using Framework.Scripting.Abstractions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Regression tests for the Phase 3 review findings (server script sandbox, Auto Repeat, webhooks, email HTML).</summary>
sealed partial class Tests
{
    private sealed class FixedScriptingSwitch(bool enabled) : IScriptingSwitch
    {
        public bool IsEnabled => enabled;
    }

    private static ScriptContext ScriptTestContext(ScriptBudget budget, ScriptDoc? doc = null) => new()
    {
        Doc = doc,
        User = new ScriptUser { Id = "test" },
        Docs = null!,
        Log = new ScriptLog(),
        Email = null!,
        __budget = budget
    };

    // ---------------------------------------------------------------- finding 1: sandbox bypasses

    [Test]
    public Task Phase3Fix_sandbox_rejects_known_bypasses()
    {
        string[] attacks =
        [
            // name-based denylist bypasses
            """System.IO.File.ReadAllText("/etc/passwd");""",
            """System.IO.@File.ReadAllText("/etc/passwd");""",
            // reflection without any "forbidden" identifier
            """var t = typeof(object).Module.GetType("System.IO.File");""",
            """var a = "".GetType().Assembly;""",
            """var m = typeof(string).GetMethods();""",
            // background work outside the budget
            """Task.Factory.StartNew(() => { while (true) { } });""",
            """System.Threading.Tasks.Task.Run(() => 1);""",
            """var timer = new System.Threading.Timer(_ => { }, null, 0, 1);""",
            """System.Threading.Tasks.Parallel.For(0, 10, i => { });""",
            """CancellationToken.Register(() => { });""",
            // process / environment / console / activator
            """System.Environment.Exit(0);""",
            """System.Console.WriteLine("x");""",
            """var o = System.Activator.CreateInstance<object>();""",
            // host state
            """System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;""",
            // ReDoS
            """var r = new System.Text.RegularExpressions.Regex("(a+)+$");""",
            // budget tampering
            """__budget = null;""",
            """ctx.__budget.Tick();""",
            // constructs that escape instrumentation or crash the process
            """void F() => F(); F();""",
            """L: goto L;""",
            """var q = from x in new[] { 1 } select x;""",
            """dynamic d = 1;""",
            // escaping the generated method body
            "} } public static class Evil { static Evil() { } } public static class X { public static void M() { {",
            "#pragma warning disable\nvar x = 1;",
            "using System.IO;\nvar x = 1;",
        ];

        foreach (var code in attacks)
        {
            var errors = ScriptCompiler.Validate(code);
            Assert.True(errors.Count > 0, $"Validate must reject: {code}");

            var threw = false;
            try { using var _ = ScriptCompiler.Compile("attack", 1, code); }
            catch (ScriptSecurityException) { threw = true; }
            catch (ScriptCompilationException) { threw = true; }
            Assert.True(threw, $"Compile must reject: {code}");
        }

        return Task.CompletedTask;
    }

    [Test]
    public async Task Phase3Fix_sandbox_still_allows_ordinary_scripts()
    {
        const string code = """
var items = new List<decimal> { 1m, 2m, 3m };
var total = items.Where(x => x > 1m).Sum();
var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 1 };
var sb = new StringBuilder();
items.ForEach(x => sb.Append(x));
Func<decimal, decimal> twice = x => x * 2;
try { total += twice(Math.Round(1.234m, 2)); }
catch (InvalidOperationException ex) { Log.Warn(ex.Message); }
foreach (var kv in names) { total += kv.Value; }
var label = $"Total {total:N2} on {DateTime.Today:yyyy-MM-dd} ({sb})";
Log.Info(label);
ctx.Result = total;
""";

        var errors = ScriptCompiler.Validate(code);
        Assert.True(errors.Count == 0, "Ordinary script is valid: " + string.Join(" | ", errors));

        using var compiled = ScriptCompiler.Compile("ordinary", 1, code);
        var ctx = ScriptTestContext(new ScriptBudget());
        await compiled.Runner(ctx);
        Assert.Equal(8.46m, Convert.ToDecimal(ctx.Result), "Ordinary script computed the expected value");
    }

    [Test]
    public Task Phase3Fix_compile_errors_report_the_users_line_number()
    {
        var errors = ScriptCompiler.Validate("var a = 1;\nvar b = undefinedName;");
        Assert.True(errors.Any(e => e.StartsWith("Line 2:")), "Error is reported on the user's line 2: " + string.Join(" | ", errors));
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- finding 2: budget

    [Test]
    public async Task Phase3Fix_budget_covers_lambdas_catch_blocks_time_and_recursion()
    {
        async Task<string?> RunAsync(string code, ScriptBudget budget)
        {
            using var compiled = ScriptCompiler.Compile("budget", 1, code);
            try { await compiled.Runner(ScriptTestContext(budget)); return null; }
            catch (ScriptBudgetExceededException ex) { return ex.Message; }
        }

        // LINQ callbacks tick (previously only loop statements did).
        var linq = await RunAsync("var n = Enumerable.Range(0, int.MaxValue).Where(x => x >= 0).Count();", new ScriptBudget(maxStatements: 200));
        Assert.True(linq is not null, "Lambda bodies consume the statement budget");

        // A tripped budget cannot be swallowed by try/catch.
        var swallow = await RunAsync("while (true) { try { while (true) { } } catch { } }", new ScriptBudget(maxStatements: 200));
        Assert.True(swallow is not null, "catch blocks re-throw a tripped budget");

        // Wall-clock limit is enforced by the tick itself, not only by a cancellation token nobody checks.
        var slow = await RunAsync("var i = 0; while (true) { i++; }", new ScriptBudget(maxStatements: int.MaxValue, timeout: TimeSpan.FromMilliseconds(200)));
        Assert.True(slow?.Contains("time limit") == true, "Time limit stops a busy loop: " + slow);

        // Unbounded recursion through a delegate stops before a StackOverflowException kills the process.
        var recursion = await RunAsync("Func<int, int> f = null!; f = x => f(x + 1) + 1; f(0);", new ScriptBudget(maxStatements: int.MaxValue, timeout: TimeSpan.FromSeconds(5)));
        Assert.True(recursion?.Contains("recursion") == true, "Deep recursion is stopped: " + recursion);
    }

    // ---------------------------------------------------------------- finding 3: field permission level

    [Test]
    public async Task Phase3Fix_doc_event_script_cannot_write_fields_above_users_perm_level()
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var user = U("andi"); // requester: no write access to PermLevel 1 field 'budget_code'

        foreach (var (id, code) in new[]
        {
            ("scr_permlevel_indexer", "Doc[\"budget_code\"] = \"BUDGET-HACK\";"),
            ("scr_permlevel_data", "Doc.Data[\"budget_code\"] = \"BUDGET-HACK\";"),
        })
        {
            await store.SaveAsync(new ServerScriptDef
            {
                Id = id,
                TenantId = "acme",
                Name = "PermLevel bypass attempt",
                ScriptType = ServerScriptType.DocEvent,
                DocType = PR,
                DocEvent = DocEventStage.BeforeValidate,
                IsActive = true,
                CurrentCode = code
            }, code, user.Id);

            try
            {
                await Assert.Throws<PermissionDeniedException>(() => Docs.InsertAsync(user, PR, P(
                    ("vendor", "VND-0001"),
                    ("request_date", ReqDate()),
                    ("required_by", ReqDate(5)),
                    ("justification", "Regular order"),
                    ("items", Items(1, 100000)))), $"Script '{id}' may not set budget_code for andi");
            }
            finally
            {
                await store.DeleteAsync("acme", id);
            }
        }
    }

    // ---------------------------------------------------------------- findings 4-6: run-as, API roles, kill switch

    [Test]
    public async Task Phase3Fix_scheduled_script_without_run_as_user_does_not_run()
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var service = sp.GetRequiredService<ServerScriptExecutionService>();
        const string code = "Log.Info(\"should not run\");";

        await store.SaveAsync(new ServerScriptDef
        {
            Id = "scr_no_runas",
            TenantId = "acme",
            Name = "No run-as",
            ScriptType = ServerScriptType.Scheduled,
            CronExpr = "@hourly",
            RunAsUserId = null,
            IsActive = true,
            CurrentCode = code
        }, code, "admin");

        try
        {
            await Assert.Throws<InvalidOperationException>(() => service.ExecuteScheduledAsync("acme", "scr_no_runas"), "No implicit Administrator");
            var runs = await store.ListRunsAsync("acme", "scr_no_runas");
            Assert.True(runs.Count == 1 && runs[0].Status == "Failed", "The refused run is visible in the run log");
        }
        finally
        {
            await store.DeleteAsync("acme", "scr_no_runas");
        }
    }

    [Test]
    public async Task Phase3Fix_api_script_enforces_roles_and_returns_null_results()
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var service = sp.GetRequiredService<ServerScriptExecutionService>();
        const string code = "ctx.Result = null;";

        await store.SaveAsync(new ServerScriptDef
        {
            Id = "scr_admin_only",
            TenantId = "acme",
            Name = "Admin-only method",
            ScriptType = ServerScriptType.Api,
            ApiMethod = "admin_only",
            AllowedRoles = [],
            IsActive = true,
            CurrentCode = code
        }, code, "admin");

        try
        {
            await Assert.Throws<PermissionDeniedException>(() => service.ExecuteApiAsync(U("andi"), "admin_only", null), "Empty AllowedRoles = admins only");

            var asAdmin = await service.ExecuteApiAsync(U("admin"), "admin_only", null);
            Assert.True(asAdmin.Found && asAdmin.Data is null, "A null result is still a found method (no 404 after side effects)");

            var missing = await service.ExecuteApiAsync(U("admin"), "does_not_exist", null);
            Assert.True(!missing.Found, "Unknown method is reported as not found");
        }
        finally
        {
            await store.DeleteAsync("acme", "scr_admin_only");
        }
    }

    [Test]
    public async Task Phase3Fix_kill_switch_stops_all_script_types()
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var disabled = ActivatorUtilities.CreateInstance<ServerScriptExecutionService>(sp, (IScriptingSwitch)new FixedScriptingSwitch(false));
        const string code = "ctx.Result = 1;";

        await store.SaveAsync(new ServerScriptDef
        {
            Id = "scr_switched_off",
            TenantId = "acme",
            Name = "Switched off",
            ScriptType = ServerScriptType.Api,
            ApiMethod = "switched_off",
            IsActive = true,
            CurrentCode = code
        }, code, "admin");

        try
        {
            Assert.True(!disabled.IsEnabled, "Switch reports disabled");
            var result = await disabled.ExecuteApiAsync(U("admin"), "switched_off", null);
            Assert.True(!result.Found, "Api scripts do not run while switched off");
            var runs = await store.ListRunsAsync("acme", "scr_switched_off");
            Assert.True(runs.Count == 0, "Nothing was executed");
        }
        finally
        {
            await store.DeleteAsync("acme", "scr_switched_off");
            disabled.Dispose();
        }
    }

    // ---------------------------------------------------------------- findings 7-8: Auto Repeat

    [Test]
    public async Task Phase3Fix_auto_repeat_claims_a_period_before_creating_its_document()
    {
        var store = new InMemoryAutoRepeatStore();
        var day = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.True(await store.TryClaimRunAsync("acme", "rep_claim", day), "First claim wins");
        Assert.True(!await store.TryClaimRunAsync("acme", "rep_claim", day), "Second claim for the same period loses");

        await store.CompleteRunAsync(new AutoRepeatRun
        {
            Id = Guid.NewGuid(), TenantId = "acme", AutoRepeatId = "rep_claim", ScheduledFor = day,
            GeneratedDocName = "", Status = AutoRepeatRunStatus.Failed, Error = "boom"
        });
        Assert.True(await store.TryClaimRunAsync("acme", "rep_claim", day), "A failed period may be claimed again");

        await store.CompleteRunAsync(new AutoRepeatRun
        {
            Id = Guid.NewGuid(), TenantId = "acme", AutoRepeatId = "rep_claim", ScheduledFor = day,
            GeneratedDocName = "PR-X", Status = AutoRepeatRunStatus.Success
        });
        Assert.True(!await store.TryClaimRunAsync("acme", "rep_claim", day), "A finished period is never claimed again");
    }

    [Test]
    public async Task Phase3Fix_auto_repeat_does_not_duplicate_after_interrupted_run()
    {
        var autoRepeatStore = new InMemoryAutoRepeatStore();
        var service = new AutoRepeatService(
            autoRepeatStore,
            Docs,
            sp.GetRequiredService<IMetadataProvider>(),
            new AutomationIdentity(sp.GetRequiredService<IUserAccountRepository>()),
            sp.GetRequiredService<IPermissionService>());

        var user = U("andi");
        var source = await Docs.InsertAsync(user, PR, P(
            ("vendor", "VND-0001"),
            ("request_date", ReqDate()),
            ("required_by", ReqDate(5)),
            ("items", Items(1, 100000))));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var repeat = new AutoRepeatDef
        {
            Id = "rep_no_dup",
            TenantId = "acme",
            Name = "No duplicates",
            ReferenceDocType = PR,
            ReferenceDocName = source.Name,
            Frequency = AutoRepeatFrequency.Daily,
            StartDate = today,
            NextScheduleDate = today,
            Status = AutoRepeatStatus.Active,
            RunAsUserId = "andi"
        };
        await autoRepeatStore.SaveAsync(repeat);

        var first = (await service.ExecuteDueAsync("acme", today)).Single(s => s.AutoRepeatId == repeat.Id);
        Assert.Equal(1, first.DocumentsGenerated, "First run creates today's document");

        // Simulate a crash after the document was created but before next_date was saved.
        await autoRepeatStore.SaveAsync(repeat);
        var second = (await service.ExecuteDueAsync("acme", today)).Single(s => s.AutoRepeatId == repeat.Id);
        Assert.Equal(0, second.DocumentsGenerated, "The same period is not generated twice");
    }

    [Test]
    public async Task Phase3Fix_auto_repeat_service_enforces_run_as_and_ownership()
    {
        var autoRepeatStore = new InMemoryAutoRepeatStore();
        var service = new AutoRepeatService(
            autoRepeatStore,
            Docs,
            sp.GetRequiredService<IMetadataProvider>(),
            new AutomationIdentity(sp.GetRequiredService<IUserAccountRepository>()),
            sp.GetRequiredService<IPermissionService>());

        var andi = U("andi");
        var source = await Docs.InsertAsync(andi, PR, P(
            ("vendor", "VND-0001"),
            ("request_date", ReqDate()),
            ("required_by", ReqDate(5)),
            ("items", Items(1, 100000))));

        SaveAutoRepeatRequest Request(string? runAs, string? id = null) => new(
            Id: id,
            Name: "Monthly copy",
            ReferenceDocType: PR,
            ReferenceDocName: source.Name,
            Frequency: AutoRepeatFrequency.Monthly,
            StartDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1),
            EndDate: null,
            RunAsUserId: runAs);

        await Assert.Throws<PermissionDeniedException>(() => service.SaveAsync(andi, Request("admin")), "A non-admin may not run a schedule as someone else");

        var own = await service.SaveAsync(andi, Request(null));
        Assert.Equal("andi", own.RunAsUserId, "Default run-as user is the creator");

        await Assert.Throws<PermissionDeniedException>(() => service.ExecuteRepeatNowAsync(U("sari"), own.Id), "Another user may not trigger it");
        await Assert.Throws<PermissionDeniedException>(() => service.SaveAsync(U("sari"), Request(null, own.Id)), "Another user may not take it over");
    }

    // ---------------------------------------------------------------- finding 9: webhook deliveries stuck in 'sending'

    [Test]
    public async Task Phase3Fix_webhook_delivery_left_in_sending_is_reclaimed_after_lease()
    {
        var store = new InMemoryWebhookStore();
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            WebhookId = "wh_reclaim",
            EventId = Guid.NewGuid(),
            EventType = "document.submit",
            DocName = "PR-1",
            PayloadJson = "{}",
            NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.EnqueueAsync(delivery);

        // A worker claims it with a lease that has already expired, then "crashes" without updating the row.
        var firstClaim = await store.ClaimDueAsync(limit: 10, leaseFor: TimeSpan.Zero);
        Assert.True(firstClaim.Count == 1, "Delivery claimed once");

        await Task.Delay(10);
        var reclaimed = await store.ClaimDueAsync(limit: 10, leaseFor: TimeSpan.FromMinutes(2));
        Assert.True(reclaimed.Any(d => d.Id == delivery.Id), "A 'sending' delivery with an expired lease is reclaimed");

        var whileLeased = await store.ClaimDueAsync(limit: 10, leaseFor: TimeSpan.FromMinutes(2));
        Assert.True(!whileLeased.Any(d => d.Id == delivery.Id), "It is not reclaimed while its lease is still valid");
    }

    // ---------------------------------------------------------------- finding 10: email HTML

    [Test]
    public Task Phase3Fix_email_html_is_sanitized_before_rendering()
    {
        var clean = HtmlContentSanitizer.Sanitize("<p onclick=\"steal()\">Hi</p><img src=x onerror=alert(1)><script>alert(2)</script><b>ok</b>")!;
        Assert.True(!clean.Contains("onerror") && !clean.Contains("onclick") && !clean.Contains("<script"), "Script vectors removed: " + clean);
        Assert.True(clean.Contains("<b>ok</b>"), "Harmless formatting is kept");
        Assert.True(HtmlContentSanitizer.Sanitize(null) is null, "Null stays null");
        return Task.CompletedTask;
    }
}
