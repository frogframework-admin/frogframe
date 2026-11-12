using Framework.Metadata;
using Framework.Web.Admin;
using Microsoft.Extensions.DependencyInjection;

/// <summary>DocType Editor: Tab / Section / Column controls in the palette (layout breaks).</summary>
sealed partial class Tests
{
    private (DocTypeDesign Design, MetadataRegistry Registry) PrDesign()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        return (DocTypeDesign.From(reg.Current.Get(PR).Def), reg);
    }

    private static (DesignerSection Section, int Column, int Index) At(DocTypeDesign d, string field)
    {
        var loc = d.Locate(d.AllFields.Single(f => f.Name == field))!.Value;
        return (loc.Section, loc.Column, loc.Index);
    }

    [Test]
    public Task Designer_tab_break_moves_the_field_and_everything_after_it_to_a_new_tab()
    {
        var (d, reg) = PrDesign();
        // Kebutuhan: column 1 = vendor, justification; column 2 = priority. Then section Item (items, total). Then tab Pengadaan.
        var (sec, col, idx) = At(d, "justification");
        var newTab = d.BreakTab(sec, col, idx);

        Assert.Equal("Pengajuan,Tab 1,Pengadaan", string.Join(",", d.Tabs.Select(t => t.Title)), "new tab right after the current one");
        var def = d.ToDef(2);
        string TabOf(string name) => def.Fields.Single(f => f.Name == name).Tab!;
        Assert.Equal("Pengajuan", TabOf("vendor"), "fields before the break stay");
        Assert.Equal("Tab 1", TabOf("justification"), "the field moves");
        Assert.Equal("Tab 1", TabOf("priority"), "later column of the same section moves");
        Assert.Equal("Tab 1", TabOf("items"), "later sections move");
        Assert.Equal("Pengadaan", TabOf("budget_code"), "other tabs untouched");
        Assert.Equal("Kebutuhan", def.Fields.Single(f => f.Name == "justification").Section, "section title carried over");
        Assert.Equal(def.Fields.Count, reg.Current.Get(PR).Def.Fields.Count, "no field lost");

        var (snap, errs) = reg.CompileDraft(def);
        Assert.True(snap is not null && errs.Count == 0, "draft compiles: " + string.Join("; ", errs));
        Assert.Equal("Tab 1", snap!.Get(PR).Layout[1].Title, "second tab in the form");
        Assert.True(newTab.Sections[0].Columns[0][0].Name == "justification", "justification first in the new tab");
        return Task.CompletedTask;
    }

    [Test]
    public Task Designer_section_break_and_column_break()
    {
        var (d, reg) = PrDesign();

        // Section break at 'total' (section Item: items, total).
        var (sec, col, idx) = At(d, "total");
        var newSection = d.BreakSection(sec, col, idx);
        Assert.Equal("Section 1", newSection.Title, "new section gets a free title");
        var def = d.ToDef(2);
        Assert.Equal("Item", def.Fields.Single(f => f.Name == "items").Section, "field before the break stays");
        Assert.Equal("Section 1", def.Fields.Single(f => f.Name == "total").Section, "field moves to the new section");
        Assert.Equal("Pengajuan", def.Fields.Single(f => f.Name == "total").Tab, "same tab");
        var tab = d.TabOf(sec);
        Assert.Equal(tab.Sections.IndexOf(sec) + 1, tab.Sections.IndexOf(newSection), "inserted right after");

        // Column break at 'justification' in Kebutuhan (2 columns → 3, justification starts column 2, priority shifts to 3).
        (sec, col, idx) = At(d, "justification");
        Assert.True(d.BreakColumn(sec, col, idx), "column added");
        def = d.ToDef(2);
        int ColumnOf(string name) => def.Fields.Single(f => f.Name == name).Column;
        Assert.Equal(1, ColumnOf("vendor"), "vendor stays in column 1");
        Assert.Equal(2, ColumnOf("justification"), "justification starts column 2");
        Assert.Equal(3, ColumnOf("priority"), "later column shifts right");
        Assert.True(!d.BreakColumn(sec, 0, 0), "at most 3 columns");

        var (snap, errs) = reg.CompileDraft(def);
        Assert.True(snap is not null && errs.Count == 0, "draft compiles: " + string.Join("; ", errs));
        Assert.Equal(def.Fields.Count, reg.Current.Get(PR).Def.Fields.Count, "no field lost");
        return Task.CompletedTask;
    }

    [Test]
    public Task Designer_tab_break_at_the_end_of_a_column_creates_a_tab_for_what_follows()
    {
        var (d, _) = PrDesign();
        var item = d.TabOf(At(d, "items").Section).Sections.Single(s => s.Title == "Item");
        // Dropped below the last field of section Item (index null = end): nothing of Item moves, no later sections.
        var newTab = d.BreakTab(item, 0, null);
        Assert.Equal(1, newTab.Sections.Count, "new tab with one empty section to fill");
        Assert.True(newTab.IsEmpty, "empty: nothing followed the drop point");
        Assert.Equal("Item", d.ToDef(2).Fields.Single(f => f.Name == "total").Section, "fields stay");
        return Task.CompletedTask;
    }
}
