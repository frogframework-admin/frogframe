using System.Text.Json;
using Framework.Web.AI;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Rule catalog generated from hook code (docs/rules, tools/generate_ai_rules.py), the explain_error tool,
/// and Indonesian glossary expansion for the AI Assistant.
/// </summary>
sealed partial class Tests
{
    [Test]
    public Task AiErrorCatalog_matches_user_visible_messages_to_code_rules()
    {
        var catalog = sp.GetRequiredService<AiErrorCatalog>();
        Assert.True(catalog.Count > 100, $"rule catalog loaded from docs/rules/error-catalog.json ({catalog.Count} entries)");

        var stock = catalog.Match("Only 1 of ITM-LAP-001 available in Gudang Utama Cikarang");
        Assert.True(stock.Count > 0 && stock[0].MatchType == "pattern", "interpolated stock message matches its pattern");
        Assert.True(stock[0].Entry.DocTypes.Contains("SalesInvoice") && stock[0].Entry.DocTypes.Contains("StockEntry"),
            "shared stock rule is mapped to the DocTypes that use it");

        var period = catalog.Match("Period 2026-01 is closed", "JournalEntry");
        Assert.True(period.Count > 0 && period[0].Entry.Message.Contains("is closed"), "period rule found");
        Assert.True(!string.IsNullOrWhiteSpace(period[0].Entry.Condition), "the triggering condition is included");

        Assert.True(catalog.Match("hello world").Count == 0, "unrelated text does not match a rule");
        return Task.CompletedTask;
    }

    [Test]
    public Task AiAssistantTools_explain_error_returns_rule_condition_and_guide()
    {
        var tools = AiAssistantTools.ForUser(sp, U("dewi"));
        using var found = JsonDocument.Parse(tools.ExplainError("Only 3 days of Annual Leave remain", "LeaveApplication"));
        Assert.True(found.RootElement.GetProperty("found").GetBoolean(), "leave balance message is explained");
        var first = found.RootElement.GetProperty("matches")[0];
        Assert.Equal("to_date", first.GetProperty("field").GetString(), "field of the leave balance rule");
        Assert.True((first.GetProperty("triggeredWhen").GetString() ?? "").Contains("balance"), "condition mentions the balance");
        Assert.Equal("rules/LeaveApplication.md", first.GetProperty("relatedGuide").GetString(), "points to the DocType rule guide");

        using var missing = JsonDocument.Parse(tools.ExplainError("Something completely unrelated happened"));
        Assert.True(!missing.RootElement.GetProperty("found").GetBoolean(), "no match reports found = false");
        return Task.CompletedTask;
    }

    [Test]
    public Task AiKnowledgeIndex_expands_indonesian_terms_and_indexes_rules()
    {
        var index = sp.GetRequiredService<AiKnowledgeIndex>();
        Assert.True(index.GlossaryTermCount > 50, "glossary loaded from guides/glossary-id.md");
        Assert.True(index.Chunks.Any(c => c.Kind == "Rule" && c.Source == "rules/LeaveApplication.md"), "generated rule guides are indexed");

        var expanded = index.ExpandQuery("kenapa cuti saya ditolak");
        Assert.True(expanded.Contains("LeaveApplication"), $"'cuti' expands to LeaveApplication: {expanded}");
        Assert.True(index.MatchGlossaryTerms("buat faktur penjualan").Contains("faktur penjualan"), "multi-word terms match");
        Assert.Equal("zzz qqq", index.ExpandQuery("zzz qqq"), "unknown words are left unchanged");

        var hits = index.Search("kenapa pengajuan cuti ditolak saldo", maxResults: 5);
        Assert.True(hits.Any(h => h.Source == "rules/LeaveApplication.md"), "Indonesian question finds the LeaveApplication rules");
        return Task.CompletedTask;
    }
}
