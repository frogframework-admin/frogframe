using Framework.Documents;
using Framework.Scripting;
using Framework.Scripting.Abstractions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Client Scripts: C# scripts that run during form events (Load, Change, Validate) to adjust the form.</summary>
sealed partial class Tests
{
    private async Task<ServerScriptDef> SaveClientScript(string id, string name, FormScriptEvent evt, string? field, string code, bool active = true)
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        var def = new ServerScriptDef
        {
            Id = id,
            TenantId = "acme",
            Name = name,
            ScriptType = ServerScriptType.Form,
            DocType = PR,
            FormEvent = evt,
            FormField = field,
            IsActive = active,
            Version = 1,
            CurrentCode = code,
        };
        await store.SaveAsync(def, code, "admin");
        sp.GetRequiredService<ServerScriptExecutionService>().Invalidate("acme", id);
        return def;
    }

    private async Task DeleteClientScripts(params string[] ids)
    {
        var store = sp.GetRequiredService<IServerScriptStore>();
        foreach (var id in ids)
        {
            await store.DeleteAsync("acme", id);
            sp.GetRequiredService<ServerScriptExecutionService>().Invalidate("acme", id);
        }
    }

    private static Dictionary<string, object?> PrForm(string priority = "Normal") => new()
    {
        ["priority"] = priority,
        ["justification"] = "",
        ["vendor"] = "VND-0001",
    };

    [Test]
    public async Task ClientScript_change_event_adjusts_form_and_fills_values()
    {
        var service = sp.GetRequiredService<ServerScriptExecutionService>();
        await SaveClientScript("cs_priority", "Priority rules", FormScriptEvent.Change, "priority", """
var urgent = Form.GetValue("priority") as string == "Tinggi";
Form.SetHidden("procurement_notes", !urgent);
Form.SetRequired("justification", urgent);
Form.SetReadOnly("vendor", urgent);
Form.SetOptions("priority", new[] { "Normal", "Tinggi" });
Form.SetDescription("justification", urgent ? "Explain why this is urgent." : null);
if (urgent && string.IsNullOrEmpty(Form.GetValue("justification") as string))
    Form.SetValue("justification", "Urgent request");
Form.ShowMessage("Priority is " + Form.GetValue("priority"), "warning");
""");
        try
        {
            var data = PrForm("Tinggi");
            var outcome = await service.RunFormEventAsync(U("andi"), PR, FormScriptEvent.Change, "priority", data, isNew: true);

            Assert.Equal(0, outcome.Failures.Count, "no failures: " + string.Join(" | ", outcome.Failures.Select(f => f.Message)));
            Assert.Equal("Urgent request", outcome.Values["justification"], "value filled in");
            Assert.Equal("", data["justification"], "the caller's data is not changed by the script");
            Assert.True(!outcome.Hidden["procurement_notes"], "shown when urgent");
            Assert.True(outcome.Required["justification"], "required when urgent");
            Assert.True(outcome.ReadOnly["vendor"], "vendor locked when urgent");
            Assert.Equal(2, outcome.Options["priority"].Count, "options narrowed");
            Assert.Equal("Explain why this is urgent.", outcome.Descriptions["justification"], "description");
            Assert.Equal("warning", outcome.Messages.Single().Level, "message level");

            // Script bound to 'priority' does not run when another field changes.
            var other = await service.RunFormEventAsync(U("andi"), PR, FormScriptEvent.Change, "vendor", PrForm("Tinggi"), isNew: true);
            Assert.True(ReferenceEquals(other, FormScriptOutcome.Empty), "not run for other fields");

            // And not for other events.
            var load = await service.RunFormEventAsync(U("andi"), PR, FormScriptEvent.Load, null, PrForm("Tinggi"), isNew: true);
            Assert.True(ReferenceEquals(load, FormScriptOutcome.Empty), "not run on load");
        }
        finally { await DeleteClientScripts("cs_priority"); }
    }

    [Test]
    public async Task ClientScript_validate_event_returns_field_errors()
    {
        var service = sp.GetRequiredService<ServerScriptExecutionService>();
        await SaveClientScript("cs_validate_a", "A: check justification", FormScriptEvent.Validate, null, """
if ((Form.GetValue("justification") as string ?? "").Length < 10)
    Form.AddError("justification", "Please write at least 10 characters.");
""");
        await SaveClientScript("cs_validate_b", "B: blocked vendor", FormScriptEvent.Validate, null, """
if (Doc.GetString("vendor") == "VND-BLOCKED")
    throw new ScriptValidationException("This vendor is blocked.", "vendor");
""");
        try
        {
            var data = PrForm();
            data["vendor"] = "VND-BLOCKED";
            var outcome = await service.RunFormEventAsync(U("andi"), PR, FormScriptEvent.Validate, null, data, isNew: false, docName: "PR-X");
            Assert.True(outcome.Errors.Any(e => e.Path == "justification"), "AddError reported");
            Assert.True(outcome.Errors.Any(e => e.Path == "vendor" && e.Message.Contains("blocked")), "ScriptValidationException reported");
            Assert.Equal(0, outcome.Failures.Count, "validation errors are not failures");

            data["vendor"] = "VND-0001";
            data["justification"] = "Monthly office supplies";
            var ok = await service.RunFormEventAsync(U("andi"), PR, FormScriptEvent.Validate, null, data, isNew: false, docName: "PR-X");
            Assert.Equal(0, ok.Errors.Count, "valid form passes");
        }
        finally { await DeleteClientScripts("cs_validate_a", "cs_validate_b"); }
    }

    [Test]
    public async Task ClientScript_is_limited_to_what_the_user_could_do_in_the_form()
    {
        var service = sp.GetRequiredService<ServerScriptExecutionService>();
        await SaveClientScript("cs_limits_a", "A: tries to write", FormScriptEvent.Load, null, """
await Docs.DeleteAsync("PurchaseRequest", "PR-00001");
""");
        await SaveClientScript("cs_limits_b", "B: tries to email", FormScriptEvent.Load, null, """
await Email.SendAsync("a@example.com", "hi", "body");
""");
        await SaveClientScript("cs_limits_c", "C: sets values", FormScriptEvent.Load, null, """
Form.SetValue("total", 999);            // computed: dropped
Form.SetValue("request_date", "not a date"); // invalid: dropped
Form.SetValue("no_such_field", "x");    // unknown: dropped
Form.SetValue("required_by", "2026-12-31");
Form.SetValue("priority", "Tinggi");
""");
        await SaveClientScript("cs_limits_d", "D: inactive", FormScriptEvent.Load, null, """
Form.SetValue("justification", "should not run");
""", active: false);
        try
        {
            var outcome = await service.RunFormEventAsync(U("andi"), PR, FormScriptEvent.Load, null, PrForm(), isNew: true);

            Assert.True(outcome.Failures.Any(f => f.Script == "A: tries to write"), "writing documents is refused");
            Assert.True(outcome.Failures.Any(f => f.Script == "B: tries to email"), "sending email is refused");
            Assert.True(!outcome.Values.ContainsKey("total"), "computed field not set");
            Assert.True(!outcome.Values.ContainsKey("request_date"), "invalid value not set");
            Assert.True(!outcome.Values.ContainsKey("no_such_field"), "unknown field not set");
            Assert.True(outcome.Failures.Any(f => f.Message.Contains("total")), "dropped value reported");
            Assert.Equal("2026-12-31", outcome.Values["required_by"]?.ToString(), "valid value set (scripts after a failing one still run)");
            Assert.Equal("Tinggi", outcome.Values["priority"], "select value set");
            Assert.True(!outcome.Values.ContainsKey("justification"), "inactive script does not run");
        }
        finally { await DeleteClientScripts("cs_limits_a", "cs_limits_b", "cs_limits_c", "cs_limits_d"); }
    }

    [Test]
    public async Task ClientScript_runaway_script_is_stopped()
    {
        var service = sp.GetRequiredService<ServerScriptExecutionService>();
        await SaveClientScript("cs_loop", "Endless loop", FormScriptEvent.Load, null, "while (true) { }");
        try
        {
            var outcome = await service.RunFormEventAsync(U("andi"), PR, FormScriptEvent.Load, null, PrForm(), isNew: true);
            Assert.True(outcome.Failures.Any(f => f.Script == "Endless loop"), "budget stops the script and it is reported");

            Assert.True(ScriptCompiler.Validate("Form.SetHidden(\"x\");").Count == 0, "Form is a known global for the compiler");
        }
        finally { await DeleteClientScripts("cs_loop"); }
    }
}
