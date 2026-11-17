using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Framework.Web.AI;

/// <summary>
/// One validation message, warning, or failed-action message extracted from application hook code by
/// <c>tools/generate_ai_rules.py</c> (file <c>rules/error-catalog.json</c>).
/// </summary>
public sealed record AiErrorCatalogEntry
{
    public string Id { get; init; } = "";
    /// <summary><c>Error</c> (document rejected), <c>Warning</c> (saved with a message), or <c>ActionFailure</c> (form button failed).</summary>
    public string Kind { get; init; } = "Error";
    /// <summary>Message as written in code; interpolations are shown as <c>{expression}</c>.</summary>
    public string Message { get; init; } = "";
    /// <summary>Regular expression matching the message as users see it (interpolations become wildcards).</summary>
    public string Pattern { get; init; } = "";
    public string? Field { get; init; }
    /// <summary>The C# <c>if</c> condition that raises the message, when it could be extracted.</summary>
    public string? Condition { get; init; }
    public IReadOnlyList<string> DocTypes { get; init; } = [];
    public string? Event { get; init; }
    public string ClassName { get; init; } = "";
    public string? Method { get; init; }
    public string File { get; init; } = "";
    public int Line { get; init; }
    /// <summary>True when the rule lives in a shared helper (periods, stock, invoices) used by several DocTypes.</summary>
    public bool Shared { get; init; }
}

/// <summary>Catalog match returned by <see cref="AiErrorCatalog.Match"/>.</summary>
public sealed record AiErrorMatch(AiErrorCatalogEntry Entry, double Score, string MatchType);

/// <summary>
/// Looks up the code rule behind a message a user saw ("Only 1 of ITM-LAP-001 available in Gudang Utama", "Period 2026-01 is closed").
/// Loaded once from <c>KnowledgeBase/rules/error-catalog.json</c> (published output) or <c>docs/rules/error-catalog.json</c>
/// (running from source / tests). A missing or unreadable file yields an empty catalog; the application still starts.
/// </summary>
public sealed class AiErrorCatalog
{
    private const string FileName = "error-catalog.json";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);
    private readonly List<(AiErrorCatalogEntry Entry, Regex? Regex, HashSet<string> Tokens)> _entries = [];

    public AiErrorCatalog(string? baseDirectory = null)
    {
        var path = ResolveCatalogPath(baseDirectory ?? AppContext.BaseDirectory);
        if (path is null) return;
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var file = JsonSerializer.Deserialize<CatalogFile>(stream, ReadOptions);
            foreach (var entry in file?.Entries ?? [])
            {
                Regex? regex = null;
                try { regex = new Regex(entry.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
                catch (ArgumentException) { /* unusable pattern: token matching still works */ }
                _entries.Add((entry, regex, LiteralTokens(entry.Message)));
            }
        }
        catch
        {
            // Ignore a malformed catalog so the application never fails to boot.
        }
    }

    public int Count => _entries.Count;

    public IReadOnlyList<AiErrorCatalogEntry> Entries => _entries.Select(e => e.Entry).ToList();

    /// <summary>
    /// Finds the catalog entries that best explain <paramref name="message"/>. A full pattern match ranks first; otherwise entries are
    /// ranked by how many of the message's fixed words they share. <paramref name="docType"/>, when given, ranks that DocType's rules higher.
    /// </summary>
    public IReadOnlyList<AiErrorMatch> Match(string? message, string? docType = null, int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(message) || maxResults <= 0 || _entries.Count == 0)
            return [];
        var text = Regex.Replace(message.Trim(), @"\s+", " ");
        if (text.Length > 2000) text = text[..2000];
        var queryTokens = AiKnowledgeIndex.Tokenize(text).ToHashSet(StringComparer.Ordinal);

        var results = new List<AiErrorMatch>();
        foreach (var (entry, regex, tokens) in _entries)
        {
            double score = 0;
            var matchType = "";
            if (regex is not null)
            {
                try
                {
                    if (regex.IsMatch(text))
                    {
                        // Longer fixed text = more specific rule.
                        score = 100 + Math.Min(tokens.Count, 20);
                        matchType = "pattern";
                    }
                }
                catch (RegexMatchTimeoutException) { }
            }
            if (score == 0 && tokens.Count > 0 && queryTokens.Count > 0)
            {
                var shared = tokens.Count(queryTokens.Contains);
                var coverage = (double)shared / tokens.Count;
                if (shared >= 2 && coverage >= 0.5)
                {
                    score = 50 * coverage + shared;
                    matchType = "similar";
                }
            }
            if (score == 0) continue;
            if (!string.IsNullOrWhiteSpace(docType) && entry.DocTypes.Contains(docType, StringComparer.OrdinalIgnoreCase))
                score += 20;
            results.Add(new AiErrorMatch(entry, Math.Round(score, 2), matchType));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Entry.Shared)
            .ThenBy(r => r.Entry.Id, StringComparer.Ordinal)
            .GroupBy(r => (r.Entry.Message, r.Entry.ClassName))
            .Select(g => g.First())
            .Take(maxResults)
            .ToList();
    }

    private static HashSet<string> LiteralTokens(string message) =>
        AiKnowledgeIndex.Tokenize(Regex.Replace(message, @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", " ")).ToHashSet(StringComparer.Ordinal);

    private static string? ResolveCatalogPath(string baseDir)
    {
        var bundled = Path.Combine(baseDir, "KnowledgeBase", "rules", FileName);
        if (System.IO.File.Exists(bundled))
            return bundled;
        // Running from source or the test runner: walk up to the repository's docs/rules folder.
        var current = new DirectoryInfo(baseDir);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "docs", "rules", FileName);
            if (System.IO.File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        return null;
    }

    private sealed class CatalogFile
    {
        [JsonPropertyName("entries")]
        public List<AiErrorCatalogEntry>? Entries { get; set; }
    }
}
