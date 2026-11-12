using System.Text.Json;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Customization layer: admin changes to application DocTypes survive deploys of newer DocType files.</summary>
sealed partial class Tests
{
    /// <summary>In-memory stand-in for the PostgreSQL metadata store, shared between "restarts".</summary>
    private sealed class FakeMetadataStore : IMetadataVersionStore
    {
        public List<MetadataVersion> Versions { get; } = [];
        public List<WorkflowVersion> WorkflowVersions { get; } = [];
        public List<DocTypeCustomization> Customizations { get; } = [];

        public IReadOnlyList<MetadataVersion> LoadAll() => Versions.ToList();
        public void Save(MetadataVersion version)
        {
            for (var i = 0; i < Versions.Count; i++)
                if (Versions[i].DocType == version.DocType && Versions[i].Status == MetadataStatus.Published)
                    Versions[i] = Versions[i] with { Status = MetadataStatus.Deprecated };
            Versions.Add(version);
        }
        public IReadOnlyList<WorkflowVersion> LoadAllWorkflows() => WorkflowVersions.ToList();
        public void SaveWorkflow(WorkflowVersion version) => WorkflowVersions.Add(version);
        public IReadOnlyList<DocTypeCustomization> LoadAllCustomizations() =>
            Customizations.Select(c => JsonSerializer.Deserialize<DocTypeCustomization>(MetadataJson.Serialize(c), MetadataJson.Options)!).ToList();
        public void SaveCustomization(DocTypeCustomization c) => Customizations.Add(c);
    }

    private static DocTypeDef CzFile(int version, params FieldDef[] extra) => new()
    {
        Name = "CzItem", Title = "Item", Version = version,
        Naming = new NamingDef { Pattern = "CZ-{#####}" },
        Fields =
        [
            new FieldDef { Name = "title", Type = "Data", Label = "Title", RequiredWhen = "true" },
            new FieldDef { Name = "qty", Type = "Int", Label = "Qty" },
            .. extra,
        ],
        Permissions = [new PermissionDef { Role = UserContext.SystemManager, Actions = ["read", "create", "write", "delete"], Scope = "all" }],
        ListView = new ListViewDef { TitleField = "title", SearchFields = ["name", "title"] },
    };

    private MetadataRegistry CzRegistry(FakeMetadataStore store, DocTypeDef file, params WorkflowDef[] workflows) =>
        new(sp.GetRequiredService<MetadataCompiler>(), [file], workflows, TimeProvider.System, store);

    /// <summary>What an administrator does in the DocType Editor: rename a label, add a field, make qty required.</summary>
    private static DocTypeDef CzEdited(MetadataRegistry reg)
    {
        var edited = Customizations.Clone(reg.Current.Get("CzItem").Def);
        edited.Version += 1; // the editor always sends active + 1; ignored for application DocTypes
        edited.Fields.Single(f => f.Name == "title").Label = "Item name";
        edited.Fields.Single(f => f.Name == "qty").RequiredWhen = "true";
        edited.Fields.Insert(1, new FieldDef { Name = "custom_color", Type = "Data", Label = "Color" });
        edited.Title = "Stock item";
        return edited;
    }

    [Test]
    public Task Customization_extract_and_apply_round_trip()
    {
        var baseDef = CzFile(1);
        var edited = Customizations.Clone(baseDef);
        edited.Fields[0].Label = "Item name";
        edited.Fields.Insert(1, new FieldDef { Name = "custom_color", Type = "Data", Label = "Color" });
        edited.Title = "Stock item";

        var (c, errors) = Customizations.Extract(baseDef, edited);
        Assert.Equal(0, errors.Count, "no errors: " + string.Join(" | ", errors));
        Assert.Equal(1, c.CustomFields.Count, "one custom field");
        Assert.Equal("title", c.CustomFields[0].InsertAfter, "anchored after title");
        Assert.True(c.PropertySetters.Any(p => p.Field == "title" && p.Property == "label"), "label setter");
        Assert.True(c.PropertySetters.Any(p => p.Field is null && p.Property == "title"), "DocType title setter");
        Assert.True(c.FieldOrder is null, "no explicit order needed");

        var applied = Customizations.Apply(baseDef, c).Def;
        Assert.Equal(MetadataJson.Serialize(edited), MetadataJson.Serialize(applied), "apply(base, extract(base, edited)) == edited");
        return Task.CompletedTask;
    }

    [Test]
    public Task Customization_rejects_changes_reserved_for_developers()
    {
        var baseDef = CzFile(1);

        var removed = Customizations.Clone(baseDef);
        removed.Fields.RemoveAll(f => f.Name == "qty");
        Assert.True(Customizations.Extract(baseDef, removed).Errors.Any(e => e.Path == "fields.qty"), "application field cannot be removed");

        var retyped = Customizations.Clone(baseDef);
        retyped.Fields.Single(f => f.Name == "qty").Type = "Float";
        Assert.True(Customizations.Extract(baseDef, retyped).Errors.Any(e => e.Path == "fields.qty.type"), "type is locked");

        var unprefixed = Customizations.Clone(baseDef);
        unprefixed.Fields.Add(new FieldDef { Name = "color", Type = "Data", Label = "Color" });
        Assert.True(Customizations.Extract(baseDef, unprefixed).Errors.Any(e => e.Path == "fields.color"), "new field needs custom_ prefix");

        var naming = Customizations.Clone(baseDef);
        naming.Naming.Pattern = "X-{#####}";
        Assert.True(Customizations.Extract(baseDef, naming).Errors.Any(e => e.Path == "naming"), "naming is developer-owned");
        return Task.CompletedTask;
    }

    [Test]
    public Task Customization_survives_deploy_of_newer_doctype_file()
    {
        var store = new FakeMetadataStore();
        var reg = CzRegistry(store, CzFile(1));
        reg.Publish(CzEdited(reg), "admin", acknowledgeRisk: true);

        Assert.Equal(1, reg.Current.Get("CzItem").Version, "base version unchanged (customization only)");
        Assert.True(store.Versions.All(v => v.Version == 1), "no new base version stored");
        Assert.Equal(1, store.Customizations.Count, "customization stored");
        Assert.Equal("Item name", reg.Current.Get("CzItem").ByName["title"].Label, "label customized");

        // Deploy: the developers ship v2 with a new field. Restart = new registry over the same store.
        var v2 = CzFile(2, new FieldDef { Name = "unit", Type = "Data", Label = "Unit" });
        var after = CzRegistry(store, v2);
        var meta = after.Current.Get("CzItem");
        Assert.Equal(2, meta.Version, "new base active");
        Assert.True(meta.ByName.ContainsKey("unit"), "developer's new field present");
        Assert.True(meta.ByName.ContainsKey("custom_color"), "admin's custom field kept");
        Assert.Equal("Item name", meta.ByName["title"].Label, "admin's label kept");
        Assert.Equal("true", meta.ByName["qty"].Def.RequiredWhen, "admin's rule kept");
        Assert.Equal("Stock item", meta.Def.Title, "admin's DocType title kept");
        Assert.Equal(1, meta.Fields.ToList().FindIndex(f => f.Name == "custom_color"), "custom field still after title");
        return Task.CompletedTask;
    }

    [Test]
    public Task Customization_problems_after_deploy_become_warnings_not_crashes()
    {
        var store = new FakeMetadataStore();
        var reg = CzRegistry(store, CzFile(1));
        var edited = CzEdited(reg);
        // A custom field whose formula uses qty.
        edited.Fields.Add(new FieldDef { Name = "custom_double", Type = "Int", Label = "Double", Compute = "qty * 2" });
        reg.Publish(edited, "admin", acknowledgeRisk: true);

        // Deploy v2 removes qty: the override of qty is skipped and the formula no longer compiles.
        var v2 = CzFile(2);
        v2.Fields.RemoveAll(f => f.Name == "qty");
        var after = CzRegistry(store, v2);

        Assert.True(after.Current.TryGet("CzItem", out var meta), "application still starts");
        Assert.True(after.CustomizationWarnings("CzItem").Count > 0, "problems reported: " + string.Join(" | ", after.CustomizationWarnings("CzItem")));
        Assert.True(!meta.ByName.ContainsKey("qty"), "deploy's removal respected");
        return Task.CompletedTask;
    }

    [Test]
    public Task Customization_reset_restores_application_definition()
    {
        var store = new FakeMetadataStore();
        var reg = CzRegistry(store, CzFile(1));
        reg.Publish(CzEdited(reg), "admin", acknowledgeRisk: true);
        Assert.True(reg.Current.Get("CzItem").ByName.ContainsKey("custom_color"), "customized");

        reg.ResetCustomization("CzItem", "admin");
        Assert.True(!reg.Current.Get("CzItem").ByName.ContainsKey("custom_color"), "custom field gone");
        Assert.Equal("Title", reg.Current.Get("CzItem").ByName["title"].Label, "label back to default");
        Assert.True(reg.Customization("CzItem") is null, "no active customization");
        Assert.Equal(2, reg.CustomizationHistory("CzItem").Count, "reset kept in history");

        var restarted = CzRegistry(store, CzFile(1));
        Assert.True(!restarted.Current.Get("CzItem").ByName.ContainsKey("custom_color"), "reset survives restart");
        return Task.CompletedTask;
    }

    [Test]
    public Task Customization_workflow_link_on_app_doctype_survives_deploy()
    {
        var store = new FakeMetadataStore();
        var reg = CzRegistry(store, CzFile(1));
        var wf = new WorkflowDef
        {
            Name = "CzItemApproval", DocType = "CzItem", Version = 1, InitialState = "Draft",
            States = [new WorkflowStateDef { Name = "Draft", DocStatus = 0 }, new WorkflowStateDef { Name = "Approved", DocStatus = 1 }],
            Transitions = [new WorkflowTransitionDef { From = "Draft", To = "Approved", Action = "Approve", AllowedRoles = [UserContext.SystemManager] }],
        };
        reg.PublishWorkflow(wf, "admin", acknowledgeRisk: true, attach: true);
        Assert.Equal("CzItemApproval", reg.Current.Get("CzItem").Def.Workflow, "linked");
        Assert.Equal(1, reg.Current.Get("CzItem").Version, "base version unchanged");

        var after = CzRegistry(store, CzFile(2));
        Assert.Equal("CzItemApproval", after.Current.Get("CzItem").Def.Workflow, "link kept after deploy");
        return Task.CompletedTask;
    }

    [Test]
    public Task Customization_does_not_apply_to_doctypes_created_in_the_ui()
    {
        var store = new FakeMetadataStore();
        var reg = CzRegistry(store, CzFile(1));
        var uiDoc = CzFile(1);
        uiDoc.Name = "CzUiOnly";
        uiDoc.Naming = new NamingDef { Pattern = "CU-{#####}" };
        reg.Publish(uiDoc, "admin", acknowledgeRisk: false);
        Assert.True(!reg.IsAppOwned("CzUiOnly") && reg.IsAppOwned("CzItem"), "ownership");

        var next = Customizations.Clone(uiDoc);
        next.Version = 2;
        next.Fields.Add(new FieldDef { Name = "color", Type = "Data", Label = "Color" }); // no prefix needed
        reg.Publish(next, "admin", acknowledgeRisk: false);
        Assert.Equal(2, reg.Current.Get("CzUiOnly").Version, "UI DocTypes still get normal versions");
        Assert.True(reg.Customization("CzUiOnly") is null, "no customization for UI DocTypes");
        return Task.CompletedTask;
    }
}
