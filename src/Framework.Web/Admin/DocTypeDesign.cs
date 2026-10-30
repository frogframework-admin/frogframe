using System.Globalization;
using System.Text.Json;
using Framework.Metadata;

namespace Framework.Web.Admin;

public sealed class DesignerSection
{
    public string? Title { get; set; }
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
    public static readonly string[] FieldTypes = ["Data", "Text", "Email", "Int", "Float", "Currency", "Check", "Date", "Select", "Link", "Table", "Image"];

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

            var sec = tab.Sections.FirstOrDefault(s => s.Title == f.Section);
            if (sec is null) tab.Sections.Add(sec = new DesignerSection { Title = f.Section });
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
                    copy.Section = string.IsNullOrWhiteSpace(sec.Title) ? null : sec.Title;
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
                foreach (var s in t.Sections.Where(s => s.IsEmpty && !t.IsEmpty))
                    w.Add($"Section \"{s.Title ?? "(tanpa judul)"}\" di tab \"{t.Title}\" kosong dan tidak akan tersimpan.");
                foreach (var g in t.Sections.GroupBy(s => s.Title ?? "").Where(g => g.Count() > 1))
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

    public string UniqueName(string baseName)
    {
        var names = AllFields.Select(f => f.Name).ToHashSet();
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++) if (!names.Contains($"{baseName}_{i}")) return $"{baseName}_{i}";
    }

    /// <summary>Field baru dengan nilai awal yang lolos compiler untuk tipe tersebut.</summary>
    public FieldDef AddField(string type, DesignerSection section, int column, int? index, MetadataSnapshot snapshot)
    {
        var f = new FieldDef { Name = UniqueName(type.ToLowerInvariant() + "_field"), Label = $"{type} baru", Type = type };
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
        if (f.Type != "Select") f.Options = null;
        if (f.Type != "Link") { f.LinkTo = null; f.LookupDialog = false; }
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

    public bool RemoveSection(DesignerTab tab, DesignerSection sec)
    {
        if (!sec.IsEmpty) return false;
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
        if (fieldType is "Int" or "Float" or "Currency" && decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return JsonSerializer.SerializeToElement(d);
        if (fieldType == "Check" && bool.TryParse(t, out var b)) return JsonSerializer.SerializeToElement(b);
        return JsonSerializer.SerializeToElement(t);
    }
}
