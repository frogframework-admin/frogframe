using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.Documents;
using Framework.Security;

namespace Framework.Api;

/// <summary>Tipe argumen method: text, number (decimal), date (DateOnly yyyy-MM-dd), boolean, list (daftar teks).</summary>
public static class ArgTypes
{
    public const string Text = "text", Number = "number", Date = "date", Boolean = "boolean", List = "list";
}

public sealed record ApiMethodParam(string Name, string Type, bool Required = false, string? Description = null);

public sealed class ApiMethodContext
{
    public required UserContext User { get; init; }
    public required IReadOnlyDictionary<string, object?> Args { get; init; }
    public required IServiceProvider Services { get; init; }

    public T? Arg<T>(string name) => Args.TryGetValue(name, out var v) && v is T t ? t : default;
}

/// <summary>Kesalahan yang memang untuk pemanggil (400), mis. data tidak cocok. Kesalahan lain tidak dibocorkan.</summary>
public sealed class ApiMethodException(string message) : Exception(message);

/// <summary>
/// Method kustom yang dibuka sebagai API (padanan @frappe.whitelist): POST /api/method/{Name}.
/// Method berjalan sebagai pemanggil — gunakan DocumentService agar permission, cakupan baris, dan validasi tetap berlaku.
/// </summary>
public interface IApiMethod
{
    /// <summary>Huruf kecil, angka, tanda hubung, mis. "sisa-anggaran".</summary>
    string Name { get; }
    string Description { get; }
    /// <summary>Role yang boleh memanggil. Kosong = semua pengguna yang terautentikasi.</summary>
    IReadOnlyList<string> Roles { get; }
    IReadOnlyList<ApiMethodParam> Parameters { get; }
    Task<object?> InvokeAsync(ApiMethodContext ctx, CancellationToken ct);
}

public sealed partial class ApiMethodRegistry
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")] private static partial Regex NameRx();
    private readonly Dictionary<string, IApiMethod> _methods;

    public ApiMethodRegistry(IEnumerable<IApiMethod> methods)
    {
        _methods = new(StringComparer.Ordinal);
        foreach (var m in methods)
        {
            if (!NameRx().IsMatch(m.Name)) throw new InvalidOperationException($"API method name '{m.Name}' is invalid (lowercase, numbers, '-').");
            if (!_methods.TryAdd(m.Name, m)) throw new InvalidOperationException($"API method '{m.Name}' is registered more than once.");
        }
    }

    public static bool CanCall(UserContext u, IApiMethod m) =>
        m.Roles.Count == 0 || u.Roles.Contains(UserContext.SystemManager) || m.Roles.Any(u.Roles.Contains);

    public IEnumerable<IApiMethod> Visible(UserContext u) => _methods.Values.Where(m => CanCall(u, m)).OrderBy(m => m.Name);
    public IApiMethod? Find(string name) => _methods.GetValueOrDefault(name);

    public async Task<object?> InvokeAsync(UserContext user, string name, JsonElement? body, IServiceProvider services, CancellationToken ct = default)
    {
        var m = Find(name) ?? throw new KeyNotFoundException($"API method '{name}' not found.");
        if (!CanCall(user, m)) throw new PermissionDeniedException($"You are not allowed to call method '{name}'.");
        var args = Bind(m, body);
        return await m.InvokeAsync(new ApiMethodContext { User = user, Args = args, Services = services }, ct);
    }

    /// <summary>Argumen divalidasi ketat: hanya parameter yang dideklarasikan (argumen asing ditolak), wajib, dan tipe.</summary>
    public static Dictionary<string, object?> Bind(IApiMethod m, JsonElement? body)
    {
        var errors = new List<FieldError>();
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (body is { } b && b.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
            throw new ValidationFailedException([new FieldError("", "Body must be a JSON object containing arguments.")]);
        var given = body is { ValueKind: JsonValueKind.Object } o ? o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value) : [];
        foreach (var extra in given.Keys.Where(k => m.Parameters.All(p => p.Name != k)))
            errors.Add(new FieldError(extra, "unknown argument"));
        foreach (var p in m.Parameters)
        {
            if (!given.TryGetValue(p.Name, out var v) || v.ValueKind is JsonValueKind.Null || v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length == 0)
            {
                if (p.Required) errors.Add(new FieldError(p.Name, "is required"));
                else args[p.Name] = null;
                continue;
            }
            try { args[p.Name] = Convert(p, v); }
            catch (FormatException ex) { errors.Add(new FieldError(p.Name, ex.Message)); }
        }
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        return args;
    }

    private static object Convert(ApiMethodParam p, JsonElement v) => p.Type switch
    {
        ArgTypes.Number => v.ValueKind == JsonValueKind.Number ? v.GetDecimal()
            : v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d
            : throw new FormatException("must be a number"),
        ArgTypes.Boolean => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new FormatException("must be true or false"),
        ArgTypes.Date => v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt
            : throw new FormatException("must be date yyyy-MM-dd"),
        ArgTypes.List => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
            ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : throw new FormatException("must be a list of strings"),
        _ => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new FormatException("must be a string"),
    };
}
