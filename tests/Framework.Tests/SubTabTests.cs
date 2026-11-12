using Framework.Metadata;
using Framework.Web.Admin;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Tabs inside a section (field property subTab): compiled layout and DocType Editor.</summary>
sealed partial class Tests
{
    [Test]
    public Task SubTab_compiles_into_inner_tabs_of_a_section()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var def = Framework.Web.Admin.DocTypeDesign.Clone(reg.Current.Get("Vendor").Def);
        def.Version += 1;
        def.Fields.Add(new FieldDef { Name = "custom_address_note", Type = "Data", Label = "Note", Section = "Address" });
        def.Fields.Add(new FieldDef { Name = "custom_home_street", Type = "Data", Label = "Street", Section = "Address", SubTab = "Home" });
        def.Fields.Add(new FieldDef { Name = "custom_office_street", Type = "Data", Label = "Street", Section = "Address", SubTab = "Office" });
        def.Fields.Add(new FieldDef { Name = "custom_office_city", Type = "Data", Label = "City", Section = "Address", SubTab = "Office", Column = 2 });
        def.Fields.Add(new FieldDef { Name = "custom_bank_name", Type = "Data", Label = "Bank", Section = "Bank", SubTab = "Primary" });

        var (snap, errs) = reg.CompileDraft(def);
        Assert.True(snap is not null && errs.Count == 0, "compiles: " + string.Join("; ", errs));
        var sections = snap!.Get("Vendor").Layout.SelectMany(t => t.Sections).ToList();

        var address = sections.Where(s => s.Title == "Address" || s.ParentTitle == "Address").ToList();
        Assert.Equal(3, address.Count, "Address: own fields + 2 inner tabs");
        Assert.True(address[0].SubTab is null && address[0].Columns[0].Fields[0].Name == "custom_address_note", "own fields first, as a normal section");
        Assert.Equal("Home,Office", string.Join(",", address.Where(s => s.SubTab is not null).Select(s => s.SubTab)), "inner tabs in order");
        var office = address.Single(s => s.SubTab == "Office");
        Assert.Equal("Home,Office", string.Join(",", office.SubTabs), "strip lists all inner tabs");
        Assert.Equal(2, office.Columns.Count, "inner tab keeps its own columns");
        Assert.True(!office.ShowParentTitle, "section title shown on the own-fields part");
        Assert.True(office.SubTabGroup is not null && office.SubTabGroup == address[1].SubTabGroup, "same group");

        var bank = sections.Single(s => s.SubTab == "Primary");
        Assert.True(bank.ShowParentTitle && bank.ParentTitle == "Bank", "only inner tabs: the strip carries the section title");
        Assert.True(bank.SubTabGroup != office.SubTabGroup, "groups per section");

        var all = sections.SelectMany(s => s.Columns).SelectMany(c => c.Fields).Select(f => f.Name).ToHashSet();
        Assert.True(def.Fields.All(f => all.Contains(f.Name)), "every field is in exactly one layout part");
        return Task.CompletedTask;
    }

    [Test]
    public Task SubTab_designer_add_drop_save_and_reload()
    {
        var (d, reg) = PrDesign();
        var pengajuan = d.Tabs.Single(t => t.Title == "Pengajuan");

        // Reported scenario: Add section, then put a tab inside it, then fill it.
        var address = d.AddSection(pengajuan);
        address.Title = "Address";
        var home = d.AddSubTab(address);
        Assert.Equal("Tab 1", home.SubTab, "inner tab gets a free name");
        Assert.True(ReferenceEquals(home.Parent, address), "belongs to the section");
        Assert.Equal(pengajuan.Sections.IndexOf(address) + 1, pengajuan.Sections.IndexOf(home), "directly under its section");
        Assert.True(!d.Warnings.Any(w => w.Contains("\"Address\"") && w.Contains("kosong") && !w.Contains("Tab \"")), "a section that holds inner tabs is not reported as empty");
        Assert.True(d.Warnings.Any(w => w.Contains("Tab \"Tab 1\" di dalam section \"Address\"")), "empty inner tab is reported");
        Assert.True(!d.RemoveSection(pengajuan, address), "a section with inner tabs cannot be deleted");

        var street = d.AddField("Data", home, 0, null, reg.Current);
        street.Name = "custom_home_street"; street.Label = "Street";

        // Drop "Tab in section" on 'justification' (Kebutuhan): it and the fields after it move into a new inner tab.
        var (sec, col, idx) = At(d, "justification");
        var inner = d.BreakSubTab(sec, col, idx);
        Assert.Equal("Kebutuhan", inner.EffectiveTitle, "inner tab of Kebutuhan");

        var def = d.ToDef(2);
        var saved = def.Fields.Single(f => f.Name == "custom_home_street");
        Assert.True(saved.Section == "Address" && saved.SubTab == "Tab 1", "saved as section + subTab");
        Assert.True(def.Fields.Single(f => f.Name == "justification").SubTab == inner.SubTab, "moved field saved in the inner tab");
        Assert.True(def.Fields.Single(f => f.Name == "vendor").SubTab is null, "field before the drop point stays in the section");

        var (snap, errs) = reg.CompileDraft(def);
        Assert.True(snap is not null && errs.Count == 0, "compiles: " + string.Join("; ", errs));

        // Reload the saved definition: same structure.
        var again = DocTypeDesign.From(def);
        var reloaded = again.Tabs.SelectMany(t => t.Sections).Single(s => s.SubTab == "Tab 1" && s.EffectiveTitle == "Address");
        Assert.True(reloaded.Parent is not null && reloaded.Parent.Title == "Address", "inner tab linked to its section after reload");
        Assert.Equal(MetadataJson.Serialize(def), MetadataJson.Serialize(again.ToDef(2)), "round trip");

        // Renaming the section keeps its inner tabs with it.
        address.Title = "Alamat";
        Assert.Equal("Alamat", d.ToDef(2).Fields.Single(f => f.Name == "custom_home_street").Section, "inner tab follows the section title");

        // Moving the section moves its inner tabs along.
        var before = pengajuan.Sections.IndexOf(address);
        d.MoveSection(pengajuan, address, -1);
        Assert.Equal(before - 1, pengajuan.Sections.IndexOf(address), "section moved up");
        Assert.Equal(pengajuan.Sections.IndexOf(address) + 1, pengajuan.Sections.IndexOf(home), "inner tab still directly under it");
        return Task.CompletedTask;
    }
}
