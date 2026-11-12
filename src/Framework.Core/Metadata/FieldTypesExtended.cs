using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Framework.Communication;
using Framework.Security;

namespace Framework.Metadata;

// ============================================================================================================
//  Field types beyond the original 12 (Gap #3 in the Frappe gap analysis). Each type normalizes and validates
//  its value on the server; the editor category (Editor) tells FieldInput.razor which BlazorBlueprint component
//  renders it. Values are always stored in a JSON-friendly, culture-invariant form.
// ============================================================================================================

/// <summary>Site-wide defaults used while normalizing values.</summary>
public static class FieldTypeSettings
{
    /// <summary>Time zone used for date-times sent without an offset, and for display. Default Asia/Jakarta.</summary>
    public static TimeZoneInfo DefaultTimeZone { get; set; } = FindZone("Asia/Jakarta") ?? FindZone("SE Asia Standard Time") ?? TimeZoneInfo.Utc;

    /// <summary>Country calling code used for phone numbers written with a leading 0 (e.g. 0812… → +62812…).</summary>
    public static string DefaultCountryCode { get; set; } = "62";

    private static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }
}

/// <summary>Groups of field types that need special handling across the platform.</summary>
public static class FieldKinds
{
    /// <summary>Layout-only fields: they hold no value and are never stored.</summary>
    public static readonly IReadOnlySet<string> Layout = new HashSet<string>(StringComparer.Ordinal) { "HTML", "Button", "Heading" };

    private static readonly HashSet<string> NotQueryable = new(StringComparer.Ordinal)
        { "Table", "Image", "Password", "Attach", "Signature", "TableMultiSelect", "JSON", "TextEditor", "Markdown", "HTML", "Button", "Heading" };

    private static readonly HashSet<string> NotReported = new(StringComparer.Ordinal)
        { "Table", "Image", "Password", "Attach", "Signature", "HTML", "Button", "Heading" };

    private static readonly HashSet<string> NotInChildTable = new(StringComparer.Ordinal)
        { "TextEditor", "Markdown", "JSON", "Signature", "TableMultiSelect", "Password", "HTML", "Button", "Heading" };

    private static readonly HashSet<string> NotImportable = new(StringComparer.Ordinal)
        { "Image", "Table", "Attach", "Signature", "Password", "HTML", "Button", "Heading" };

    /// <summary>Types that cannot have a formula, fetchFrom or default value.</summary>
    private static readonly HashSet<string> NoDerivedValue = new(StringComparer.Ordinal)
        { "Password", "Attach", "Signature", "Image", "Table", "HTML", "Button", "Heading" };

    public static bool IsLayout(string? type) => type is not null && Layout.Contains(type);

    /// <summary>Usable as list column, filter, sort or search field (and in API/SQL filters).</summary>
    public static bool IsQueryable(string? type) => type is not null && !NotQueryable.Contains(type);

    /// <summary>Exposed as a column in reporting.* views.</summary>
    public static bool IsReported(string? type) => type is not null && !NotReported.Contains(type);

    public static bool AllowedInChildTable(string? type) => type is not null && !NotInChildTable.Contains(type);

    public static bool IsImportable(string? type) => type is not null && !NotImportable.Contains(type);

    public static bool AllowsDerivedValue(string? type) => type is not null && !NoDerivedValue.Contains(type);

    /// <summary>Fields whose value is a file id managed by FileService (linked to the document on save).</summary>
    public static bool IsFileField(string? type) => type is "Image" or "Attach";
}

internal static class TextValue
{
    /// <summary>Common text normalization: blank → null, trim, maximum length.</summary>
    public static NormalizeResult Normalize(object? raw, int max, bool trim = true)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        if (v is JsonElement) return NormalizeResult.Fail("harus berupa teks");
        var s = Convert.ToString(v, CultureInfo.InvariantCulture)!;
        if (trim) s = s.Trim();
        if (s.Length == 0) return NormalizeResult.Success(null);
        return s.Length > max ? NormalizeResult.Fail($"maksimal {max:N0} karakter") : NormalizeResult.Success(s);
    }
}

// ------------------------------------------------------------------------------------------------------------
//  Date and time
// ------------------------------------------------------------------------------------------------------------

/// <summary>
/// Date and time, stored in UTC as "yyyy-MM-ddTHH:mm:ssZ" (sorts correctly as text, cast to timestamptz in reporting).
/// Input without an offset is read in <see cref="FieldTypeSettings.DefaultTimeZone"/>.
/// </summary>
public sealed partial class DatetimeFieldType : IFieldType
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)] private static partial Regex OffsetRx();
    private static readonly string[] LocalFormats =
        ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    public string Name => "Datetime";
    public Type ClrType => typeof(DateTimeOffset);
    public string Editor => "datetime";

    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        DateTimeOffset? utc = v switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt => dt.Kind switch
            {
                DateTimeKind.Utc => new DateTimeOffset(dt),
                DateTimeKind.Local => new DateTimeOffset(dt).ToUniversalTime(),
                _ => FromLocal(dt),
            },
            string s => Parse(s.Trim()),
            _ => null,
        };
        return utc is { } u
            ? NormalizeResult.Success(u.ToString(Format, CultureInfo.InvariantCulture))
            : NormalizeResult.Fail("tanggal-waktu harus berformat yyyy-MM-ddTHH:mm:ss (UTC dengan Z, atau dengan offset)");
    }

    private static DateTimeOffset? Parse(string s)
    {
        if (OffsetRx().IsMatch(s))
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o) ? o.ToUniversalTime() : null;
        return DateTime.TryParseExact(s, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? FromLocal(local)
            : null;
    }

    private static DateTimeOffset FromLocal(DateTime unspecified)
    {
        var zone = FieldTypeSettings.DefaultTimeZone;
        var offset = zone.GetUtcOffset(DateTime.SpecifyKind(unspecified, DateTimeKind.Unspecified));
        return new DateTimeOffset(DateTime.SpecifyKind(unspecified, DateTimeKind.Unspecified), offset).ToUniversalTime();
    }

    /// <summary>Stored value → time in the given zone (default zone when null).</summary>
    public static DateTimeOffset? ToZone(object? stored, TimeZoneInfo? zone = null) =>
        stored is string s && DateTimeOffset.TryParseExact(s, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u)
            ? TimeZoneInfo.ConvertTime(u, zone ?? FieldTypeSettings.DefaultTimeZone)
            : null;
}

/// <summary>Time of day, stored as "HH:mm:ss".</summary>
public sealed class TimeFieldType : IFieldType
{
    private static readonly string[] Formats = ["HH:mm:ss", "HH:mm", "H:mm", "H:mm:ss"];
    public string Name => "Time";
    public Type ClrType => typeof(TimeOnly);
    public string Editor => "time";

    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        TimeOnly? t = v switch
        {
            TimeOnly to => to,
            TimeSpan ts when ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1) => TimeOnly.FromTimeSpan(ts),
            DateTime dt => TimeOnly.FromDateTime(dt),
            string s when TimeOnly.TryParseExact(s.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
            _ => null,
        };
        return t is { } x
            ? NormalizeResult.Success(x.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            : NormalizeResult.Fail("jam harus berformat HH:mm atau HH:mm:ss");
    }

    public static TimeSpan? ToTimeSpan(object? stored) =>
        stored is string s && TimeOnly.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToTimeSpan() : null;
}

/// <summary>
/// Duration, stored as whole seconds (long). Accepts seconds, "hh:mm[:ss]", or "1d 2h 30m 10s".
/// </summary>
public sealed partial class DurationFieldType : IFieldType
{
    public const long MaxSeconds = 10L * 366 * 24 * 3600;
    [GeneratedRegex(@"^\s*(?:(\d+)\s*d)?\s*(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?\s*(?:(\d+)\s*s)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitsRx();
    [GeneratedRegex(@"^\s*(\d+):([0-5]?\d)(?::([0-5]?\d))?\s*$")] private static partial Regex ClockRx();

    public string Name => "Duration";
    public Type ClrType => typeof(long);
    public string Editor => "duration";

    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        long? seconds = v switch
        {
            TimeSpan ts => (long)ts.TotalSeconds,
            long l => l,
            int i => i,
            decimal d when d == Math.Truncate(d) => (long)d,
            string s => Parse(s),
            _ => null,
        };
        if (seconds is null) return NormalizeResult.Fail("durasi harus berupa detik, hh:mm:ss, atau mis. 1h 30m");
        if (seconds < 0 || seconds > MaxSeconds) return NormalizeResult.Fail("durasi di luar batas");
        return NormalizeResult.Success(seconds.Value);
    }

    private static long? Parse(string s)
    {
        if (long.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        if (ClockRx().Match(s) is { Success: true } c)
            return long.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                 + long.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                 + (c.Groups[3].Success ? long.Parse(c.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
        if (UnitsRx().Match(s) is { Success: true } u && s.Any(char.IsDigit))
        {
            long Part(int g, long factor) => u.Groups[g].Success ? long.Parse(u.Groups[g].Value, CultureInfo.InvariantCulture) * factor : 0;
            return Part(1, 86400) + Part(2, 3600) + Part(3, 60) + Part(4, 1);
        }
        return null;
    }

    /// <summary>Seconds → "1d 2h 30m" style text.</summary>
    public static string Display(long seconds)
    {
        if (seconds == 0) return "0s";
        var ts = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>();
        if (ts.Days > 0) parts.Add($"{ts.Days}d");
        if (ts.Hours > 0) parts.Add($"{ts.Hours}h");
        if (ts.Minutes > 0) parts.Add($"{ts.Minutes}m");
        if (ts.Seconds > 0) parts.Add($"{ts.Seconds}s");
        return string.Join(" ", parts);
    }
}

// ------------------------------------------------------------------------------------------------------------
//  Text-like
// ------------------------------------------------------------------------------------------------------------

/// <summary>
/// Phone number in E.164 form (+628123456789). Spaces, dashes, dots and parentheses are removed; a leading 0 becomes
/// the default country code (<see cref="FieldTypeSettings.DefaultCountryCode"/>), a leading 00 becomes +.
/// </summary>
public sealed partial class PhoneFieldType : IFieldType
{
    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")] private static partial Regex E164Rx();
    [GeneratedRegex(@"[\s\-().]")] private static partial Regex SeparatorsRx();

    public string Name => "Phone";
    public Type ClrType => typeof(string);
    public string Editor => "phone";
    public bool Searchable => true;

    public NormalizeResult Normalize(object? raw)
    {
        var r = TextValue.Normalize(raw, 32);
        if (!r.Ok || r.Value is null) return r;
        var s = SeparatorsRx().Replace((string)r.Value, "");
        if (s.StartsWith("00", StringComparison.Ordinal)) s = "+" + s[2..];
        else if (s.StartsWith('0')) s = "+" + FieldTypeSettings.DefaultCountryCode + s[1..];
        else if (!s.StartsWith('+') && s.StartsWith(FieldTypeSettings.DefaultCountryCode, StringComparison.Ordinal)) s = "+" + s;
        return E164Rx().IsMatch(s) ? NormalizeResult.Success(s) : NormalizeResult.Fail("nomor telepon tidak valid (contoh: +6281234567890 atau 081234567890)");
    }
}

/// <summary>Rich text (HTML from the Quill-based editor). Sanitized on the server on every write, whatever the source.</summary>
public sealed class TextEditorFieldType : IFieldType
{
    public const int MaxLength = 100_000;
    public string Name => "TextEditor";
    public Type ClrType => typeof(string);
    public string Editor => "texteditor";

    public NormalizeResult Normalize(object? raw)
    {
        var r = TextValue.Normalize(raw, MaxLength * 2, trim: false);
        if (!r.Ok || r.Value is null) return r;
        var clean = HtmlContentSanitizer.Sanitize((string)r.Value)?.Trim();
        if (string.IsNullOrEmpty(clean) || clean is "<p><br></p>" or "<p></p>") return NormalizeResult.Success(null);
        return clean.Length > MaxLength ? NormalizeResult.Fail($"maksimal {MaxLength:N0} karakter") : NormalizeResult.Success(clean);
    }
}

/// <summary>Markdown source. Rendered (and sanitized) only when displayed.</summary>
public sealed class MarkdownFieldType : IFieldType
{
    public string Name => "Markdown";
    public Type ClrType => typeof(string);
    public string Editor => "markdown";
    public NormalizeResult Normalize(object? raw) => TextValue.Normalize(raw, TextEditorFieldType.MaxLength, trim: false);
}

/// <summary>
/// JSON document stored as compact JSON text. Accepts a JSON string or a JSON object/array in the API payload.
/// </summary>
public sealed class JsonFieldType : IFieldType
{
    public const int MaxLength = 100_000;
    public string Name => "JSON";
    public Type ClrType => typeof(string);
    public string Editor => "json";

    public NormalizeResult Normalize(object? raw)
    {
        if (raw is JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } el)
            return Check(el.GetRawText());
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        return v is string s ? Check(s) : NormalizeResult.Fail("harus berupa JSON");
    }

    private static NormalizeResult Check(string text)
    {
        if (text.Length > MaxLength) return NormalizeResult.Fail($"maksimal {MaxLength:N0} karakter");
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            return NormalizeResult.Success(JsonSerializer.Serialize(doc.RootElement));
        }
        catch (JsonException ex) { return NormalizeResult.Fail($"JSON tidak valid: {ex.Message}"); }
    }
}

/// <summary>Value encoded by a barcode (printable ASCII). The symbology is FieldDef.Options[0] (default Code128).</summary>
public sealed partial class BarcodeFieldType : IFieldType
{
    [GeneratedRegex(@"^[\x20-\x7E]+$")] private static partial Regex PrintableRx();

    /// <summary>Symbologies the BlazorBlueprint barcode renderer supports (FieldDef.Options[0]).</summary>
    public static readonly IReadOnlyList<string> Formats = ["Code128", "Code39", "Ean13", "Ean8", "UpcA", "Itf", "Codabar", "Msi"];

    public string Name => "Barcode";
    public Type ClrType => typeof(string);
    public string Editor => "barcode";
    public bool Searchable => true;

    public NormalizeResult Normalize(object? raw)
    {
        var r = TextValue.Normalize(raw, 140);
        if (!r.Ok || r.Value is null) return r;
        return PrintableRx().IsMatch((string)r.Value) ? r : NormalizeResult.Fail("hanya karakter ASCII yang bisa dicetak");
    }
}

/// <summary>Color in hex: #rrggbb or #rrggbbaa (lowercase). #rgb is expanded.</summary>
public sealed partial class ColorFieldType : IFieldType
{
    [GeneratedRegex("^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")] private static partial Regex HexRx();
    public string Name => "Color";
    public Type ClrType => typeof(string);
    public string Editor => "color";

    public NormalizeResult Normalize(object? raw)
    {
        var r = TextValue.Normalize(raw, 9);
        if (!r.Ok || r.Value is null) return r;
        var m = HexRx().Match((string)r.Value);
        if (!m.Success) return NormalizeResult.Fail("warna harus berformat hex, mis. #1f6feb");
        var hex = m.Groups[1].Value.ToLowerInvariant();
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        return NormalizeResult.Success("#" + hex);
    }
}

/// <summary>
/// Latitude/longitude stored as "lat,lng" (6 decimals, invariant). Accepts that text or {"lat":…,"lng":…}.
/// </summary>
public sealed class GeolocationFieldType : IFieldType
{
    public string Name => "Geolocation";
    public Type ClrType => typeof(string);
    public string Editor => "geolocation";

    public NormalizeResult Normalize(object? raw)
    {
        decimal? lat = null, lng = null;
        if (raw is JsonElement { ValueKind: JsonValueKind.Object } o)
        {
            lat = Num(o, "lat") ?? Num(o, "latitude");
            lng = Num(o, "lng") ?? Num(o, "lon") ?? Num(o, "longitude");
        }
        else
        {
            var v = Raw.Unwrap(raw);
            if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
            if (v is string s && s.Split(',') is { Length: 2 } p
                && decimal.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && decimal.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            { lat = a; lng = b; }
        }
        if (lat is null || lng is null) return NormalizeResult.Fail("lokasi harus berformat 'lintang,bujur', mis. -6.200000,106.816666");
        if (lat is < -90 or > 90 || lng is < -180 or > 180) return NormalizeResult.Fail("lintang -90..90 dan bujur -180..180");
        return NormalizeResult.Success(Format(lat.Value, lng.Value));
    }

    public static string Format(decimal lat, decimal lng) =>
        $"{Math.Round(lat, 6).ToString("0.######", CultureInfo.InvariantCulture)},{Math.Round(lng, 6).ToString("0.######", CultureInfo.InvariantCulture)}";

    public static (decimal Lat, decimal Lng)? Parse(object? stored) =>
        stored is string s && s.Split(',') is { Length: 2 } p
        && decimal.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
        && decimal.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? (a, b) : null;

    private static decimal? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : null;
}

// ------------------------------------------------------------------------------------------------------------
//  Numbers
// ------------------------------------------------------------------------------------------------------------

/// <summary>Star rating 0..10 in steps of 0.5. The maximum shown in the UI is FieldDef.Max (default 5).</summary>
public sealed class RatingFieldType : IFieldType
{
    public string Name => "Rating";
    public Type ClrType => typeof(decimal);
    public string Editor => "rating";

    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        var d = Expressions.Values.ToDecimal(v);
        if (d is null) return NormalizeResult.Fail("harus berupa angka");
        if (d < 0 || d > 10) return NormalizeResult.Fail("nilai rating 0 sampai 10");
        return NormalizeResult.Success(Math.Round(d.Value * 2, MidpointRounding.AwayFromZero) / 2);
    }
}

// ------------------------------------------------------------------------------------------------------------
//  References
// ------------------------------------------------------------------------------------------------------------

/// <summary>Link whose target DocType is stored in another field of the same document (FieldDef.LinkToField).</summary>
public sealed class DynamicLinkFieldType : IFieldType
{
    public string Name => "DynamicLink";
    public Type ClrType => typeof(string);
    public string Editor => "dynamiclink";
    public bool Searchable => true;
    public NormalizeResult Normalize(object? raw) => TextValue.Normalize(raw, 140);
}

/// <summary>
/// Several links to one DocType (FieldDef.LinkTo), stored as a JSON array of document names.
/// (Frappe stores this as a child table with one Link field; a plain array is simpler and keeps the same checks.)
/// </summary>
public sealed class TableMultiSelectFieldType : IFieldType
{
    public const int MaxItems = 200;
    public string Name => "TableMultiSelect";
    public Type ClrType => typeof(List<string>);
    public string Editor => "multiselect";

    public NormalizeResult Normalize(object? raw)
    {
        IEnumerable<object?>? items = raw switch
        {
            null => Array.Empty<object?>(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => Array.Empty<object?>(),
            JsonElement { ValueKind: JsonValueKind.Array } a => a.EnumerateArray().Select(e => (object?)(e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Null ? null : e.GetRawText())),
            JsonElement { ValueKind: JsonValueKind.String } s => Split(s.GetString()),
            string s => s.TrimStart().StartsWith('[') ? FromJsonText(s) : Split(s),
            IEnumerable<string> list => list.Cast<object?>(),
            IEnumerable<object?> objs => objs,
            _ => null,
        };
        if (items is null) return NormalizeResult.Fail("harus berupa daftar nama dokumen");

        var result = new List<string>();
        foreach (var item in items)
        {
            if (item is null) continue;
            if (item is not string name) return NormalizeResult.Fail("setiap item harus berupa nama dokumen");
            name = name.Trim();
            if (name.Length == 0) continue;
            if (name.Length > 140) return NormalizeResult.Fail("nama dokumen maksimal 140 karakter");
            if (!result.Contains(name, StringComparer.Ordinal)) result.Add(name);
        }
        return result.Count > MaxItems ? NormalizeResult.Fail($"maksimal {MaxItems} item") : NormalizeResult.Success(result);
    }

    private static IEnumerable<object?> Split(string? s) => (s ?? "").Split(',').Select(x => (object?)x);

    private static IEnumerable<object?>? FromJsonText(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => (object?)(e.ValueKind == JsonValueKind.String ? e.GetString() : null)).ToList()
                : null;
        }
        catch (JsonException) { return null; }
    }

    public static IReadOnlyList<string> Items(object? stored) => stored switch
    {
        IEnumerable<string> l => l.ToList(),
        IEnumerable<object?> o => o.OfType<string>().ToList(),
        _ => [],
    };
}

/// <summary>Any file (PDF, Office, CSV, text, image) uploaded through FileService; the value is the file id.</summary>
public sealed partial class AttachFieldType : IFieldType
{
    [GeneratedRegex("^f_[0-9a-f]{32}$")] private static partial Regex IdRx();
    public string Name => "Attach";
    public Type ClrType => typeof(string);
    public string Editor => "attach";

    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        return v is string s && IdRx().IsMatch(s) ? NormalizeResult.Success(s) : NormalizeResult.Fail("bukan file yang valid");
    }
}

// ------------------------------------------------------------------------------------------------------------
//  Signature and password
// ------------------------------------------------------------------------------------------------------------

/// <summary>
/// Signature as SVG markup (drawn or typed). Checked to be a plain SVG document without scripts, event handlers,
/// external references or embedded HTML. The UI always renders it through an &lt;img&gt; data URI, where SVG
/// scripts never run, so the check is a second layer.
/// </summary>
public sealed class SignatureFieldType : IFieldType
{
    public const int MaxLength = 200_000;
    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.OrdinalIgnoreCase)
        { "script", "foreignObject", "iframe", "object", "embed", "image", "use", "style", "animate", "set", "a" };

    public string Name => "Signature";
    public Type ClrType => typeof(string);
    public string Editor => "signature";

    public NormalizeResult Normalize(object? raw)
    {
        var r = TextValue.Normalize(raw, MaxLength);
        if (!r.Ok || r.Value is null) return r;
        var svg = (string)r.Value;
        return IsSafeSvg(svg) ? NormalizeResult.Success(svg) : NormalizeResult.Fail("tanda tangan harus berupa SVG yang valid");
    }

    public static bool IsSafeSvg(string svg)
    {
        if (!svg.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true,
            });
            var first = true;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (first && !reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)) return false;
                first = false;
                if (ForbiddenElements.Contains(reader.LocalName)) return false;
                if (!reader.HasAttributes) continue;
                while (reader.MoveToNextAttribute())
                {
                    var name = reader.LocalName;
                    if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.Equals("href", StringComparison.OrdinalIgnoreCase) && !reader.Value.StartsWith('#')) return false;
                    if (reader.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
                }
                reader.MoveToElement();
            }
            return !first;
        }
        catch (XmlException) { return false; }
    }
}

/// <summary>
/// Password / secret. Encrypted with ASP.NET Data Protection before it is stored ("enc:v1:…"), never sent back to the
/// UI or API (reads show <see cref="Mask"/>), never indexed, listed, filtered, reported or exported.
/// Server code reads the plaintext with <see cref="Reveal"/>.
/// </summary>
public sealed class PasswordFieldType(ISecretProtector protector) : IFieldType
{
    public const string Prefix = "enc:v1:";
    public const string Mask = "********";
    public const int MaxLength = 256;

    public string Name => "Password";
    public Type ClrType => typeof(string);
    public string Editor => "password";

    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        if (v is not string s) return NormalizeResult.Fail("harus berupa teks");
        if (s.StartsWith(Prefix, StringComparison.Ordinal))
            return Reveal(s) is not null ? NormalizeResult.Success(s) : NormalizeResult.Fail("nilai terenkripsi tidak valid");
        if (s == Mask) return NormalizeResult.Fail("isi kata sandi baru atau kosongkan");
        if (s.Length > MaxLength) return NormalizeResult.Fail($"maksimal {MaxLength} karakter");
        return NormalizeResult.Success(Prefix + protector.Protect(s));
    }

    /// <summary>Stored value → plaintext, or null when empty or not decryptable.</summary>
    public string? Reveal(object? stored)
    {
        if (stored is not string s || !s.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return protector.Unprotect(s[Prefix.Length..]); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}

// ------------------------------------------------------------------------------------------------------------
//  Display-only
// ------------------------------------------------------------------------------------------------------------

/// <summary>Value shown read-only in forms (typically filled by fetchFrom, a formula, or server code).</summary>
public sealed class ReadOnlyFieldType : IFieldType
{
    public string Name => "ReadOnly";
    public Type ClrType => typeof(string);
    public string Editor => "readonly";
    public bool Searchable => true;
    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        return v is decimal or long or bool
            ? NormalizeResult.Success(Convert.ToString(v, CultureInfo.InvariantCulture))
            : TextValue.Normalize(raw, 5000);
    }
}

/// <summary>
/// Layout-only field (HTML block, Button, Heading). Holds no value: anything sent for it is ignored and nothing is stored.
/// HTML content comes from FieldDef.Content (sanitized when rendered); a Button runs the form action FieldDef.ButtonAction.
/// </summary>
public sealed class LayoutFieldType(string name, string editor) : IFieldType
{
    public string Name => name;
    public Type ClrType => typeof(void);
    public string Editor => editor;
    public NormalizeResult Normalize(object? raw) => NormalizeResult.Success(null);
}
