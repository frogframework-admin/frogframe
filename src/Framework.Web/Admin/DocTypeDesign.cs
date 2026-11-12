using System.Globalization;
using System.Text.Json;
using Framework.Metadata;

namespace Framework.Web.Admin;

public sealed class DesignerSection
{
    public string? Title { get; set; }
    /// <summary>
    /// Set when this part is a tab INSIDE a section: the name of that inner tab. <see cref="Parent"/> is the section
    /// that holds it (its title is saved as the fields' section); the inner tab keeps its own columns.
    /// </summary>
    public string? SubTab { get; set; }
    public DesignerSection? Parent { get; set; }
    /// <summary>Section title as saved: the holding section's title for an inner tab.</summary>
    public string? EffectiveTitle => Parent?.Title ?? Title;
    /// <summary>Kolom 1..3, masing-masing berisi field berurutan.</summary>
    public List<List<FieldDef>> Columns { get; } = [[]];
    public bool IsEmpty => Columns.All(c => c.Count == 0);
}

public sealed class DesignerTab
{
    public string Title { get; set; } = DocTypeDesign.DefaultTab;
    /// <summary>True bila judul tab ditulis eksplisit di metadata (bukan default "Detail").</summary>
    public bool Explicit { get; set; }
    public List<DesignerSection> Sections { get; } = [];
    public bool IsEmpty => Sections.All(s => s.IsEmpty);
}

/// <summary>
/// Model kerja editor DocType visual. Metadata tetap satu-satunya sumber kebenaran: layout diturunkan dari
/// properti tab/section/column tiap field, dan <see cref="ToDef"/> menuliskannya kembali dalam urutan tampilan.
/// Properti lain (permission, workflow, rules, printFormat, dst.) dibawa apa adanya.
/// </summary>
public sealed class DocTypeDesign
{
    public const string DefaultTab = "Detail";
    public static readonly string[] FieldTypes =
    [
        "Data", "Text", "Email", "Int", "Float", "Currency", "Check", "Date", "Select", "Link", "Table", "Image",
        // Gap #3 (Frappe parity)
        "Datetime", "Time", "Duration", "Percent", "Phone", "TextEditor", "Markdown", "Attach", "Password",
        "DynamicLink", "TableMultiSelect", "Rating", "Color", "JSON", "Signature", "Barcode", "Geolocation",
        "ReadOnly", "HTML", "Button", "Heading",
    ];

    private DocTypeDef _def = new();
    /// <summary>Posisi asli field di metadata sumber, untuk mempertahankan urutan saat kolom digabung kembali.</summary>
    private readonly Dictionary<FieldDef, int> _origIndex = new(ReferenceEqualityComparer.Instance);
    public DocTypeDef Def => _def;
    public List<DesignerTab> Tabs { get; } = [];

    public IEnumerable<FieldDef> AllFields => Tabs.SelectMany(t => t.Sections).SelectMany(s => s.Columns).SelectMany(c => c);

    public static DocTypeDef Clone(DocTypeDef d) => MetadataJson.ParseDocType(MetadataJson.Serialize(d));

    public static DocTypeDesign From(DocTypeDef source)
    {
        var design = new DocTypeDesign { _def = Clone(source) };
        for (var i = 0; i < design._def.Fields.Count; i++) design._origIndex[design._def.Fields[i]] = i;
        foreach (var f in design._def.Fields)
        {
            var tabTitle = string.IsNullOrWhiteSpace(f.Tab) ? DefaultTab : f.Tab!;
            var tab = design.Tabs.FirstOrDefault(t => t.Title == tabTitle);
            if (tab is null) design.Tabs.Add(tab = new DesignerTab { Title = tabTitle });
            if (!string.IsNullOrWhiteSpace(f.Tab)) tab.Explicit = true;

            var main = tab.Sections.FirstOrDefault(s => s.SubTab is null && s.Title == f.Section);
            if (main is null) tab.Sections.Add(main = new DesignerSection { Title = f.Section });
            var sec = main;
            if (!string.IsNullOrWhiteSpace(f.SubTab))
            {
                var name = f.SubTab.Trim();
                sec = tab.Sections.FirstOrDefault(s => s.Parent == main && s.SubTab == name)!;
                if (sec is null)
                {
                    sec = new DesignerSection { Title = main.Title, SubTab = name, Parent = main };
                    tab.Sections.Insert(LastOfGroup(tab, main) + 1, sec);
                }
            }
            var col = Math.Clamp(f.Column, 1, 3);
            while (sec.Columns.Count < col) sec.Columns.Add([]);
            sec.Columns[col - 1].Add(f);
        }
        if (design.Tabs.Count == 0) design.AddTab();
        return design;
    }

    /// <summary>Menulis layout kembali ke metadata. Section/tab kosong tidak tersimpan (lihat <see cref="Warnings"/>).</summary>
    public DocTypeDef ToDef(int version)
    {
        var def = Clone(_def);
        def.Version = version;
        def.Fields = [];
        foreach (var tab in Tabs)
            foreach (var sec in tab.Sections)
                foreach (var (f, c) in MergeColumns(sec))
                {
                    var copy = JsonSerializer.Deserialize<FieldDef>(JsonSerializer.Serialize(f, MetadataJson.Options), MetadataJson.Options)!;
                    copy.Tab = tab.Title == DefaultTab && !tab.Explicit ? null : tab.Title;
                    copy.Section = string.IsNullOrWhiteSpace(sec.EffectiveTitle) ? null : sec.EffectiveTitle;
                    copy.SubTab = string.IsNullOrWhiteSpace(sec.SubTab) ? null : sec.SubTab.Trim();
                    copy.Column = c + 1;
                    def.Fields.Add(copy);
                }
        return def;
    }

    /// <summary>
    /// Menggabungkan kolom section menjadi satu urutan. Compiler hanya memperhatikan urutan di dalam kolom, jadi
    /// selang-seling antar-kolom bebas; dipilih yang paling mendekati urutan asli agar diff tidak melaporkan
    /// "urutan berubah" bila layout tidak diubah. Field baru diletakkan tepat setelah field di atasnya.
    /// </summary>
    private IEnumerable<(FieldDef Field, int Column)> MergeColumns(DesignerSection sec)
    {
        var keyed = sec.Columns.Select(col =>
        {
            var prev = -1.0; var list = new List<(FieldDef, double)>();
            foreach (var f in col)
            {
                prev = _origIndex.TryGetValue(f, out var i) ? Math.Max(i, prev) : prev + 0.001;
                list.Add((f, prev));
            }
            return list;
        }).ToList();
        var pos = new int[keyed.Count];
        while (true)
        {
            var best = -1;
            for (var c = 0; c < keyed.Count; c++)
                if (pos[c] < keyed[c].Count && (best < 0 || keyed[c][pos[c]].Item2 < keyed[best][pos[best]].Item2)) best = c;
            if (best < 0) yield break;
            yield return (keyed[best][pos[best]].Item1, best);
            pos[best]++;
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            var w = new List<string>();
            foreach (var t in Tabs)
            {
                if (t.IsEmpty) w.Add($"Tab \"{t.Title}\" belum berisi field dan tidak akan tersimpan.");
                // A section that only holds inner tabs has no own fields: that is fine.
                foreach (var s in t.Sections.Where(s => s.IsEmpty && !t.IsEmpty && s.SubTab is null && !HasSubTabs(s)))
                    w.Add($"Section \"{s.Title ?? "(tanpa judul)"}\" di tab \"{t.Title}\" kosong dan tidak akan tersimpan.");
                foreach (var s in t.Sections.Where(s => s.IsEmpty && s.SubTab is not null))
                    w.Add($"Tab \"{s.SubTab}\" di dalam section \"{s.EffectiveTitle ?? "(tanpa judul)"}\" belum berisi field dan tidak akan tersimpan.");
                foreach (var g in t.Sections.Where(s => s.SubTab is null).GroupBy(s => s.Title ?? "").Where(g => g.Count() > 1))
                    w.Add($"Ada {g.Count()} section berjudul \"{(g.Key == "" ? "(tanpa judul)" : g.Key)}\" di tab \"{t.Title}\"; saat disimpan keduanya digabung.");
            }
            foreach (var g in Tabs.GroupBy(t => t.Title).Where(g => g.Count() > 1))
                w.Add($"Ada {g.Count()} tab berjudul \"{g.Key}\"; saat disimpan keduanya digabung.");
            return w;
        }
    }

    // ---------------- Operasi field ----------------

    public (DesignerTab Tab, DesignerSection Section, int Column, int Index)? Locate(FieldDef f)
    {
        foreach (var t in Tabs)
            foreach (var s in t.Sections)
                for (var c = 0; c < s.Columns.Count; c++)
                {
                    var i = s.Columns[c].IndexOf(f);
                    if (i >= 0) return (t, s, c, i);
                }
        return null;
    }

    /// <summary>Prefix for new field names, e.g. "custom_" when customizing an application DocType.</summary>
    public string? NewFieldPrefix { get; set; }

    public string UniqueName(string baseName)
    {
        var names = AllFields.Select(f => f.Name).ToHashSet();
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++) if (!names.Contains($"{baseName}_{i}")) return $"{baseName}_{i}";
    }

    /// <summary>Field baru dengan nilai awal yang lolos compiler untuk tipe tersebut.</summary>
    public FieldDef AddField(string type, DesignerSection section, int column, int? index, MetadataSnapshot snapshot)
    {
        var f = new FieldDef { Name = UniqueName((NewFieldPrefix ?? "") + type.ToLowerInvariant() + "_field"), Label = $"{type} baru", Type = type };
        ApplyTypeDefaults(f, snapshot);
        Insert(f, section, column, index);
        return f;
    }

    public void ApplyTypeDefaults(FieldDef f, MetadataSnapshot snapshot)
    {
        if (f.Type == "Select" && f.Options is not { Count: > 0 }) f.Options = ["Opsi 1", "Opsi 2"];
        if (f.Type == "Link" && (f.LinkTo is null || !snapshot.TryGet(f.LinkTo, out _)))
            f.LinkTo = snapshot.DocTypes.Values.Where(d => !d.Def.IsChild && d.Name != _def.Name).Select(d => d.Name).Order().FirstOrDefault();
        if (f.Type == "Table" && f.ChildDocType is null)
            f.ChildDocType = snapshot.DocTypes.Values.Where(d => d.Def.IsChild).Select(d => d.Name).Order().FirstOrDefault();
        if (f.Type == "TableMultiSelect" && (f.LinkTo is null || !snapshot.TryGet(f.LinkTo, out _)))
            f.LinkTo = snapshot.DocTypes.Values.Where(d => !d.Def.IsChild && d.Name != _def.Name).Select(d => d.Name).Order().FirstOrDefault();
        if (f.Type == "DynamicLink" && f.LinkToField is null)
            f.LinkToField = AllFields.FirstOrDefault(x => x.Type == "Select" && x != f)?.Name;
        if (f.Type == "HTML" && f.Content is null) f.Content = "<p>Text shown on the form.</p>";
        if (f.Type is "Rating" && f.Max is null) f.Max = 5;
        if (f.Type is not ("Select" or "Barcode")) f.Options = null;
        if (f.Type is not ("Link" or "TableMultiSelect")) f.LinkTo = null;
        if (f.Type != "Link") f.LookupDialog = false;
        if (f.Type != "DynamicLink") f.LinkToField = null;
        if (f.Type is not ("HTML" or "Heading")) f.Content = null;
        if (f.Type != "Button") f.ButtonAction = null;
        if (FieldKinds.IsLayout(f.Type) || f.Type is "Password" or "Attach" or "Signature")
        {
            f.Compute = null; f.FetchFrom = null; f.Default = null; f.InListView = false;
            if (FieldKinds.IsLayout(f.Type)) f.RequiredWhen = null;
        }
        if (!FieldKinds.IsQueryable(f.Type) && f.Type != "Image") f.InListView = false;
        if (f.Type != "Table") { f.ChildDocType = null; f.AddFromLookup = null; }
    }

    public void Move(FieldDef f, DesignerSection section, int column, int? index)
    {
        if (Locate(f) is not { } from) return;
        // Pindah di kolom yang sama ke posisi sesudahnya: indeks bergeser setelah elemen dihapus.
        if (from.Section == section && from.Column == column && index is { } i && i > from.Index) index = i - 1;
        from.Section.Columns[from.Column].RemoveAt(from.Index);
        Insert(f, section, column, index);
    }

    public void MoveBy(FieldDef f, int delta)
    {
        if (Locate(f) is not { } at) return;
        var list = at.Section.Columns[at.Column];
        var target = at.Index + delta;
        if (target < 0 || target >= list.Count) return;
        list.RemoveAt(at.Index);
        list.Insert(target, f);
    }

    public void Remove(FieldDef f)
    {
        if (Locate(f) is { } at) at.Section.Columns[at.Column].RemoveAt(at.Index);
    }

    private static void Insert(FieldDef f, DesignerSection section, int column, int? index)
    {
        column = Math.Clamp(column, 0, section.Columns.Count - 1);
        var list = section.Columns[column];
        list.Insert(index is { } i ? Math.Clamp(i, 0, list.Count) : list.Count, f);
    }

    // ---------------- Operasi tab & section ----------------

    public DesignerTab AddTab()
    {
        var title = Tabs.Count == 0 ? DefaultTab : NextTitle("Tab", Tabs.Select(t => t.Title));
        var tab = new DesignerTab { Title = title, Explicit = Tabs.Count > 0 };
        tab.Sections.Add(new DesignerSection { Title = "Section 1" });
        Tabs.Add(tab);
        return tab;
    }

    public DesignerSection AddSection(DesignerTab tab)
    {
        var sec = new DesignerSection { Title = NextTitle("Section", tab.Sections.Select(s => s.Title ?? "")) };
        tab.Sections.Add(sec);
        return sec;
    }

    /// <summary>Mengubah jumlah kolom; field di kolom yang dihapus dipindah ke kolom terakhir yang tersisa.</summary>
    public void SetColumns(DesignerSection sec, int count)
    {
        count = Math.Clamp(count, 1, 3);
        while (sec.Columns.Count < count) sec.Columns.Add([]);
        while (sec.Columns.Count > count)
        {
            var last = sec.Columns[^1];
            sec.Columns.RemoveAt(sec.Columns.Count - 1);
            sec.Columns[^1].AddRange(last);
        }
    }

    // ---------------- Layout breaks (palette: Tab, Section, Column) ----------------
    //
    // Like Frappe's Tab Break / Section Break / Column Break, but only in the designer: dropping one on a field starts
    // a new tab, section or column at that field; that field and everything after it in reading order move along.
    // Metadata keeps describing the layout through the tab/section/column properties of the fields.

    /// <summary>Layout controls offered in the palette, in display order.</summary>
    public static readonly string[] LayoutBreaks = ["Tab", "SubTab", "Section", "Column"];

    public DesignerTab TabOf(DesignerSection sec) => Tabs.First(t => t.Sections.Contains(sec));

    /// <summary>
    /// Moves the fields from (<paramref name="column"/>, <paramref name="index"/>) onwards out of the section: from that
    /// column, the fields at and after the index; from later columns, all fields. The new section keeps the column count.
    /// </summary>
    private static DesignerSection SplitOff(DesignerSection sec, int column, int index, string? title)
    {
        var tail = new DesignerSection { Title = title };
        while (tail.Columns.Count < sec.Columns.Count) tail.Columns.Add([]);
        for (var c = column; c < sec.Columns.Count; c++)
        {
            var from = c == column ? Math.Clamp(index, 0, sec.Columns[c].Count) : 0;
            tail.Columns[c].AddRange(sec.Columns[c].Skip(from));
            sec.Columns[c].RemoveRange(from, sec.Columns[c].Count - from);
        }
        // Columns that start empty in the new section would leave a gap on the left: shift the fields to the left.
        var used = tail.Columns.Where(c => c.Count > 0).ToList();
        if (used.Count > 0 && used.Count < tail.Columns.Count)
        {
            tail.Columns.Clear();
            tail.Columns.AddRange(used);
        }
        return tail;
    }

    /// <summary>Tab break: a new tab after the current one starts at the given field (or at the end of the column).</summary>
    public DesignerTab BreakTab(DesignerSection sec, int column, int? index)
    {
        var tab = TabOf(sec);
        var at = index ?? sec.Columns[column].Count;
        var tail = SplitOff(sec, column, at, sec.Title);
        tail.SubTab = sec.SubTab;
        tail.Parent = sec.Parent;
        var next = tab.Sections.IndexOf(sec) + 1;
        var moved = tab.Sections.Skip(next).ToList();
        tab.Sections.RemoveRange(next, moved.Count);

        var newTab = new DesignerTab { Title = NextTitle("Tab", Tabs.Select(t => t.Title)), Explicit = true };
        if (sec.IsEmpty && tab.Sections.Count > 1)
        {
            // Nothing of the section stays behind (dropped on its first field, or into an empty section): the
            // section itself moves to the new tab instead of leaving an empty section here.
            tab.Sections.Remove(sec);
            newTab.Sections.Add(tail.IsEmpty ? sec : tail);
        }
        else if (!tail.IsEmpty) newTab.Sections.Add(tail);
        newTab.Sections.AddRange(moved);
        if (newTab.Sections.Count == 0) newTab.Sections.Add(new DesignerSection { Title = "Section 1" });
        Tabs.Insert(Tabs.IndexOf(tab) + 1, newTab);

        // A tab that kept nothing would not be saved; keep one (empty) section so it can still be filled.
        if (tab.Sections.Count == 0) tab.Sections.Add(new DesignerSection { Title = "Section 1" });
        return newTab;
    }

    /// <summary>Section break: a new section after the current one starts at the given field (or at the end of the column).</summary>
    public DesignerSection BreakSection(DesignerSection sec, int column, int? index)
    {
        var tab = TabOf(sec);
        var title = NextTitle("Section", tab.Sections.Select(s => s.Title ?? ""));
        var tail = SplitOff(sec, column, index ?? sec.Columns[column].Count, title);
        if (tail.IsEmpty) tail = new DesignerSection { Title = title };
        // After a section's inner tabs (not between the section and its tabs).
        var pos = sec.Parent is null ? LastOfGroup(tab, sec) : tab.Sections.IndexOf(sec);
        tab.Sections.Insert(pos + 1, tail);
        return tail;
    }

    /// <summary>
    /// Column break: the given field and the ones below it start the next column; later columns shift right.
    /// False when the section already has the maximum of 3 columns.
    /// </summary>
    public bool BreakColumn(DesignerSection sec, int column, int? index)
    {
        if (sec.Columns.Count >= 3) return false;
        var col = sec.Columns[column];
        var at = Math.Clamp(index ?? col.Count, 0, col.Count);
        var moved = col.Skip(at).ToList();
        col.RemoveRange(at, moved.Count);
        sec.Columns.Insert(column + 1, moved);
        return true;
    }

    // ---------------- Tabs inside a section ----------------

    /// <summary>
    /// Moves a section up/down together with its inner tabs; an inner tab moves among the tabs of its section.
    /// </summary>
    public void MoveSection(DesignerTab tab, DesignerSection sec, int delta)
    {
        if (sec.Parent is { } main)
        {
            var siblings = tab.Sections.Where(s => s.Parent == main).ToList();
            var i = siblings.IndexOf(sec);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= siblings.Count) return;
            var a = tab.Sections.IndexOf(sec);
            var b = tab.Sections.IndexOf(siblings[j]);
            (tab.Sections[a], tab.Sections[b]) = (tab.Sections[b], tab.Sections[a]);
            return;
        }
        // Blocks: a section followed by its inner tabs.
        var blocks = new List<List<DesignerSection>>();
        foreach (var s in tab.Sections)
        {
            if (s.Parent is not null && blocks.Count > 0 && blocks[^1][0] == s.Parent)
                blocks[^1].Add(s);
            else blocks.Add([s]);
        }
        var from = blocks.FindIndex(b => b[0] == sec);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= blocks.Count) return;
        (blocks[from], blocks[to]) = (blocks[to], blocks[from]);
        tab.Sections.Clear();
        tab.Sections.AddRange(blocks.SelectMany(b => b));
    }

    public bool HasSubTabs(DesignerSection sec) => Tabs.SelectMany(t => t.Sections).Any(s => s.Parent == sec);

    /// <summary>Index of the last part of a section's group (the section itself or its last inner tab).</summary>
    private static int LastOfGroup(DesignerTab tab, DesignerSection main)
    {
        var last = tab.Sections.IndexOf(main);
        for (var i = last + 1; i < tab.Sections.Count && tab.Sections[i].Parent == main; i++) last = i;
        return last;
    }

    private static string NextSubTabTitle(DesignerTab tab, DesignerSection main) =>
        NextTitle("Tab", tab.Sections.Where(s => s.Parent == main).Select(s => s.SubTab ?? ""));

    /// <summary>Adds an empty tab inside the section (or next to the inner tab) <paramref name="sec"/>.</summary>
    public DesignerSection AddSubTab(DesignerSection sec)
    {
        var tab = TabOf(sec);
        var main = sec.Parent ?? sec;
        var inner = new DesignerSection { Title = main.Title, SubTab = NextSubTabTitle(tab, main), Parent = main };
        while (inner.Columns.Count < sec.Columns.Count) inner.Columns.Add([]);
        tab.Sections.Insert(LastOfGroup(tab, main) + 1, inner);
        return inner;
    }

    /// <summary>
    /// "Tab in section" dropped on a field: a new tab inside the section starts at that field; the field and the ones
    /// after it (in this section part) move into it. Dropped into an empty part or below the last field: an empty
    /// inner tab is added.
    /// </summary>
    public DesignerSection BreakSubTab(DesignerSection sec, int column, int? index)
    {
        var tab = TabOf(sec);
        var main = sec.Parent ?? sec;
        var inner = SplitOff(sec, column, index ?? sec.Columns[column].Count, main.Title);
        inner.SubTab = NextSubTabTitle(tab, main);
        inner.Parent = main;
        // Directly after the part it came from, so the reading order stays the same.
        var at = sec == main ? LastOfGroup(tab, main) + 1 : tab.Sections.IndexOf(sec) + 1;
        tab.Sections.Insert(at, inner);
        return inner;
    }

    public bool RemoveSection(DesignerTab tab, DesignerSection sec)
    {
        if (!sec.IsEmpty || HasSubTabs(sec)) return false;
        tab.Sections.Remove(sec);
        if (tab.Sections.Count == 0) tab.Sections.Add(new DesignerSection { Title = "Section 1" });
        return true;
    }

    public bool RemoveTab(DesignerTab tab)
    {
        if (!tab.IsEmpty || Tabs.Count == 1) return false;
        Tabs.Remove(tab);
        return true;
    }

    public static void MoveItem<T>(List<T> list, T item, int delta)
    {
        var i = list.IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.RemoveAt(i);
        list.Insert(j, item);
    }

    private static string NextTitle(string prefix, IEnumerable<string> existing)
    {
        var set = existing.ToHashSet();
        for (var i = 1; ; i++) if (!set.Contains($"{prefix} {i}")) return $"{prefix} {i}";
    }

    // ---------------- Nilai default (JsonElement ⇄ teks) ----------------

    public static string DefaultText(FieldDef f) => f.Default is { } e && e.ValueKind != JsonValueKind.Null
        ? e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()
        : "";

    public static JsonElement? ParseDefault(string? text, string fieldType)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('$')) return JsonSerializer.SerializeToElement(t);
        if (fieldType is "Int" or "Float" or "Currency" or "Percent" or "Rating" or "Duration" && decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return JsonSerializer.SerializeToElement(d);
        if (fieldType == "Check" && bool.TryParse(t, out var b)) return JsonSerializer.SerializeToElement(b);
        return JsonSerializer.SerializeToElement(t);
    }
}
