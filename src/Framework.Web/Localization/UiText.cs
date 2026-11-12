using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Framework.Documents;
using Framework.Metadata;

namespace Framework.Localization;

/// <summary>
/// Translations for the current user's language (one instance per Blazor circuit / request). In Razor:
/// <c>@inject UiText T</c>, then <c>@T["Save draft"]</c>, <c>@T.Format("Back to {0}", T.Title(meta))</c>,
/// <c>@T.Label(f)</c>, <c>@T.Message(error)</c>.
/// The language comes from the UI culture chosen by <c>UseFrogFrameLocalization</c> (cookie, user preference, default).
/// </summary>
public sealed class UiText(TranslationCatalog catalog, Framework.Web.CurrentUser current)
{
    public string Language { get; } = Languages.FromCulture(CultureInfo.CurrentUICulture);

    private string? Tenant => current.User?.TenantId;

    /// <summary>Translation of a whole text; untranslated texts are returned as written.</summary>
    public string this[string? text] => catalog.Text(Tenant, Language, text);

    /// <summary>Translates the format text first, then inserts the arguments (which are not translated).</summary>
    public string Format(string format, params object?[] args)
    {
        var translated = this[format];
        try { return string.Format(CultureInfo.CurrentCulture, translated, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, format, args); }
    }

    /// <summary>Message composed elsewhere (validation errors, exceptions): exact match or template such as "{0} wajib diisi".</summary>
    public string Message(string? message) => catalog.Message(Tenant, Language, message);

    public string Label(CompiledField f) => this[f.Label];
    public string Title(CompiledDocType meta) => this[meta.Title];
    public string? Description(string? text) => text is null ? null : this[text];

    /// <summary>Display text of a stored option value (Select). The stored value itself never changes.</summary>
    public string Option(string? value) => this[value];

    /// <summary>Workflow state or document status label.</summary>
    public string State(string? workflowState, int docStatus) => this[Framework.Web.Fmt.StateLabel(workflowState, docStatus)];

    /// <summary>Fmt.Value plus translation of option values and yes/no.</summary>
    public string Value(CompiledField? f, object? v)
    {
        var text = Framework.Web.Fmt.Value(f, v);
        if (f is null) return text;
        return f.Type.Editor is "select" or "check" || v is bool ? this[text] : text;
    }

    // ------------------------------------------------------------------------------------------------ fields

    private static readonly ConditionalWeakTable<CompiledField, Dictionary<string, (int Version, CompiledField Field)>> FieldCache = new();

    /// <summary>
    /// A copy of a compiled field whose label and description are translated, for components that show
    /// <c>Field.Label</c> themselves (form, child table). Name, type, rules and options are those of the original.
    /// The shared compiled metadata is never changed.
    /// </summary>
    public CompiledField Field(CompiledField f)
    {
        var label = this[f.Label];
        var description = f.Def.Description is null ? null : this[f.Def.Description];
        if (label == f.Label && description == f.Def.Description) return f;

        var key = Language + "|" + Tenant;
        var version = catalog.Version;
        var perField = FieldCache.GetOrCreateValue(f);
        lock (perField)
        {
            if (perField.TryGetValue(key, out var hit) && hit.Version == version) return hit.Field;
            var def = JsonSerializer.Deserialize<FieldDef>(JsonSerializer.Serialize(f.Def, MetadataJson.Options), MetadataJson.Options)!;
            def.Label = label;
            def.Description = description;
            var copy = new CompiledField
            {
                Def = def,
                Type = f.Type,
                RequiredWhen = f.RequiredWhen,
                VisibleWhen = f.VisibleWhen,
                ReadOnlyWhen = f.ReadOnlyWhen,
                Compute = f.Compute,
                FetchFrom = f.FetchFrom,
                Pattern = f.Pattern,
                StaticDefault = f.StaticDefault,
                DynamicDefault = f.DynamicDefault,
            };
            perField[key] = (version, copy);
            return copy;
        }
    }

    public IEnumerable<CompiledField> Fields(IEnumerable<CompiledField> fields) => fields.Select(Field);

    /// <summary>Form section with translated title and field labels (field names, rules and options unchanged).</summary>
    public LayoutSection Section(LayoutSection section) =>
        new(section.Title is null ? null : this[section.Title],
            section.Columns.Select(c => new LayoutColumn(c.Fields.Select(Field).ToList())).ToList());
}
