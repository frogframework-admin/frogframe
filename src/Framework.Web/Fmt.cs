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
            // Gap #3 types
            case "Datetime":
                return DatetimeFieldType.ToZone(v) is { } local ? local.ToString("d MMM yyyy HH:mm", Id) : v.ToString()!;
            case "Time":
                return v is string t && t.Length >= 5 ? t[..5] : v.ToString()!;
            case "Duration":
                return Values.ToDecimal(v) is { } sec ? DurationFieldType.Display((long)sec) : v.ToString()!;
            case "Percent":
                return Values.ToDecimal(v) is { } pct ? pct.ToString("#,0.##", Id) + "%" : v.ToString()!;
            case "Rating":
                return Values.ToDecimal(v) is { } stars ? $"★ {stars.ToString("0.#", Id)} / {(f!.Def.Max is { } m ? m.ToString("0", Id) : "5")}" : v.ToString()!;
            case "Password":
                return "••••••••";
            case "Attach":
                return "Attached file";
            case "Signature":
                return "Signed";
            case "TableMultiSelect":
                return string.Join(", ", TableMultiSelectFieldType.Items(v)) is { Length: > 0 } items ? items : "—";
            case "TextEditor":
                return Shorten(PlainText(v as string ?? ""), 200);
            case "Markdown" or "JSON":
                return v is string longText ? Shorten(longText, 2000) : v.ToString()!;
            case "HTML" or "Button" or "Heading":
                return "";
        }
        return v switch
        {
            DateTimeOffset dto => When(dto),
            bool b => b ? "Yes" : "No",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };
    }

    /// <summary>Text of an HTML fragment (tags removed, entities decoded, whitespace collapsed).</summary>
    public static string PlainText(string html) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
