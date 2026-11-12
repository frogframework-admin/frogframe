using System.Reflection;
using System.Text;
using Framework.Scripting.Abstractions;

namespace Framework.Web.Components;

/// <summary>A DocType field offered as a completion inside <c>Doc["..."]</c> and <c>Doc.GetX("...")</c>.</summary>
public sealed record ScriptEditorField(string Name, string Label, string Type);

/// <summary>One completion entry sent to the Monaco editor.</summary>
public sealed record ScriptCompletionItem(
    string Label,
    string Kind,
    string Detail,
    string InsertText,
    bool IsSnippet = false,
    string? Documentation = null);

/// <summary>
/// Completion data for the server script editor. Members of the script globals are read by reflection from
/// <c>Framework.Scripting.Abstractions</c>, so the editor stays in sync with the real script API.
/// Members whose name starts with "__" (wrapper internals such as the execution budget) are never offered.
/// </summary>
public static class ScriptCompletionCatalog
{
    /// <summary>Payload passed to <c>script-editor.js</c>: globals, members per variable/type name, and snippets.</summary>
    public static object ForEditor { get; } = Build();

    private static object Build()
    {
        var members = new Dictionary<string, IReadOnlyList<ScriptCompletionItem>>(StringComparer.Ordinal)
        {
            ["Doc"] = InstanceMembers(typeof(ScriptDoc)),
            ["OldDoc"] = InstanceMembers(typeof(ScriptDoc)),
            ["User"] = InstanceMembers(typeof(ScriptUser)),
            ["Docs"] = InstanceMembers(typeof(IScriptDocs)),
            ["Log"] = InstanceMembers(typeof(IScriptLog)),
            ["Email"] = InstanceMembers(typeof(IScriptEmail)),
            ["ctx"] = InstanceMembers(typeof(ScriptContext)),
            ["Form"] = InstanceMembers(typeof(IScriptForm)),
            ["FormScriptEvent"] = EnumMembers(typeof(FormScriptEvent)),
            ["CancellationToken"] =
            [
                new("IsCancellationRequested", "property", "bool IsCancellationRequested", "IsCancellationRequested"),
                new("ThrowIfCancellationRequested", "method", "void ThrowIfCancellationRequested()", "ThrowIfCancellationRequested()")
            ],
            ["Values"] =
            [
                new("ContainsKey", "method", "bool ContainsKey(string key)", "ContainsKey(\"${1:key}\")", true),
                new("TryGetValue", "method", "bool TryGetValue(string key, out object? value)", "TryGetValue(\"${1:key}\", out var ${2:value})", true),
                new("Keys", "property", "ICollection<string> Keys", "Keys"),
                new("Count", "property", "int Count", "Count")
            ],
            // Static members of allowed BCL types (see ScriptSecurityAnalyzer.AllowedTypes).
            ["Math"] = StaticMembers(typeof(Math)),
            ["Convert"] = StaticMembers(typeof(Convert)),
            ["DateTime"] = StaticMembers(typeof(DateTime)),
            ["DateOnly"] = StaticMembers(typeof(DateOnly)),
            ["TimeSpan"] = StaticMembers(typeof(TimeSpan)),
            ["Guid"] = StaticMembers(typeof(Guid)),
            ["string"] = StaticMembers(typeof(string)),
            ["decimal"] = StaticMembers(typeof(decimal))
        };

        ScriptCompletionItem[] globals =
        [
            new("Doc", "variable", "ScriptDoc Doc", "Doc", Documentation: "Current document (DocEvent scripts)."),
            new("OldDoc", "variable", "ScriptDoc? OldDoc", "OldDoc", Documentation: "Document before this change (DocEvent scripts, null on insert)."),
            new("User", "variable", "ScriptUser User", "User", Documentation: "The user the script runs as."),
            new("Docs", "variable", "IScriptDocs Docs", "Docs", Documentation: "Read and change documents with the script user's permissions."),
            new("Log", "variable", "IScriptLog Log", "Log", Documentation: "Write to the run log (Info, Warn, Error)."),
            new("Email", "variable", "IScriptEmail Email", "Email", Documentation: "Queue an outbound email."),
            new("Values", "variable", "IDictionary<string, object?> Values", "Values", Documentation: "Request arguments (Api scripts)."),
            new("CancellationToken", "variable", "CancellationToken CancellationToken", "CancellationToken", Documentation: "Pass to Docs/Email calls; cancelled when the time limit is reached."),
            new("ctx", "variable", "ScriptContext ctx", "ctx", Documentation: "Script context. Set ctx.Result to return data from an Api script."),
            new("Form", "variable", "IScriptForm Form", "Form", Documentation: "The open form (Client Scripts only; null in other scripts): show/hide fields, fill values, messages."),
            new("FormScriptEvent", "enum", "enum FormScriptEvent", "FormScriptEvent", Documentation: "Load, Change or Validate (compare with Form.Event).")
        ];

        ScriptCompletionItem[] snippets =
        [
            new("validate", "snippet", "throw ScriptValidationException",
                "if (${1:condition})\n{\n\tthrow new ScriptValidationException(\"${2:message}\", \"${3:field}\");\n}", true,
                "Reject the document with a field validation error."),
            new("rows", "snippet", "foreach row in child table",
                "foreach (var ${1:row} in Doc.GetTable(\"${2:items}\"))\n{\n\t$0\n}", true),
            new("getdoc", "snippet", "load a document",
                "var ${1:doc} = await Docs.GetAsync(\"${2:DocType}\", ${3:name}, CancellationToken);", true),
            new("result", "snippet", "return data from an Api script",
                "ctx.Result = new Dictionary<string, object?>\n{\n\t[\"${1:key}\"] = ${2:value}\n};", true),
            new("sendmail", "snippet", "queue an email",
                "await Email.SendAsync(${1:to}, \"${2:subject}\", \"${3:body}\", ct: CancellationToken);", true),
            new("formtoggle", "snippet", "Client Script: show a field only when a condition holds",
                "var ${1:show} = ${2:Form.GetValue(\"priority\") as string == \"High\"};\nForm.SetHidden(\"${3:field}\", !${1:show});\nForm.SetRequired(\"${3:field}\", ${1:show});", true,
                "Hide and require a field depending on another one (Change event)."),
            new("formfill", "snippet", "Client Script: fill a value when empty",
                "if (string.IsNullOrEmpty(Form.GetValue(\"${1:field}\") as string))\n{\n\tForm.SetValue(\"${1:field}\", ${2:value});\n}", true),
            new("formerror", "snippet", "Client Script: block saving with a field error",
                "if (${1:condition})\n{\n\tForm.AddError(\"${2:field}\", \"${3:message}\");\n}", true,
                "Validate event: the form is not saved while errors are reported."),
            new("formmessage", "snippet", "Client Script: show a message",
                "Form.ShowMessage(\"${1:message}\", \"${2|info,success,warning,error|}\");", true)
        ];

        return new { globals, members, snippets };
    }

    private static IReadOnlyList<ScriptCompletionItem> InstanceMembers(Type type)
    {
        var flags = BindingFlags.Public | BindingFlags.Instance;
        var types = type.IsInterface ? type.GetInterfaces().Prepend(type).ToArray() : new[] { type };
        var items = new List<ScriptCompletionItem>();

        foreach (var t in types)
        {
            foreach (var p in t.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                items.Add(new(p.Name, "property", $"{TypeName(p.PropertyType)} {p.Name}", p.Name));
            }

            foreach (var m in t.GetMethods(flags))
            {
                if (m.IsSpecialName || m.DeclaringType == typeof(object) || m.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                if (m.Name is "Equals" or "GetHashCode" or "GetType" or "ToString" or "<Clone>$" or "Deconstruct") continue;
                items.Add(Method(m));
            }
        }

        return Dedupe(items);
    }

    private static IReadOnlyList<ScriptCompletionItem> StaticMembers(Type type)
    {
        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var items = new List<ScriptCompletionItem>();

        foreach (var f in type.GetFields(flags))
            items.Add(new(f.Name, "field", $"{TypeName(f.FieldType)} {f.Name}", f.Name));

        foreach (var p in type.GetProperties(flags))
            items.Add(new(p.Name, "property", $"{TypeName(p.PropertyType)} {p.Name}", p.Name));

        foreach (var m in type.GetMethods(flags))
        {
            if (m.IsSpecialName) continue; // operators and property accessors
            // Skip span/pointer overloads that scripts cannot use.
            if (m.GetParameters().Any(p => p.ParameterType.IsPointer || p.ParameterType.IsByRefLike || p.ParameterType.IsByRef && p.ParameterType.GetElementType()!.IsByRefLike)) continue;
            if (m.ReturnType.IsByRefLike) continue;
            items.Add(Method(m));
        }

        return Dedupe(items);
    }

    private static IReadOnlyList<ScriptCompletionItem> EnumMembers(Type type) =>
        Enum.GetNames(type).Select(n => new ScriptCompletionItem(n, "enumMember", $"{type.Name}.{n}", n)).ToList();

    private static ScriptCompletionItem Method(MethodInfo m)
    {
        var parameters = m.GetParameters();
        var signature = $"{TypeName(m.ReturnType)} {m.Name}({string.Join(", ", parameters.Select(Param))})";

        // Snippet with placeholders for the required parameters only.
        var required = parameters.Where(p => !p.IsOptional && !p.HasDefaultValue).ToList();
        var insert = new StringBuilder(m.Name).Append('(');
        for (var i = 0; i < required.Count; i++)
        {
            if (i > 0) insert.Append(", ");
            insert.Append("${").Append(i + 1).Append(':').Append(required[i].Name).Append('}');
        }
        insert.Append(')');

        return new(m.Name, "method", signature, insert.ToString(), IsSnippet: true);
    }

    /// <summary>One entry per name; overloads are listed in the detail line.</summary>
    private static IReadOnlyList<ScriptCompletionItem> Dedupe(List<ScriptCompletionItem> items) =>
        items
            .GroupBy(i => i.Label, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                var count = g.Count();
                return count == 1 ? first : first with { Detail = $"{first.Detail}  (+{count - 1} overload{(count > 2 ? "s" : "")})" };
            })
            .OrderBy(i => i.Label, StringComparer.Ordinal)
            .ToList();

    private static string Param(ParameterInfo p)
    {
        var prefix = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "";
        var text = $"{prefix}{TypeName(p.ParameterType)} {p.Name}";
        return p.HasDefaultValue ? $"{text} = {DefaultText(p.DefaultValue)}" : text;
    }

    private static string DefaultText(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "default"
    };

    private static string TypeName(Type type)
    {
        if (type.IsByRef) type = type.GetElementType()!;
        if (Nullable.GetUnderlyingType(type) is { } inner) return TypeName(inner) + "?";
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";

        var alias = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => "bool",
            TypeCode.Byte => "byte",
            TypeCode.Char => "char",
            TypeCode.Decimal => "decimal",
            TypeCode.Double => "double",
            TypeCode.Int16 => "short",
            TypeCode.Int32 => "int",
            TypeCode.Int64 => "long",
            TypeCode.SByte => "sbyte",
            TypeCode.Single => "float",
            TypeCode.String => "string",
            TypeCode.UInt16 => "ushort",
            TypeCode.UInt32 => "uint",
            TypeCode.UInt64 => "ulong",
            _ => null
        };
        if (alias is not null && !type.IsEnum) return alias;
        if (type == typeof(void)) return "void";
        if (type == typeof(object)) return "object";

        if (!type.IsGenericType) return type.Name;
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }
}
