using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Framework.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Localization;

// ================================================================================================================
//  Translations (Frappe style: the source text is the key).
//
//  UI text and metadata labels are written in one language (the "source") and translated when they are shown:
//
//      Translate(tenant, language, "Save draft")  →  "Simpan draf"         (framework dictionary, id)
//      Translate(tenant, language, "Pemohon")     →  "Requester"           (application dictionary, en)
//      Message(tenant, "en", "Prioritas wajib diisi") → "Priority is required"   (template "{0} wajib diisi")
//
//  Sources, lowest to highest priority:
//    1. dictionaries shipped with the framework and the application (*.translations.json),
//    2. translations entered by administrators per tenant (Translation admin page, ITranslationStore).
//  A text without a translation is shown as written. Stored values (e.g. Select options) are never translated.
// ================================================================================================================

public static class Languages
{
    public const string Indonesian = "id";
    public const string English = "en";

    /// <summary>Languages offered in the language switcher (code, native name).</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Supported =
    [
        (Indonesian, "Bahasa Indonesia"),
        (English, "English"),
    ];

    public static bool IsSupported(string? code) => code is not null && Supported.Any(l => l.Code == code);

    /// <summary>"en-US", "EN", "id-ID" → "en"/"id"; unsupported → null.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var two = code.Trim().Split('-', '_')[0].ToLowerInvariant();
        return IsSupported(two) ? two : null;
    }

    /// <summary>UI culture used for a language (number and date formats stay Indonesian, see UseFrogFrameLocalization).</summary>
    public static string UiCultureName(string language) => language == English ? "en-US" : "id-ID";

    public static string FromCulture(CultureInfo culture) => Normalize(culture.Name) ?? English;
}

/// <summary>Content of a <c>*.translations.json</c> file: <c>{ "language": "en", "translations": { "Pemohon": "Requester" } }</c>.</summary>
public sealed class TranslationFile
{
    public string Language { get; set; } = "";
    public Dictionary<string, string> Translations { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] public string? Origin { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static TranslationFile Parse(string json, string? origin = null)
    {
        var file = JsonSerializer.Deserialize<TranslationFile>(json, Json) ?? throw new JsonException("Translation file is empty.");
        file.Language = Languages.Normalize(file.Language) ?? throw new JsonException($"Unsupported language '{file.Language}' in {origin ?? "translation file"}.");
        file.Translations = new Dictionary<string, string>(file.Translations, StringComparer.Ordinal);
        file.Origin = origin;
        return file;
    }

    public string ToJson() => JsonSerializer.Serialize(new { language = Language, translations = Translations.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary() },
        new JsonSerializerOptions(Json) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>A translation entered by an administrator for one tenant. Overrides the shipped dictionaries.</summary>
public sealed class TranslationOverride
{
    public string TenantId { get; set; } = "";
    public string Language { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string ModifiedBy { get; set; } = "";
    public DateTimeOffset Modified { get; set; }
}

public interface ITranslationStore
{
    Task<IReadOnlyList<TranslationOverride>> ListAsync(string tenantId, CancellationToken ct = default);
    /// <summary>Insert or replace (tenant, language, source).</summary>
    Task SaveAsync(TranslationOverride translation, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string language, string source, CancellationToken ct = default);
}

public sealed class InMemoryTranslationStore : ITranslationStore
{
    private readonly ConcurrentDictionary<(string Tenant, string Lang, string Source), TranslationOverride> _items = new();

    public Task<IReadOnlyList<TranslationOverride>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TranslationOverride>>(_items.Values.Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.Language).ThenBy(t => t.Source, StringComparer.Ordinal).ToList());

    public Task SaveAsync(TranslationOverride translation, CancellationToken ct = default)
    {
        _items[(translation.TenantId, translation.Language, translation.Source)] = translation;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string language, string source, CancellationToken ct = default)
    {
        _items.TryRemove((tenantId, language, source), out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Looks up translations. Thread-safe singleton; tenant translations are loaded once per tenant and reloaded after
/// <see cref="SaveAsync"/>/<see cref="DeleteAsync"/> (other app instances pick them up after <see cref="TenantCacheTtl"/>).
/// </summary>
public sealed class TranslationCatalog
{
    public const int MaxTextLength = 2000;
    public static readonly TimeSpan TenantCacheTtl = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, Dictionary<string, string>> _shipped = new(StringComparer.Ordinal);
    private readonly ITranslationStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, TenantTranslations> _tenants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<Template>> _shippedTemplates = new(StringComparer.Ordinal);
    private int _version;

    public TranslationCatalog(IEnumerable<TranslationFile> files, ITranslationStore store, ILogger<TranslationCatalog>? logger = null, TimeProvider? clock = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<TranslationCatalog>.Instance;
        _clock = clock ?? TimeProvider.System;
        // Later files win: the framework registers its dictionaries first, the application after it.
        foreach (var file in files)
        {
            if (!_shipped.TryGetValue(file.Language, out var dict)) _shipped[file.Language] = dict = new(StringComparer.Ordinal);
            foreach (var (source, target) in file.Translations)
                if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(target)) dict[source] = target;
        }
    }

    /// <summary>Changes whenever tenant translations change; UI caches key on it.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Shipped dictionary of one language (read only).</summary>
    public IReadOnlyDictionary<string, string> Shipped(string language) =>
        _shipped.TryGetValue(language, out var d) ? d : new Dictionary<string, string>();

    /// <summary>Translation of a whole text (label, title, button). No translation → the text itself.</summary>
    public string Text(string? tenantId, string language, string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        return TryExact(tenantId, language, source, out var target) ? target : source;
    }

    /// <summary>
    /// Like <see cref="Text"/>, and also matches templates such as <c>"{0} wajib diisi"</c> → <c>"{0} is required"</c>,
    /// translating the inserted parts (usually field labels). Used for messages composed on the server.
    /// </summary>
    public string Message(string? tenantId, string language, string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        if (TryExact(tenantId, language, source, out var target)) return target;
        if (source.Length > MaxTextLength) return source;
        foreach (var t in Templates(tenantId, language))
        {
            var m = t.Pattern.Match(source);
            if (!m.Success) continue;
            var args = new object?[t.Slots];
            for (var i = 0; i < t.Slots; i++) args[i] = Text(tenantId, language, m.Groups["p" + i].Value);
            try { return string.Format(CultureInfo.InvariantCulture, t.Target, args); }
            catch (FormatException) { return source; }
        }
        return source;
    }

    public bool TryExact(string? tenantId, string language, string source, out string target)
    {
        if (tenantId is not null && Tenant(tenantId).Get(language, source) is { } custom) { target = custom; return true; }
        if (_shipped.TryGetValue(language, out var dict) && dict.TryGetValue(source, out var shipped)) { target = shipped; return true; }
        target = source;
        return false;
    }

    // ------------------------------------------------------------------------------------------------ tenant data

    public async Task<IReadOnlyList<TranslationOverride>> ListOverridesAsync(string tenantId, CancellationToken ct = default) =>
        await _store.ListAsync(tenantId, ct);

    public async Task SaveAsync(string tenantId, string language, string source, string target, string userId, CancellationToken ct = default)
    {
        language = Languages.Normalize(language) ?? throw new ArgumentException($"Unsupported language '{language}'.", nameof(language));
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Source text is required.", nameof(source));
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Translation is required.", nameof(target));
        if (source.Length > MaxTextLength || target.Length > MaxTextLength)
            throw new ArgumentException($"Texts are limited to {MaxTextLength} characters.");
        if (Placeholders(source) != Placeholders(target))
            throw new ArgumentException("The translation must use the same placeholders ({0}, {1}, …) as the source text.");
        await _store.SaveAsync(new TranslationOverride
        {
            TenantId = tenantId, Language = language, Source = source, Target = target.Trim(),
            ModifiedBy = userId, Modified = _clock.GetUtcNow(),
        }, ct);
        Invalidate(tenantId);
    }

    public async Task DeleteAsync(string tenantId, string language, string source, CancellationToken ct = default)
    {
        await _store.DeleteAsync(tenantId, language, source, ct);
        Invalidate(tenantId);
    }

    public void Invalidate(string tenantId)
    {
        _tenants.TryRemove(tenantId, out _);
        Interlocked.Increment(ref _version);
    }

    private TenantTranslations Tenant(string tenantId)
    {
        var now = _clock.GetUtcNow();
        if (_tenants.TryGetValue(tenantId, out var cached) && now - cached.LoadedAt < TenantCacheTtl) return cached;
        TenantTranslations loaded;
        try
        {
            // Translation runs inside synchronous rendering. Task.Run avoids blocking on a captured sync context.
            var list = Task.Run(() => _store.ListAsync(tenantId)).GetAwaiter().GetResult();
            loaded = new TenantTranslations(now, list);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load translations of tenant {Tenant}; shipped translations are used.", tenantId);
            loaded = new TenantTranslations(now, []);
        }
        Interlocked.Increment(ref _version); // UI caches (translated field copies) refresh
        _tenants[tenantId] = loaded;
        return loaded;
    }

    // ------------------------------------------------------------------------------------------------ templates

    private sealed record Template(Regex Pattern, string Target, int Slots);

    private IEnumerable<Template> Templates(string? tenantId, string language)
    {
        if (tenantId is not null)
            foreach (var t in Tenant(tenantId).Templates(language)) yield return t;
        foreach (var t in _shippedTemplates.GetOrAdd(language, l => BuildTemplates(Shipped(l)))) yield return t;
    }

    private static IReadOnlyList<Template> BuildTemplates(IEnumerable<KeyValuePair<string, string>> entries) =>
        entries
            .Where(e => Placeholders(e.Key) > 0)
            .OrderByDescending(e => e.Key.Length) // most specific first
            .Select(e => ToTemplate(e.Key, e.Value))
            .OfType<Template>()
            .ToList();

    private static Template? ToTemplate(string source, string target)
    {
        var slots = Placeholders(source);
        var parts = PlaceholderRx.Split(source);
        var pattern = "^";
        foreach (var part in parts)
        {
            if (PlaceholderRx.IsMatch(part))
            {
                var n = int.Parse(part.Trim('{', '}'), CultureInfo.InvariantCulture);
                if (n >= slots) return null;
                pattern += $"(?<p{n}>.+?)";
            }
            else pattern += Regex.Escape(part);
        }
        try { return new Template(new Regex(pattern + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), target, slots); }
        catch (ArgumentException) { return null; }
    }

    private static readonly Regex PlaceholderRx = new(@"(\{\d\})", RegexOptions.Compiled);

    /// <summary>Number of distinct placeholders {0}…{9}; also used to check a translation keeps them.</summary>
    public static int Placeholders(string text) =>
        PlaceholderRx.Matches(text).Select(m => m.Value).Distinct().Count();

    private sealed class TenantTranslations(DateTimeOffset loadedAt, IReadOnlyList<TranslationOverride> items)
    {
        private readonly Dictionary<(string, string), string> _byKey = items
            .GroupBy(i => (i.Language, i.Source)).ToDictionary(g => g.Key, g => g.Last().Target);
        private readonly ConcurrentDictionary<string, IReadOnlyList<Template>> _templates = new(StringComparer.Ordinal);

        public DateTimeOffset LoadedAt { get; } = loadedAt;
        public int Count => _byKey.Count;
        public string? Get(string language, string source) => _byKey.GetValueOrDefault((language, source));
        public IReadOnlyList<Template> Templates(string language) =>
            _templates.GetOrAdd(language, l => BuildTemplates(items.Where(i => i.Language == l).Select(i => KeyValuePair.Create(i.Source, i.Target))));
    }
}

/// <summary>Collects the texts of metadata that are shown to users (for the translation admin page).</summary>
public static class MetadataTexts
{
    public sealed record Item(string Text, string Where);

    public static IReadOnlyList<Item> Collect(MetadataSnapshot snapshot)
    {
        var items = new List<Item>();
        void Add(string? text, string where)
        {
            if (!string.IsNullOrWhiteSpace(text)) items.Add(new Item(text.Trim(), where));
        }

        foreach (var meta in snapshot.DocTypes.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var d = meta.Def;
            Add(meta.Title, $"{d.Name} · title");
            Add(d.Description, $"{d.Name} · description");
            foreach (var f in d.Fields)
            {
                Add(string.IsNullOrEmpty(f.Label) ? null : f.Label, $"{d.Name}.{f.Name} · label");
                Add(f.Description, $"{d.Name}.{f.Name} · description");
                Add(f.Section, $"{d.Name} · section");
                Add(f.Tab, $"{d.Name} · tab");
                foreach (var o in f.Options ?? []) Add(o, $"{d.Name}.{f.Name} · option");
            }
        }
        foreach (var wf in snapshot.Workflows.Values.OrderBy(w => w.Def.Name, StringComparer.Ordinal))
        {
            foreach (var s in wf.Def.States) Add(s.Name, $"{wf.Def.Name} · state");
            foreach (var t in wf.Def.Transitions) Add(t.Action, $"{wf.Def.Name} · action");
        }

        return items
            .GroupBy(i => i.Text, StringComparer.Ordinal)
            .Select(g => new Item(g.Key, string.Join(", ", g.Select(i => i.Where).Distinct().Take(4)) + (g.Count() > 4 ? ", …" : "")))
            .OrderBy(i => i.Text, StringComparer.Ordinal)
            .ToList();
    }
}
