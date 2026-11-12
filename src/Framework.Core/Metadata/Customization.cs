using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Framework.Metadata;

// ================================================================================================================
//  Customization layer (Frappe: Custom Field + Property Setter).
//
//  A DocType that ships with the application (a *.doctype.json file) has a BASE definition owned by developers.
//  Administrators customize it through an overlay that is stored separately and applied on top of the base:
//
//      effective = Apply(base from file or store, customization) → compile
//
//  A deploy that brings a newer base keeps the overlay, so fields and settings added by administrators survive
//  application updates. Editing an app DocType in the UI never writes a new base version: the edited definition
//  is diffed against the base and only the difference is stored (Extract).
// ================================================================================================================

/// <summary>Overlay for one DocType. Applies to the whole installation (metadata is shared by all tenants).</summary>
public sealed class DocTypeCustomization
{
    public string DocType { get; set; } = "";
    /// <summary>Increases with every save (history is kept by the store).</summary>
    public int Version { get; set; }
    /// <summary>Fields added by administrators. Names must start with <see cref="Customizations.CustomFieldPrefix"/>.</summary>
    public List<CustomFieldDef> CustomFields { get; set; } = [];
    /// <summary>Property overrides on base fields (Field set) or on the DocType itself (Field null).</summary>
    public List<PropertySetterDef> PropertySetters { get; set; } = [];
    /// <summary>Full field order, only when administrators re-ordered fields of the base DocType.</summary>
    public List<string>? FieldOrder { get; set; }
    public string ModifiedBy { get; set; } = "";
    public DateTimeOffset Modified { get; set; }

    [JsonIgnore] public bool IsEmpty => CustomFields.Count == 0 && PropertySetters.Count == 0 && FieldOrder is null;
}

public sealed class CustomFieldDef
{
    /// <summary>Name of the field this one follows; null = first. When that field is gone, the custom field goes last.</summary>
    public string? InsertAfter { get; set; }
    public FieldDef Field { get; set; } = new();
}

public sealed class PropertySetterDef
{
    /// <summary>Base field name, or null for a DocType-level property.</summary>
    public string? Field { get; set; }
    /// <summary>JSON property name as in *.doctype.json (camelCase), e.g. "label", "requiredWhen", "listView".</summary>
    public string Property { get; set; } = "";
    public JsonElement Value { get; set; }
}

public sealed record CustomizationResult(DocTypeDef Def, IReadOnlyList<string> Warnings);

public static class Customizations
{
    public const string CustomFieldPrefix = "custom_";

    /// <summary>Base-field properties that change what the data is or where it comes from; only developers change them.</summary>
    public static readonly IReadOnlySet<string> LockedFieldProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "type", "linkTo", "childDocType", "compute", "fetchFrom", "linkToField", "buttonAction", "ignoreLinks", "addFromLookup",
    };

    /// <summary>DocType-level properties administrators may override.</summary>
    public static readonly IReadOnlySet<string> AllowedDocTypeProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "description", "listView", "permissions", "printFormat", "rules", "hooks", "import", "allowAutoRepeat", "workflow",
    };

    /// <summary>Not compared at DocType level (fields are handled separately; version belongs to the base).</summary>
    private static readonly HashSet<string> IgnoredDocTypeProperties = new(StringComparer.Ordinal) { "fields", "version", "dataMigration" };

    private static readonly JsonSerializerOptions Json = new(MetadataJson.Options) { WriteIndented = false };

    // ------------------------------------------------------------------------------------------------ apply

    /// <summary>Base + overlay → effective definition. Never throws; problems become warnings and the item is skipped.</summary>
    public static CustomizationResult Apply(DocTypeDef baseDef, DocTypeCustomization? customization)
    {
        var def = Clone(baseDef);
        var warnings = new List<string>();
        if (customization is null || customization.IsEmpty) return new(def, warnings);

        // 1. DocType-level property setters.
        foreach (var ps in customization.PropertySetters.Where(p => p.Field is null))
        {
            if (!AllowedDocTypeProperties.Contains(ps.Property)) { warnings.Add($"DocType property '{ps.Property}' cannot be customized; ignored."); continue; }
            if (TrySet(def, ps.Property, ps.Value, out var updated)) def = updated;
            else warnings.Add($"Override of '{ps.Property}' no longer fits this DocType; ignored.");
        }

        // 2. Field property setters.
        foreach (var ps in customization.PropertySetters.Where(p => p.Field is not null))
        {
            var index = def.Fields.FindIndex(f => f.Name == ps.Field);
            if (index < 0) { warnings.Add($"Field '{ps.Field}' no longer exists in the application; its override of '{ps.Property}' is ignored."); continue; }
            if (LockedFieldProperties.Contains(ps.Property)) { warnings.Add($"'{ps.Property}' of '{ps.Field}' cannot be customized; ignored."); continue; }
            if (TrySet(def.Fields[index], ps.Property, ps.Value, out var field)) def.Fields[index] = field;
            else warnings.Add($"Override of '{ps.Property}' on '{ps.Field}' no longer fits; ignored.");
        }

        // 3. Custom fields, in their stored order, each after its anchor.
        foreach (var cf in customization.CustomFields)
        {
            var field = Clone(cf.Field);
            if (def.Fields.Any(f => f.Name == field.Name))
            {
                warnings.Add($"The application now defines '{field.Name}' itself; the custom field is ignored (data stays).");
                continue;
            }
            var anchor = cf.InsertAfter is null ? -1 : def.Fields.FindIndex(f => f.Name == cf.InsertAfter);
            if (cf.InsertAfter is not null && anchor < 0) anchor = def.Fields.Count - 1; // anchor removed: append
            def.Fields.Insert(anchor + 1, field);
        }

        // 4. Explicit order (only when base fields were re-ordered). Unknown names are skipped; fields missing from
        //    the list (e.g. new in this deploy) keep their relative position at the end.
        if (customization.FieldOrder is { Count: > 0 } order)
        {
            var rank = order.Select((n, i) => (n, i)).GroupBy(x => x.n).ToDictionary(g => g.Key, g => g.First().i);
            def.Fields = def.Fields
                .Select((f, i) => (f, i))
                .OrderBy(x => rank.TryGetValue(x.f.Name, out var r) ? r : order.Count + x.i)
                .Select(x => x.f)
                .ToList();
        }

        return new(def, warnings);
    }

    // ------------------------------------------------------------------------------------------------ extract

    /// <summary>
    /// Computes the overlay that turns <paramref name="baseDef"/> into <paramref name="edited"/> (an edited effective
    /// definition). Changes administrators may not make to an app DocType are returned as errors.
    /// </summary>
    public static (DocTypeCustomization Customization, List<MetadataError> Errors) Extract(DocTypeDef baseDef, DocTypeDef edited)
    {
        var errors = new List<MetadataError>();
        var c = new DocTypeCustomization { DocType = baseDef.Name };
        if (edited.Name != baseDef.Name) errors.Add(new(baseDef.Name, "name", "The name of an application DocType cannot be changed."));
        if (edited.DataMigration is not null)
            errors.Add(new(baseDef.Name, "dataMigration", "Data migrations are not available for customizations of application DocTypes."));

        // DocType-level properties.
        var baseObj = (JsonObject)JsonSerializer.SerializeToNode(baseDef, Json)!;
        var editedObj = (JsonObject)JsonSerializer.SerializeToNode(edited, Json)!;
        foreach (var key in baseObj.Select(p => p.Key).Union(editedObj.Select(p => p.Key)).Distinct())
        {
            if (IgnoredDocTypeProperties.Contains(key)) continue;
            if (JsonNode.DeepEquals(baseObj[key], editedObj[key])) continue;
            if (!AllowedDocTypeProperties.Contains(key))
            {
                errors.Add(new(baseDef.Name, key, $"'{key}' of an application DocType can only be changed by the developers (in the DocType file)."));
                continue;
            }
            c.PropertySetters.Add(new PropertySetterDef { Field = null, Property = key, Value = ToElement(editedObj[key]) });
        }

        // Base fields: none may be removed; allowed properties become setters.
        var editedByName = edited.Fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var bf in baseDef.Fields)
        {
            if (!editedByName.TryGetValue(bf.Name, out var ef))
            {
                errors.Add(new(baseDef.Name, $"fields.{bf.Name}", $"Field '{bf.Name}' belongs to the application and cannot be removed. Hide it instead (Visible off or a 'Visible when' rule)."));
                continue;
            }
            var bo = (JsonObject)JsonSerializer.SerializeToNode(bf, Json)!;
            var eo = (JsonObject)JsonSerializer.SerializeToNode(ef, Json)!;
            foreach (var key in bo.Select(p => p.Key).Union(eo.Select(p => p.Key)).Distinct())
            {
                if (JsonNode.DeepEquals(bo[key], eo[key])) continue;
                if (LockedFieldProperties.Contains(key))
                {
                    errors.Add(new(baseDef.Name, $"fields.{bf.Name}.{key}", $"'{key}' of application field '{bf.Name}' can only be changed by the developers."));
                    continue;
                }
                c.PropertySetters.Add(new PropertySetterDef { Field = bf.Name, Property = key, Value = ToElement(eo[key]) });
            }
        }

        // New fields → custom fields anchored to the field before them.
        var baseNames = baseDef.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        string? previous = null;
        foreach (var ef in edited.Fields)
        {
            if (!baseNames.Contains(ef.Name))
            {
                if (!ef.Name.StartsWith(CustomFieldPrefix, StringComparison.Ordinal))
                    errors.Add(new(baseDef.Name, $"fields.{ef.Name}",
                        $"New fields on an application DocType must be named '{CustomFieldPrefix}…' (e.g. {CustomFieldPrefix}{ef.Name}) so they never clash with fields the developers add later."));
                else
                    c.CustomFields.Add(new CustomFieldDef { InsertAfter = previous, Field = Clone(ef) });
            }
            previous = ef.Name;
        }

        // Re-ordered base fields → explicit order.
        var baseOrder = baseDef.Fields.Select(f => f.Name).ToList();
        var editedBaseOrder = edited.Fields.Select(f => f.Name).Where(baseNames.Contains).ToList();
        if (!baseOrder.SequenceEqual(editedBaseOrder)) c.FieldOrder = edited.Fields.Select(f => f.Name).ToList();

        return (c, errors);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    public static DocTypeDef Clone(DocTypeDef d) => MetadataJson.ParseDocType(MetadataJson.Serialize(d));
    public static FieldDef Clone(FieldDef f) => JsonSerializer.Deserialize<FieldDef>(JsonSerializer.Serialize(f, Json), Json)!;

    private static JsonElement ToElement(JsonNode? node) =>
        node is null ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(node);

    /// <summary>Sets one JSON property and deserializes again; false when the value no longer fits the type.</summary>
    private static bool TrySet<T>(T target, string property, JsonElement value, out T result) where T : class
    {
        result = target;
        try
        {
            var obj = (JsonObject)JsonSerializer.SerializeToNode(target, Json)!;
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) obj.Remove(property);
            else obj[property] = JsonNode.Parse(value.GetRawText());
            var updated = obj.Deserialize<T>(Json);
            if (updated is null) return false;
            result = updated;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return false;
        }
    }
}
