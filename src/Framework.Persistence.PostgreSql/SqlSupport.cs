using System.Globalization;
using System.Text;
using System.Text.Json;
using Framework.Documents;
using Framework.Expressions;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Konversi Document.Data ⇄ JSONB. Saat membaca, nilai dinormalisasi ulang memakai tipe field dari metadata
/// aktif (Int → long, Currency → decimal, Check → bool, Date → "yyyy-MM-dd") sehingga perilakunya identik
/// dengan store in-memory. Field yang sudah tidak ada di metadata tetap dibaca dalam bentuk generik.
/// </summary>
public static class JsonCodec
{
    public static string Write(IReadOnlyDictionary<string, object?> data)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) WriteObject(w, data);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteObject(Utf8JsonWriter w, IReadOnlyDictionary<string, object?> data)
    {
        w.WriteStartObject();
        foreach (var (k, v) in data) { w.WritePropertyName(k); WriteValue(w, v); }
        w.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case decimal d: w.WriteNumberValue(d); break;
            case long l: w.WriteNumberValue(l); break;
            case int i: w.WriteNumberValue(i); break;
            case double db: w.WriteNumberValue(db); break;
            case DateOnly d: w.WriteStringValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case JsonElement e: e.WriteTo(w); break;
            case IReadOnlyDictionary<string, object?> o: WriteObject(w, o); break;
            case IEnumerable<object?> list:
                w.WriteStartArray();
                foreach (var item in list) WriteValue(w, item);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
        }
    }

    public static Dictionary<string, object?> Read(string json, CompiledDocType? meta, Func<string, CompiledDocType?> resolve)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadObject(doc.RootElement, meta, resolve);
    }

    private static Dictionary<string, object?> ReadObject(JsonElement obj, CompiledDocType? meta, Func<string, CompiledDocType?> resolve)
    {
        var result = new Dictionary<string, object?>();
        foreach (var p in obj.EnumerateObject())
        {
            if (meta is not null && meta.ByName.TryGetValue(p.Name, out var f))
            {
                if (f.Def.Type == "Table")
                {
                    var child = f.Def.ChildDocType is { } c ? resolve(c) : null;
                    result[p.Name] = p.Value.ValueKind == JsonValueKind.Array
                        ? p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => ReadObject(e, child, resolve)).ToList()
                        : new List<Dictionary<string, object?>>();
                    continue;
                }
                var n = f.Type.Normalize(p.Value);
                if (n.Ok) { result[p.Name] = n.Value; continue; }
            }
            result[p.Name] = p.Name == "idx" && p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : Generic(p.Value);
        }
        return result;
    }

    private static object? Generic(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(Generic).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Generic(p.Value)),
        _ => null
    };
}

/// <summary>
/// Menerjemahkan StoreQuery menjadi SQL berparameter. NILAI selalu dikirim sebagai parameter. Nama field data
/// ditulis sebagai literal (data ->> 'field') agar ekspresi identik dengan indeks dari DocumentIndexSync dan planner
/// selalu bisa memakainya — aman karena nama field hanya diterima bila ada di metadata (lolos compiler:
/// ^[a-z][a-z0-9_]*$) dan tetap di-escape. Kolom standar dari peta tetap; operator & arah sort dari allowlist.
/// </summary>
internal sealed class SqlQueryBuilder(CompiledDocType? meta)
{
    private readonly List<NpgsqlParameter> _params = [];
    public IReadOnlyList<NpgsqlParameter> Parameters => _params;

    private static readonly Dictionary<string, (string Column, NpgsqlDbType Type)> Standard = new()
    {
        [StandardFields.Name] = ("name", NpgsqlDbType.Text),
        [StandardFields.Owner] = ("owner", NpgsqlDbType.Text),
        [StandardFields.DocStatus] = ("docstatus", NpgsqlDbType.Numeric),
        [StandardFields.WorkflowState] = ("workflow_state", NpgsqlDbType.Text),
        [StandardFields.Created] = ("created", NpgsqlDbType.TimestampTz),
        [StandardFields.Modified] = ("modified", NpgsqlDbType.TimestampTz),
        [StandardFields.ModifiedBy] = ("modified_by", NpgsqlDbType.Text),
        [StandardFields.AmendedFrom] = ("amended_from", NpgsqlDbType.Text),
    };

    public string Param(object? value, NpgsqlDbType type)
    {
        _params.Add(new NpgsqlParameter { Value = value ?? DBNull.Value, NpgsqlDbType = type });
        return "$" + _params.Count;
    }

    /// <summary>Ekspresi SQL untuk sebuah field beserta tipe nilai yang dibandingkan.</summary>
    public (string Sql, NpgsqlDbType Type) Field(string field)
    {
        if (Standard.TryGetValue(field, out var std))
            return (std.Type == NpgsqlDbType.Numeric ? $"d.{std.Column}::numeric" : $"d.{std.Column}", std.Type);
        if (meta is null || !meta.ByName.TryGetValue(field, out var f) || f.Def.Type == "Table"
            || (f.Def.Type != "Image" && !FieldKinds.IsQueryable(f.Def.Type)))
            throw new InvalidPayloadException($"Field '{field}' tidak dapat dipakai dalam query", [field]);
        var type = f.Type.ClrType == typeof(long) || f.Type.ClrType == typeof(decimal) ? NpgsqlDbType.Numeric
                 : f.Type.ClrType == typeof(bool) ? NpgsqlDbType.Boolean : NpgsqlDbType.Text;
        return (DocumentIndexSync.Expression(f, "d."), type);
    }

    private static object? Coerce(object? v, NpgsqlDbType type) => v switch
    {
        null => null,
        _ when type == NpgsqlDbType.Numeric => Values.ToDecimal(v) ?? throw new InvalidPayloadException($"Nilai '{v}' harus berupa angka"),
        _ when type == NpgsqlDbType.Boolean => v is bool b ? b : bool.Parse(Convert.ToString(v, CultureInfo.InvariantCulture)!),
        _ when type == NpgsqlDbType.TimestampTz => v is DateTimeOffset d ? d : DateTimeOffset.Parse(Convert.ToString(v, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)
    };

    public string Filter(FilterClause f)
    {
        var (sql, type) = Field(f.Field);
        switch (f.Op)
        {
            case "like":
                return $"{(type == NpgsqlDbType.Text ? sql : sql + "::text")} ILIKE {Param("%" + EscapeLike(Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? "") + "%", NpgsqlDbType.Text)}";
            case "in":
                var items = (f.Value as IEnumerable<object?> ?? []).Select(v => Coerce(v, type)).ToArray();
                var arr = type switch
                {
                    NpgsqlDbType.Numeric => (object)items.Select(x => (decimal)x!).ToArray(),
                    NpgsqlDbType.Boolean => items.Select(x => (bool)x!).ToArray(),
                    _ => items.Select(x => (string?)x).ToArray()
                };
                return $"{sql} = ANY({Param(arr, NpgsqlDbType.Array | type)})";
            case "=":
                return f.Value is null ? $"{sql} IS NULL" : $"{sql} = {Param(Coerce(f.Value, type), type)}";
            case "!=":
                return f.Value is null ? $"{sql} IS NOT NULL" : $"{sql} IS DISTINCT FROM {Param(Coerce(f.Value, type), type)}";
            case ">" or ">=" or "<" or "<=":
                return $"{sql} {f.Op} {Param(Coerce(f.Value, type), type)}";
            default:
                throw new InvalidPayloadException($"Operator filter '{f.Op}' tidak diizinkan");
        }
    }

    public string Scope(RowScope scope) => scope switch
    {
        RowScope.AllRows => "TRUE",
        RowScope.AnyOf any => "(" + string.Join(" OR ", any.Conditions.Select(c =>
            c.Field == StandardFields.Owner
                ? $"d.owner = {Param(c.Value, NpgsqlDbType.Text)}"
                : $"{MatchField(c.Field)} = {Param(c.Value, NpgsqlDbType.Text)}")) + ")",
        _ => "FALSE"
    };

    /// <summary>Field kondisi row scope (match:field=atribut); nama berasal dari metadata yang sudah tervalidasi.</summary>
    private static string MatchField(string field)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(field, "^[a-z][a-z0-9_]{0,63}$"))
            throw new InvalidPayloadException($"Field '{field}' tidak dapat dipakai dalam query", [field]);
        return $"(d.data ->> '{field}')";
    }

    public string Search(string term, IReadOnlyList<string> fields)
    {
        if (fields.Count == 0) return "TRUE";
        var p = Param("%" + EscapeLike(term) + "%", NpgsqlDbType.Text);
        return "(" + string.Join(" OR ", fields.Select(f => Field(f) is var (sql, type) && type == NpgsqlDbType.Text ? $"{sql} ILIKE {p}" : $"{sql}::text ILIKE {p}")) + ")";
    }

    public string OrderBy(IReadOnlyList<SortDef> sort)
    {
        var parts = sort.Select(s =>
        {
            var desc = s.Direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
            // Kolom standar NOT NULL: tanpa klausa NULLS agar cocok dengan arah indeks bawaan (ix_documents_list).
            if (NotNullStandard.Contains(s.Field)) return $"{Field(s.Field).Sql} {(desc ? "DESC" : "ASC")}";
            // Disamakan dengan store in-memory: nilai kosong dianggap paling kecil. Indeks urut dari
            // DocumentIndexSync memakai arah yang sama persis.
            return $"{Field(s.Field).Sql} {SortSuffix(desc)}";
        }).Append("d.name ASC");
        return string.Join(", ", parts);
    }

    public static string SortSuffix(bool desc) => desc ? "DESC NULLS LAST" : "ASC NULLS FIRST";

    private static readonly HashSet<string> NotNullStandard =
        [StandardFields.Name, StandardFields.Owner, StandardFields.Created, StandardFields.Modified, StandardFields.ModifiedBy];

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
