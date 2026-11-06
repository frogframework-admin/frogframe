using System.Globalization;
using System.Text.Json;
using Framework.Documents;
using Framework.Expressions;
using Framework.Persistence;

namespace Framework.Metadata;

/// <summary>Evaluasi filter Link statis: dipakai validasi server, pencarian Link, dan lookup dialog.</summary>
public static class LinkFilters
{
    public static readonly IReadOnlySet<string> Ops = new HashSet<string> { "=", "!=", "in" };

    public static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.Array => e.EnumerateArray().Select(ToValue).ToList(),
        _ => null,
    };

    private static object? Actual(Document doc, string field) =>
        doc.Get(field);

    public static bool Matches(Document doc, LinkFilterDef f)
    {
        var actual = Actual(doc, f.Field);
        var expected = ToValue(f.Value);
        return f.Op switch
        {
            "=" => Values.AreEqual(actual, expected),
            "!=" => !Values.AreEqual(actual, expected),
            "in" => expected is IEnumerable<object?> list && list.Any(x => Values.AreEqual(actual, x)),
            _ => false,
        };
    }

    public static string Describe(LinkFilterDef f) =>
        $"{f.Field} {f.Op} {(f.Value.ValueKind == JsonValueKind.Undefined ? "" : f.Value.GetRawText())}";

    public static IReadOnlyList<FilterClause> ToClauses(IEnumerable<LinkFilterDef> filters) =>
        filters.Select(f => new FilterClause(f.Field, f.Op, ToValue(f.Value))).ToList();
}
