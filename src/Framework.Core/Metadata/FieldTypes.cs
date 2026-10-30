using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Framework.Metadata;

public readonly record struct NormalizeResult(bool Ok, object? Value, string? Error)
{
    public static NormalizeResult Success(object? v) => new(true, v, null);
    public static NormalizeResult Fail(string e) => new(false, null, e);
}

public interface IFieldType
{
    string Name { get; }
    Type ClrType { get; }
    /// <summary>Kategori renderer UI: text, textarea, number, check, date, select, link, table.</summary>
    string Editor { get; }
    bool Searchable => false;
    NormalizeResult Normalize(object? raw);
}

public interface IFieldTypeRegistry
{
    bool TryGet(string type, out IFieldType fieldType);
    IEnumerable<string> Names { get; }
}

public sealed class FieldTypeRegistry : IFieldTypeRegistry
{
    private readonly Dictionary<string, IFieldType> _types = new(StringComparer.Ordinal);

    public FieldTypeRegistry(IEnumerable<IFieldType> types)
    {
        foreach (var t in types) _types[t.Name] = t;
    }

    public bool TryGet(string type, out IFieldType fieldType) => _types.TryGetValue(type, out fieldType!);
    public IEnumerable<string> Names => _types.Keys;

    public static IEnumerable<IFieldType> BuiltIns() =>
    [
        new TextType("Data", "text", 140), new TextType("Text", "textarea", 5000), new EmailType(),
        new IntType(), new DecimalType("Float", 6), new DecimalType("Currency", 2),
        new CheckType(), new DateType(), new SelectType(), new LinkType(), new TableType(), new ImageType()
    ];
}

internal static class Raw
{
    public static object? Unwrap(object? raw) => raw is JsonElement e ? e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => e
    } : raw;

    public static bool IsBlank(object? v) => v is null || (v is string s && s.Length == 0);
}

public class TextType(string name, string editor, int defaultMax) : IFieldType
{
    public string Name => name;
    public Type ClrType => typeof(string);
    public string Editor => editor;
    public bool Searchable => true;
    public virtual NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        if (v is JsonElement) return NormalizeResult.Fail("harus berupa teks");
        var s = Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim();
        if (s.Length > defaultMax) return NormalizeResult.Fail($"maksimal {defaultMax} karakter");
        return NormalizeResult.Success(s.Length == 0 ? null : s);
    }
}

public sealed partial class EmailType() : TextType("Email", "text", 254)
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailRx();
    public override NormalizeResult Normalize(object? raw)
    {
        var r = base.Normalize(raw);
        if (!r.Ok || r.Value is null) return r;
        return EmailRx().IsMatch((string)r.Value) ? r : NormalizeResult.Fail("format email tidak valid");
    }
}

public sealed class IntType : IFieldType
{
    public string Name => "Int";
    public Type ClrType => typeof(long);
    public string Editor => "number";
    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        var d = Expressions.Values.ToDecimal(v);
        if (d is null || d != Math.Truncate(d.Value)) return NormalizeResult.Fail("harus bilangan bulat");
        return NormalizeResult.Success((long)d.Value);
    }
}

public sealed class DecimalType(string name, int scale) : IFieldType
{
    public string Name => name;
    public Type ClrType => typeof(decimal);
    public string Editor => "number";
    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        var d = Expressions.Values.ToDecimal(v);
        return d is null ? NormalizeResult.Fail("harus berupa angka") : NormalizeResult.Success(Math.Round(d.Value, scale));
    }
}

public sealed class CheckType : IFieldType
{
    public string Name => "Check";
    public Type ClrType => typeof(bool);
    public string Editor => "check";
    public NormalizeResult Normalize(object? raw) => Raw.Unwrap(raw) switch
    {
        null => NormalizeResult.Success(false),
        bool b => NormalizeResult.Success(b),
        decimal d => NormalizeResult.Success(d != 0),
        string s when bool.TryParse(s, out var b) => NormalizeResult.Success(b),
        "1" => NormalizeResult.Success(true),
        "0" => NormalizeResult.Success(false),
        _ => NormalizeResult.Fail("harus true/false")
    };
}

public sealed class DateType : IFieldType
{
    public string Name => "Date";
    public Type ClrType => typeof(DateOnly);
    public string Editor => "date";
    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        if (v is DateOnly d0) return NormalizeResult.Success(d0.ToString("yyyy-MM-dd"));
        if (v is DateTime dt) return NormalizeResult.Success(DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd"));
        return v is string s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? NormalizeResult.Success(d.ToString("yyyy-MM-dd"))
            : NormalizeResult.Fail("tanggal harus berformat yyyy-MM-dd");
    }
}

public sealed class SelectType() : TextType("Select", "select", 140);
public sealed class LinkType() : TextType("Link", "link", 140);

/// <summary>Child table. Normalisasi baris dilakukan oleh DocumentService memakai metadata child.</summary>
public sealed class TableType : IFieldType
{
    public string Name => "Table";
    public Type ClrType => typeof(List<Dictionary<string, object?>>);
    public string Editor => "table";
    public NormalizeResult Normalize(object? raw)
    {
        var rows = new List<Dictionary<string, object?>>();
        switch (raw)
        {
            case null: return NormalizeResult.Success(rows);
            case JsonElement { ValueKind: JsonValueKind.Null }: return NormalizeResult.Success(rows);
            case JsonElement { ValueKind: JsonValueKind.Array } arr:
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) return NormalizeResult.Fail("setiap baris harus objek");
                    rows.Add(el.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
                }
                return NormalizeResult.Success(rows);
            case IEnumerable<IDictionary<string, object?>> list:
                rows.AddRange(list.Select(r => new Dictionary<string, object?>(r)));
                return NormalizeResult.Success(rows);
            case IEnumerable<object?> objs:
                foreach (var o in objs)
                {
                    if (o is not IDictionary<string, object?> d) return NormalizeResult.Fail("setiap baris harus objek");
                    rows.Add(new Dictionary<string, object?>(d));
                }
                return NormalizeResult.Success(rows);
            default: return NormalizeResult.Fail("harus berupa daftar baris");
        }
    }
}

/// <summary>Gambar: nilai = id file yang diunggah lewat FileService (mis. "f_3f2a…").</summary>
public sealed partial class ImageType : IFieldType
{
    [GeneratedRegex("^f_[0-9a-f]{32}$")] private static partial Regex IdRx();
    public string Name => "Image";
    public Type ClrType => typeof(string);
    public string Editor => "image";
    public NormalizeResult Normalize(object? raw)
    {
        var v = Raw.Unwrap(raw);
        if (Raw.IsBlank(v)) return NormalizeResult.Success(null);
        return v is string s && IdRx().IsMatch(s) ? NormalizeResult.Success(s) : NormalizeResult.Fail("bukan gambar yang valid");
    }
}
