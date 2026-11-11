using System.Text.Json;
using Framework.Documents;
using Framework.Metadata;
using Framework.Security;
using Framework.Scripting;
using Framework.Scripting.Abstractions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Unit & regression tests for Phase 3 Workstream C: C# Server Scripting (Roslyn Sandbox).</summary>
sealed partial class Tests
{
    [Test]
    public Task ServerScript_sandbox_analyzer_blocks_forbidden_namespaces_and_types()
    {
        // 1. Forbidden namespaces in using directives
        var usingIoCode = "using System.IO;\nDoc[\"val\"] = 123;";
        var ioErrors = ScriptCompiler.Validate(usingIoCode);
        Assert.True(ioErrors.Any(e => e.Contains("System.IO")), "Blocked using System.IO");

        var usingDiagCode = "using System.Diagnostics;\nDoc[\"val\"] = 123;";
        var diagErrors = ScriptCompiler.Validate(usingDiagCode);
        Assert.True(diagErrors.Any(e => e.Contains("System.Diagnostics")), "Blocked using System.Diagnostics");

        var usingReflCode = "using System.Reflection;\nDoc[\"val\"] = 123;";
        var reflErrors = ScriptCompiler.Validate(usingReflCode);
        Assert.True(reflErrors.Any(e => e.Contains("System.Reflection")), "Blocked using System.Reflection");

        // 2. Forbidden type invocations (File, Process, Environment, Assembly)
        var fileCode = "File.ReadAllText(\"test.txt\");";
        var fileErrors = ScriptCompiler.Validate(fileCode);
        Assert.True(fileErrors.Any(e => e.Contains("File")), "Blocked File access");

        var procCode = "Process.Start(\"calc.exe\");";
        var procErrors = ScriptCompiler.Validate(procCode);
        Assert.True(procErrors.Any(e => e.Contains("Process")), "Blocked Process invocation");

        var envCode = "Environment.Exit(0);";
        var envErrors = ScriptCompiler.Validate(envCode);
        Assert.True(envErrors.Any(e => e.Contains("Environment")), "Blocked Environment invocation");

        // 3. Unsafe code, dynamic, and background thread spawning
        var unsafeCode = "unsafe { int x = 10; int* p = &x; }";
        var unsafeErrors = ScriptCompiler.Validate(unsafeCode);
        Assert.True(unsafeErrors.Any(e => e.Contains("unsafe")), "Blocked unsafe code block");

        var dynamicCode = "dynamic x = 123;\nDoc[\"val\"] = x;";
        var dynamicErrors = ScriptCompiler.Validate(dynamicCode);
        Assert.True(dynamicErrors.Any(e => e.Contains("dynamic")), "Blocked dynamic keyword");

        var taskRunCode = "Task.Run(() => 123);";
        var taskRunErrors = ScriptCompiler.Validate(taskRunCode);
        Assert.True(taskRunErrors.Any(e => e.Contains("Task.Run")), "Blocked Task.Run background thread spawning");

        return Task.CompletedTask;
    }

    [Test]
    public async Task ServerScript_statement_budget_blocks_infinite_loops()
    {
        // 1. Infinite loop must exceed budget and throw ScriptBudgetExceededException
        var infiniteLoopCode = "while (true) { }";
        using var compiled = ScriptCompiler.Compile("test_infinite", 1, infiniteLoopCode);

        var budget = new ScriptBudget(maxStatements: 200);
        var ctx = new ScriptContext
        {
            User = new ScriptUser { Id = "test" },
            Docs = null!,
            Log = new ScriptLog(),
            Email = null!,
            __budget = budget
        };

        var threwBudget = false;
        try
        {
            await compiled.Runner(ctx);
        }
        catch (ScriptBudgetExceededException)
        {
            threwBudget = true;
        }

        Assert.True(threwBudget, "Infinite loop was terminated by statement budget");
        Assert.True(budget.Count > 200, "Statement counter incremented beyond limit");

        // 2. Normal loop within budget succeeds cleanly
        var normalLoopCode = """
int sum = 0;
for (int i = 0; i < 50; i++)
{
    sum += i;
}
Doc["total_sum"] = (decimal)sum;
""";
        using var compiledNormal = ScriptCompiler.Compile("test_normal", 1, normalLoopCode);
        var doc = new ScriptDoc(new Dictionary<string, object?>());
        var normalBudget = new ScriptBudget(maxStatements: 50_000);
        var normalCtx = new ScriptContext
        {
            Doc = doc,
            User = new ScriptUser { Id = "test" },
            Docs = null!,
            Log = new ScriptLog(),
            Email = null!,
            __budget = normalBudget
        };

        await compiledNormal.Runner(normalCtx);
        Assert.Equal(1225m, doc.GetDecimal("total_sum"), "Normal loop completed and computed correct sum");
        Assert.True(normalBudget.Count >= 50, "Statement count recorded for iterations");
    }

    [Test]
    public Task ServerScript_collectible_assembly_load_context_unloads()
    {
        var code = "Doc[\"greeting\"] = \"Hello World\";";
        var compiled = ScriptCompiler.Compile("test_collectible", 1, code);
        Assert.True(compiled.IsAlive, "AssemblyLoadContext is alive after compilation");

        compiled.Dispose();
        Assert.True(!compiled.IsAlive, "AssemblyLoadContext unloaded upon disposal");

        return Task.CompletedTask;
    }

    [Test]
    public async Task ServerScript_doc_event_modifies_document_payload_before_save()
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var user = U("andi");

        var scriptId = "scr_modify_pr";
        var scriptCode = """
Doc["justification"] = "Overridden by Server Script";
Log.Info("Justification set successfully");
""";
        var scriptDef = new ServerScriptDef
        {
            Id = scriptId,
            TenantId = "acme",
            Name = "PR Justification Setter",
            ScriptType = ServerScriptType.DocEvent,
            DocType = PR,
            DocEvent = DocEventStage.BeforeValidate,
            IsActive = true,
            Version = 1,
            CurrentCode = scriptCode
        };

        await store.SaveAsync(scriptDef, scriptCode, user.Id);

        // Insert document through DocumentService
        var inserted = await Docs.InsertAsync(user, PR, P(
            ("vendor", "VND-0001"),
            ("request_date", ReqDate()),
            ("required_by", ReqDate(5)),
            ("justification", "Original justification"),
            ("items", Items(1, 100000))
        ));

        Assert.Equal("Overridden by Server Script", inserted.Data["justification"], "Document justification was modified by Server Script");

        // Clean up
        await store.DeleteAsync("acme", scriptId);
    }

    [Test]
    public async Task ServerScript_doc_event_validation_failure_rejects_document()
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var user = U("andi");

        var scriptId = "scr_reject_pr";
        var scriptCode = """
if (Doc.GetString("vendor") == "VND-BLOCKED")
{
    throw new ScriptValidationException("Vendor is on the blocked list!", "vendor");
}
""";
        var scriptDef = new ServerScriptDef
        {
            Id = scriptId,
            TenantId = "acme",
            Name = "Block Blacklisted Vendor",
            ScriptType = ServerScriptType.DocEvent,
            DocType = PR,
            DocEvent = DocEventStage.BeforeValidate,
            IsActive = true,
            Version = 1,
            CurrentCode = scriptCode
        };

        await store.SaveAsync(scriptDef, scriptCode, user.Id);

        // Attempting to insert blocked vendor must fail
        var threwValidation = false;
        try
        {
            await Docs.InsertAsync(user, PR, P(
                ("vendor", "VND-BLOCKED"),
                ("request_date", ReqDate()),
                ("required_by", ReqDate(5)),
                ("justification", "Regular monthly order"),
                ("items", Items(1, 100000))
            ));
        }
        catch (ValidationFailedException ex)
        {
            threwValidation = true;
            Assert.True(ex.Errors.Any(e => e.Message.Contains("blocked list")), "Error message matches script validation");
        }

        Assert.True(threwValidation, "Document insert was rejected by Server Script validation");

        // Clean up
        await store.DeleteAsync("acme", scriptId);
    }

    [Test]
    public async Task ServerScript_api_script_executes_and_returns_result()
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var executionService = sp.GetRequiredService<ServerScriptExecutionService>();
        var user = U("andi");

        var scriptId = "scr_calc_tax";
        var scriptCode = """
var baseAmount = Values.ContainsKey("amount") ? Convert.ToDecimal(Values["amount"]) : 0m;
var rate = Values.ContainsKey("rate") ? Convert.ToDecimal(Values["rate"]) : 0.11m;
ctx.Result = baseAmount * rate;
""";
        var scriptDef = new ServerScriptDef
        {
            Id = scriptId,
            TenantId = "acme",
            Name = "Calculate Tax API",
            ScriptType = ServerScriptType.Api,
            ApiMethod = "calc_tax",
            IsActive = true,
            Version = 1,
            CurrentCode = scriptCode
        };

        await store.SaveAsync(scriptDef, scriptCode, user.Id);

        using var bodyDoc = JsonDocument.Parse("{\"amount\": 1000000, \"rate\": 0.12}");
        var result = await executionService.ExecuteApiAsync(user, "calc_tax", bodyDoc.RootElement);

        Assert.True(result is not null, "API script returned non-null result");
        Assert.Equal(120000m, Convert.ToDecimal(result), "API script calculated correct tax value");

        // Clean up
        await store.DeleteAsync("acme", scriptId);
    }

    [Test]
    public async Task ServerScript_scheduled_script_runs_as_automation_identity()
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var executionService = sp.GetRequiredService<ServerScriptExecutionService>();

        var scriptId = "scr_scheduled_sync";
        var scriptCode = """
Log.Info($"Scheduled sync running under user {User.Id}");
""";
        var scriptDef = new ServerScriptDef
        {
            Id = scriptId,
            TenantId = "acme",
            Name = "Nightly Sync Script",
            ScriptType = ServerScriptType.Scheduled,
            CronExpr = "@midnight",
            RunAsUserId = "andi",
            IsActive = true,
            Version = 1,
            CurrentCode = scriptCode
        };

        await store.SaveAsync(scriptDef, scriptCode, "andi");

        await executionService.ExecuteScheduledAsync("acme", scriptId);

        var runs = await store.ListRunsAsync("acme", scriptId);
        Assert.True(runs.Count > 0, "Execution run was recorded");
        Assert.Equal("Success", runs[0].Status, "Scheduled run completed successfully");

        // Clean up
        await store.DeleteAsync("acme", scriptId);
    }
}
