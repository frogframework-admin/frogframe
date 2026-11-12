using System.Text.Json;
using Framework.Web;
using Framework.Web.Components;
using Microsoft.AspNetCore.Http;

/// <summary>Monaco Server Script editor: completion catalog and the CSP needed by Monaco's workers.</summary>
sealed partial class Tests
{
    private static JsonElement ScriptEditorCatalogJson()
    {
        // Same serializer settings Blazor uses for JS interop (camelCase properties, dictionary keys unchanged).
        var json = JsonSerializer.Serialize(ScriptCompletionCatalog.ForEditor, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static List<string> Labels(JsonElement items) =>
        items.EnumerateArray().Select(i => i.GetProperty("label").GetString()!).ToList();

    [Test]
    public Task ScriptEditor_catalog_lists_script_api_members()
    {
        var catalog = ScriptEditorCatalogJson();
        var members = catalog.GetProperty("members");

        var doc = Labels(members.GetProperty("Doc"));
        foreach (var name in new[] { "GetDecimal", "GetString", "GetInt", "GetBool", "GetTable", "ContainsKey", "Data", "DocType", "Name" })
            Assert.True(doc.Contains(name), $"Doc offers {name}");

        Assert.True(Labels(members.GetProperty("Docs")).Contains("GetAsync"), "Docs offers GetAsync");
        Assert.True(Labels(members.GetProperty("Log")).Contains("Info"), "Log offers Info");
        Assert.True(Labels(members.GetProperty("User")).Contains("IsInRole"), "User offers IsInRole");
        Assert.True(Labels(members.GetProperty("ctx")).Contains("Result"), "ctx offers Result");
        Assert.True(Labels(members.GetProperty("Math")).Contains("Round"), "Math offers Round");

        var globals = Labels(catalog.GetProperty("globals"));
        foreach (var name in new[] { "Doc", "OldDoc", "User", "Docs", "Log", "Email", "Values", "CancellationToken", "ctx" })
            Assert.True(globals.Contains(name), $"global {name}");

        return Task.CompletedTask;
    }

    [Test]
    public Task ScriptEditor_catalog_never_offers_wrapper_internals()
    {
        var catalog = ScriptEditorCatalogJson();
        var all = catalog.GetProperty("members").EnumerateObject()
            .SelectMany(p => Labels(p.Value))
            .Concat(Labels(catalog.GetProperty("globals")))
            .ToList();

        Assert.True(!all.Any(l => l.StartsWith("__", StringComparison.Ordinal)), "no __budget or other wrapper internals");
        Assert.True(!all.Contains("GetType"), "no GetType (rejected by the analyzer)");
        return Task.CompletedTask;
    }

    [Test]
    public Task ScriptEditor_snippets_compile_in_the_sandbox()
    {
        // Each snippet with its placeholders filled in must pass the analyzer, so the editor never suggests
        // code the sandbox will reject.
        var code = """
            var amount = Doc.GetDecimal("amount");
            if (amount < 0)
            {
                throw new ScriptValidationException("Amount must not be negative.", "amount");
            }
            foreach (var row in Doc.GetTable("items"))
            {
                Log.Info(row.GetString("item_code") ?? "");
            }
            var other = await Docs.GetAsync("PurchaseRequest", "PR-0001", CancellationToken);
            await Email.SendAsync("a@example.com", "Subject", "Body", ct: CancellationToken);
            ctx.Result = new Dictionary<string, object?>
            {
                ["amount"] = Math.Round(amount, 2)
            };
            """;

        var errors = Framework.Scripting.ScriptCompiler.Validate(code);
        Assert.Equal(0, errors.Count, "snippet code validates: " + string.Join(" | ", errors));
        return Task.CompletedTask;
    }

    [Test]
    public Task ScriptEditor_catalog_supports_client_scripts()
    {
        var catalog = ScriptEditorCatalogJson();
        var members = catalog.GetProperty("members");
        var form = Labels(members.GetProperty("Form"));
        foreach (var name in new[] { "Event", "ChangedField", "IsNew", "GetValue", "SetValue", "SetHidden", "SetRequired", "SetReadOnly", "SetOptions", "SetDescription", "ShowMessage", "AddError" })
            Assert.True(form.Contains(name), $"Form offers {name}");
        Assert.True(Labels(members.GetProperty("FormScriptEvent")).SequenceEqual(new[] { "Load", "Change", "Validate" }), "FormScriptEvent values");
        var globals = Labels(catalog.GetProperty("globals"));
        Assert.True(globals.Contains("Form") && globals.Contains("FormScriptEvent"), "Form globals");
        Assert.True(Labels(catalog.GetProperty("snippets")).Count(l => l.StartsWith("form", StringComparison.Ordinal)) == 4, "Client Script snippets");

        // The Client Script snippets, filled in, pass the sandbox analyzer.
        var code = """
            var show = Form.GetValue("priority") as string == "High";
            Form.SetHidden("procurement_notes", !show);
            Form.SetRequired("procurement_notes", show);
            if (string.IsNullOrEmpty(Form.GetValue("justification") as string))
            {
                Form.SetValue("justification", "Urgent request");
            }
            if (Form.Event == FormScriptEvent.Validate && Form.ChangedField == null)
            {
                Form.AddError("justification", "Please explain.");
            }
            Form.ShowMessage("Saved", "info");
            """;
        var errors = Framework.Scripting.ScriptCompiler.Validate(code);
        Assert.Equal(0, errors.Count, "client script snippets validate: " + string.Join(" | ", errors));
        return Task.CompletedTask;
    }

    [Test]
    public async Task ScriptEditor_csp_allows_monaco_blob_workers()
    {
        var context = new DefaultHttpContext();
        var middleware = new FrogFrameSecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var csp = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.True(csp.Contains("worker-src 'self' blob:"), "CSP worker-src allows Monaco workers");
    }
}
