using System.Text.Json;
using System.Text.Json.Serialization;

namespace Framework.Metadata;

// Model metadata dipisah per tanggung jawab (RFC §5): dokumen, field, list view, permission, API, workflow.

public sealed class DocTypeDef
{
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "";
    public string? Module { get; set; }
    public string? Description { get; set; }
    public bool IsChild { get; set; }
    public StorageDef Storage { get; set; } = new();
    public DocumentDef Document { get; set; } = new();
    public NamingDef Naming { get; set; } = new();
    public List<FieldDef> Fields { get; set; } = [];
    public List<RuleDef> Rules { get; set; } = [];
    public List<DocHookDef> Hooks { get; set; } = [];
    public ListViewDef ListView { get; set; } = new();
    public List<PermissionDef> Permissions { get; set; } = [];
    public ApiDef Api { get; set; } = new();
    /// <summary>Impor Excel/CSV. Default aktif untuk DocType yang bukan child table.</summary>
    public ImportDef Import { get; set; } = new();
    public string? Workflow { get; set; }
    /// <summary>Format cetak (A4). Bila null, dipakai format default dari layout form.</summary>
    public PrintFormatDef? PrintFormat { get; set; }
}

public sealed class DocHookDef
{
    public string Name { get; set; } = "";
    public string Event { get; set; } = "BeforeValidate";
    public bool Enabled { get; set; } = true;
    public DocHookConditionDef? When { get; set; }
    public List<DocHookActionDef> Actions { get; set; } = [];
}

public sealed class DocHookConditionDef
{
    public string Field { get; set; } = "";
    public string Operator { get; set; } = "Equals";
    public JsonElement? Value { get; set; }
}

public sealed class DocHookActionDef
{
    public string Type { get; set; } = "Reject";
    public string? Message { get; set; }
    public string? Field { get; set; }
    public string? EventType { get; set; }
}

/// <summary>Format cetak dokumen (RFC: generic views). Hanya deklaratif — tidak ada template berisi kode.</summary>
public sealed class PrintFormatDef
{
    /// <summary>Judul dokumen di kertas, mis. "Pengajuan Pembelian".</summary>
    public string? Title { get; set; }
    /// <summary>Blok isian. Bila null, diambil dari section form (field yang boleh dibaca user).</summary>
    public List<PrintSectionDef>? Sections { get; set; }
    /// <summary>Kolom per child table, mis. { "items": ["item_name", "qty", "rate", "amount"] }. Default: semua kolom.</summary>
    public Dictionary<string, List<string>>? TableColumns { get; set; }
    /// <summary>Field yang ditampilkan di blok total, mis. ["total"].</summary>
    public List<string> TotalFields { get; set; } = [];
    /// <summary>Field angka yang ditulis terbilang (Rupiah), mis. "total".</summary>
    public string? AmountInWordsField { get; set; }
    /// <summary>Kotak tanda tangan elektronik, diisi otomatis dari riwayat workflow.</summary>
    public List<PrintSignatureDef> Signatures { get; set; } = [];
    public string? FooterNote { get; set; }
    /// <summary>Tanda air DRAFT / BELUM DISETUJUI / DITOLAK untuk dokumen yang belum final.</summary>
    public bool Watermark { get; set; } = true;
}

public sealed class PrintSectionDef
{
    public string? Title { get; set; }
    public List<string> Fields { get; set; } = [];
}

public sealed class PrintSignatureDef
{
    public string Label { get; set; } = "";
    /// <summary>Ditandatangani oleh actor yang memindahkan dokumen dari state ini (opsional)…</summary>
    public string? FromState { get; set; }
    /// <summary>…ke salah satu state ini, mis. ["Disetujui", "Menunggu Finance"].</summary>
    public List<string> ToStates { get; set; } = [];
    /// <summary>Sembunyikan kotak bila tidak pernah ditandatangani dan dokumen sudah tidak menunggu proses.</summary>
    public bool Optional { get; set; }
}

public sealed class StorageDef
{
    /// <summary>"document" (tabel generik + payload JSON) atau "relational" (tabel per DocType).</summary>
    public string Strategy { get; set; } = "document";
    public string? Table { get; set; }
}

public sealed class DocumentDef
{
    public bool TrackChanges { get; set; } = true;
    public bool AllowSubmit { get; set; }
    public bool AllowCancel { get; set; }
    public bool AllowAmend { get; set; }
    public bool AllowDelete { get; set; } = true;
}

public sealed class ImportDef
{
    public bool Enabled { get; set; } = true;
}

public sealed class NamingDef
{
    /// <summary>Pola nama, mis. "PR-{YYYY}-{#####}". Token: {YYYY}, {MM}, {#..#}.</summary>
    public string Pattern { get; set; } = "{doctype}-{#####}";
}

public sealed class FieldDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Data";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public string? Tab { get; set; }
    public string? Section { get; set; }
    public int Column { get; set; } = 1;
    public string? RequiredWhen { get; set; }
    public string? VisibleWhen { get; set; }
    public string? ReadOnlyWhen { get; set; }
    public JsonElement? Default { get; set; }
    public string? Compute { get; set; }
    /// <summary>"linkField.targetField" — nilai diambil server dari dokumen tertaut.</summary>
    public string? FetchFrom { get; set; }
    public string? LinkTo { get; set; }
    public string? ChildDocType { get; set; }
    public List<string>? Options { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public string? PatternMessage { get; set; }
    public List<string>? Validators { get; set; }
    public bool InListView { get; set; }
    public int PermLevel { get; set; }
    /// <summary>Field Link: tampilkan tombol untuk memilih dari tabel dalam dialog.</summary>
    public bool LookupDialog { get; set; }
    /// <summary>Field Table: tombol "Tambah dari daftar" yang mengisi baris dari DocType lain.</summary>
    public LookupAddDef? AddFromLookup { get; set; }
}

/// <summary>
/// Konfigurasi "tambah baris dari daftar" pada child table.
/// Contoh: pilih beberapa Product → setiap produk menjadi satu baris item.
/// </summary>
public sealed class LookupAddDef
{
    /// <summary>Field Link di child DocType yang diisi dengan nama dokumen terpilih, mis. "product".</summary>
    public string LinkField { get; set; } = "";
    /// <summary>Pemetaan field child ← field dokumen sumber, mis. { "item_name": "product_name", "rate": "price" }.</summary>
    public Dictionary<string, string> Map { get; set; } = new();
    public bool Multiple { get; set; } = true;
    public string? ButtonLabel { get; set; }
}

public sealed class RuleDef
{
    public string Name { get; set; } = "";
    /// <summary>Kondisi yang harus bernilai true.</summary>
    public string Expr { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Field { get; set; }
}

public sealed class ListViewDef
{
    public string? TitleField { get; set; }
    public List<string>? Columns { get; set; }
    public List<SortDef> DefaultSort { get; set; } = [new() { Field = "modified", Direction = "desc" }];
    public List<string> SearchFields { get; set; } = ["name"];
    public List<string> Filters { get; set; } = [];
    public int PageSize { get; set; } = 20;
}

public sealed class SortDef
{
    public string Field { get; set; } = "modified";
    public string Direction { get; set; } = "desc";
}

public sealed class PermissionDef
{
    public string Role { get; set; } = "";
    public List<string> Actions { get; set; } = [];
    /// <summary>"all", "owner", atau "match:{field}={atributUser}".</summary>
    public string Scope { get; set; } = "all";
    public int PermLevel { get; set; }
}

public sealed class ApiDef
{
    public bool Enabled { get; set; }
    public List<string> Operations { get; set; } = [];
}

public sealed class WorkflowDef
{
    public string Name { get; set; } = "";
    public string? DocType { get; set; }
    public int Version { get; set; } = 1;
    public string InitialState { get; set; } = "";
    public List<WorkflowStateDef> States { get; set; } = [];
    public List<WorkflowTransitionDef> Transitions { get; set; } = [];
}

public sealed class WorkflowStateDef
{
    public string Name { get; set; } = "";
    /// <summary>0 = Draft (dapat diubah), 1 = Submitted (terkunci), 2 = Cancelled.</summary>
    public int DocStatus { get; set; }
    public string Tone { get; set; } = "neutral";
}

public sealed class WorkflowTransitionDef
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Action { get; set; } = "";
    public List<string> AllowedRoles { get; set; } = [];
    public string? Condition { get; set; }
    public bool RequireComment { get; set; }
}

public static class MetadataJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static DocTypeDef ParseDocType(string json) =>
        JsonSerializer.Deserialize<DocTypeDef>(json, Options) ?? throw new JsonException("Metadata kosong");

    public static WorkflowDef ParseWorkflow(string json) =>
        JsonSerializer.Deserialize<WorkflowDef>(json, Options) ?? throw new JsonException("Workflow kosong");

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
