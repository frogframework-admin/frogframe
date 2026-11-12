using Framework.Metadata;
using Framework.Web.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

/// <summary>AI designer on application DocTypes: drafts follow the customization rules (custom_ prefix, no removals).</summary>
sealed partial class Tests
{
    private DocTypeDef AiPurchaseRequestDraft(Action<DocTypeDef> change)
    {
        var def = Customizations.Clone(sp.GetRequiredService<MetadataRegistry>().Current.Get(PR).Def);
        def.Version += 1;
        change(def);
        return def;
    }

    [Test]
    public Task AiDesigner_validator_applies_customization_rules_to_application_doctypes()
    {
        var registry = sp.GetRequiredService<MetadataRegistry>();
        Assert.True(registry.IsAppOwned(PR), "PurchaseRequest ships with the sample application");
        var validator = sp.GetRequiredService<AiDraftValidator>();
        var admin = U("admin");

        var unprefixed = AiPurchaseRequestDraft(d => d.Fields.Add(new FieldDef { Name = "npwp", Type = "Data", Label = "NPWP" }));
        var errors = validator.ValidateDocTypes(admin, unprefixed, [], null, PR);
        Assert.True(errors.Any(e => e.Contains("custom_")), "new field without custom_ is an error: " + string.Join(" | ", errors));

        var removed = AiPurchaseRequestDraft(d => d.Fields.RemoveAll(f => f.Name == "justification"));
        errors = validator.ValidateDocTypes(admin, removed, [], null, PR);
        Assert.True(errors.Any(e => e.Contains("justification") && e.Contains("cannot be removed")), "removing an application field is an error");

        var retyped = AiPurchaseRequestDraft(d => d.Fields.Single(f => f.Name == "justification").Type = "Data");
        errors = validator.ValidateDocTypes(admin, retyped, [], null, PR);
        Assert.True(errors.Any(e => e.Contains("justification.type")), "changing the type of an application field is an error");

        var ok = AiPurchaseRequestDraft(d =>
        {
            d.Fields.Add(new FieldDef { Name = "custom_npwp", Type = "Data", Label = "NPWP" });
            d.Fields.Single(f => f.Name == "justification").Label = "Alasan";
        });
        errors = validator.ValidateDocTypes(admin, ok, [], null, PR);
        Assert.Equal(0, errors.Count, "custom_ field and a new label are fine: " + string.Join(" | ", errors));
        return Task.CompletedTask;
    }

    [Test]
    public async Task AiDesigner_repairs_new_fields_of_application_doctypes_to_custom_prefix()
    {
        var bad = new DocTypeDesignAnswer
        {
            Explanation = "Added NPWP.",
            DocType = AiPurchaseRequestDraft(d => d.Fields.Add(new FieldDef { Name = "npwp", Type = "Data", Label = "NPWP" })),
        };
        var good = new DocTypeDesignAnswer
        {
            Explanation = "Added NPWP as a custom field.",
            DocType = AiPurchaseRequestDraft(d => d.Fields.Add(new FieldDef { Name = "custom_npwp", Type = "Data", Label = "NPWP" })),
        };
        var chat = new ScriptedChatClient(AnswerJson(bad), AnswerJson(good));

        var result = await AiWith(chat).DesignDocTypeAsync(U("admin"), "Add an NPWP field", existingDocType: PR);

        var firstPrompt = chat.Calls[0].Last(m => m.Role == ChatRole.User).Text;
        Assert.True(firstPrompt.Contains("ships with the application") && firstPrompt.Contains("custom_"), "the model is told about the application rules");
        Assert.True(result.Ok, "repaired: " + string.Join(" | ", result.Errors));
        Assert.Equal(2, result.Usage.Attempts, "one repair round");
        Assert.True(result.DocType!.Fields.Any(f => f.Name == "custom_npwp"), "custom field in the draft");
        var repairPrompt = chat.Calls[1].Last(m => m.Role == ChatRole.User).Text;
        Assert.True(repairPrompt.Contains("Server validation found these errors") && repairPrompt.Contains("custom_"), "customization error sent back to the model");
        Assert.True(AiDesignerPrompts.DocTypeDesigner.Contains("custom_<snake_case>"), "rules are part of the instructions");
    }
}
