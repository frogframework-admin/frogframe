using System.Text.Json;
using Framework.Metadata;

namespace Framework.Web.Components;

/// <summary>
/// Form state set by Client Scripts that FieldInput needs to render (cascaded by the form page).
/// Mutable on purpose: the form page updates it in place after each script run and re-renders.
/// </summary>
public sealed class ClientScriptFormState
{
    /// <summary>DocType of the form; FieldInputs of child tables (other DocTypes) ignore this state.</summary>
    public string DocType { get; set; } = "";

    /// <summary>Select options narrowed by <c>Form.SetOptions</c>, per field name.</summary>
    public Dictionary<string, IReadOnlyList<string>> Options { get; } = new(StringComparer.Ordinal);

    /// <summary>Copy of a compiled field with other Select options. The shared compiled metadata is never changed.</summary>
    public static CompiledField WithOptions(CompiledField field, IReadOnlyList<string> options)
    {
        var def = JsonSerializer.Deserialize<FieldDef>(JsonSerializer.Serialize(field.Def, MetadataJson.Options), MetadataJson.Options)!;
        def.Options = options.ToList();
        return new CompiledField
        {
            Def = def,
            Type = field.Type,
            RequiredWhen = field.RequiredWhen,
            VisibleWhen = field.VisibleWhen,
            ReadOnlyWhen = field.ReadOnlyWhen,
            Compute = field.Compute,
            FetchFrom = field.FetchFrom,
            Pattern = field.Pattern,
            StaticDefault = field.StaticDefault,
            DynamicDefault = field.DynamicDefault,
        };
    }
}
