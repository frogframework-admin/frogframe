using System.Globalization;
using BlazorBlueprint.Components;
using Framework.Documents;
using Framework.Expressions;
using Framework.Metadata;

namespace Framework.Web;

public static class Fmt
{
    public static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");

    public static string Value(CompiledField? f, object? v)
    {
        if (v is null || v is string { Length: 0 }) return "—";
        switch (f?.Def.Type)
        {
            case "Currency":
                return Values.ToDecimal(v) is { } c ? "Rp" + c.ToString("#,0.##", Id) : v.ToString()!;
            case "Float" or "Int":
                return Values.ToDecimal(v) is { } n ? n.ToString("#,0.######", Id) : v.ToString()!;
            case "Date":
                return v is string s && DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d.ToString("d MMM yyyy", Id) : v.ToString()!;
            case "Check":
                return v is true ? "Yes" : "No";
            case "Image":
                return "Attached image";
        }
        return v switch
        {
            DateTimeOffset dto => When(dto),
            bool b => b ? "Yes" : "No",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };
    }

    /// <summary>Raw value for input (without thousand separators).</summary>
    public static string Raw(object? v) => v switch
    {
        null => "",
        decimal d => d.ToString("0.######", CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };

    public static string When(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var span = DateTimeOffset.Now - t;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1 && local.Date == DateTime.Today) return $"today {local:HH:mm}";
        return local.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Tone(CompiledDocType meta, Document d) => Tone(meta, d.WorkflowState, d.DocStatus);

    public static string Tone(CompiledDocType meta, string? state, int docStatus) =>
        meta.Workflow is not null && state is not null && meta.Workflow.States.TryGetValue(state, out var s)
            ? s.Tone
            : docStatus switch { 1 => "positive", 2 => "negative", _ => "neutral" };

    /// <summary>Varian BbBadge untuk status di daftar.</summary>
    public static BadgeVariant BadgeFor(string tone) => tone switch
    {
        "positive" => BadgeVariant.SoftSuccess,
        "negative" => BadgeVariant.SoftDestructive,
        "pending" => BadgeVariant.Warning,
        _ => BadgeVariant.Soft
    };

    /// <summary>Varian BbButton untuk aksi workflow.</summary>
    public static ButtonVariant ButtonFor(string tone) => tone switch
    {
        "negative" => ButtonVariant.Destructive,
        "neutral" => ButtonVariant.Outline,
        _ => ButtonVariant.Default
    };

    public static string StateLabel(string? state, int docStatus) => state ?? DocStatus.Label(docStatus);
}
