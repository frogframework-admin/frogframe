using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Framework.Web.AI;

/// <summary>
/// A single indexed knowledge section extracted from bundled Markdown guides or compiled C# XML documentation.
/// </summary>
public sealed record AiKnowledgeChunk(
    string Source,
    string Section,
    string Kind,
    string Content);

/// <summary>
/// Ranked search hit returned by <see cref="AiKnowledgeIndex.Search"/>.
/// </summary>
public sealed record AiKnowledgeHit(
    string Source,
    string Section,
    string Kind,
    string Content,
    double Score);

/// <summary>
/// In-memory runtime knowledge index that survives compilation (<c>dotnet publish</c>).
/// Indexes bundled Markdown documentation (<c>KnowledgeBase/**/*.md</c>) and compiled assembly XML documentation
/// (<c>Apps.*.xml</c>, <c>Framework.*.xml</c>) so the AI Assistant can answer questions about ERP guides,
/// test scenarios, and C# hook/report business rules without needing raw <c>.cs</c> source files at runtime.
/// </summary>
public sealed partial class AiKnowledgeIndex
{
    private const int MaxChunkChars = 2200;
    private readonly List<IndexedChunk> _chunks = [];

    private sealed record IndexedChunk(
        AiKnowledgeChunk Chunk,
        string LowerSource,
        string LowerSection,
        string LowerContent,
        HashSet<string> HeaderTokens,
        Dictionary<string, int> ContentTokenCounts);

    public IReadOnlyList<AiKnowledgeChunk> Chunks => _chunks.Select(c => c.Chunk).ToList();

    /// <summary>
    /// Creates and populates the knowledge index from the application base directory and optional additional directories.
    /// </summary>
    public AiKnowledgeIndex(string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? AppContext.BaseDirectory;
        LoadMarkdownDirectory(ResolveMarkdownDirectory(baseDir));
        LoadXmlDocumentation(baseDir);
    }

    /// <summary>
    /// Searches the indexed knowledge base for chunks relevant to the specified query.
    /// Supports multi-word queries, camelCase identifiers (e.g. <c>AccountingPeriod</c>), and exact phrases.
    /// </summary>
    public IReadOnlyList<AiKnowledgeHit> Search(string? query, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return [];

        var trimmed = query.Trim();
        var lowerQuery = trimmed.ToLowerInvariant();
        var tokens = Tokenize(trimmed).Distinct(StringComparer.Ordinal).ToList();
        if (tokens.Count == 0)
            return [];

        var results = new List<AiKnowledgeHit>();

        foreach (var item in _chunks)
        {
            double score = 0;

            // Exact substring bonus on title/source and content
            if (item.LowerSection.Contains(lowerQuery, StringComparison.Ordinal) ||
                item.LowerSource.Contains(lowerQuery, StringComparison.Ordinal))
            {
                score += 20;
            }

            if (lowerQuery.Length >= 3 && item.LowerContent.Contains(lowerQuery, StringComparison.Ordinal))
            {
                score += 10;
            }

            int matchedTokens = 0;
            foreach (var token in tokens)
            {
                bool matchedThisToken = false;

                if (item.HeaderTokens.Contains(token) ||
                    item.LowerSection.Contains(token, StringComparison.Ordinal) ||
                    item.LowerSource.Contains(token, StringComparison.Ordinal))
                {
                    score += 8;
                    matchedThisToken = true;
                }

                if (item.ContentTokenCounts.TryGetValue(token, out var count))
                {
                    score += 2 + Math.Min(count, 5);
                    matchedThisToken = true;
                }
                else if (token.Length >= 4 && item.LowerContent.Contains(token, StringComparison.Ordinal))
                {
                    score += 1.5;
                    matchedThisToken = true;
                }

                if (matchedThisToken)
                    matchedTokens++;
            }

            // Coverage bonus when multiple distinct query tokens match the same chunk
            if (tokens.Count > 1 && matchedTokens > 1)
            {
                score *= 1.0 + (0.25 * matchedTokens / tokens.Count);
            }

            if (score > 0)
            {
                results.Add(new AiKnowledgeHit(
                    item.Chunk.Source,
                    item.Chunk.Section,
                    item.Chunk.Kind,
                    item.Chunk.Content,
                    Math.Round(score, 2)));
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Source, StringComparer.Ordinal)
            .ThenBy(r => r.Section, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();
    }

    private static string? ResolveMarkdownDirectory(string baseDir)
    {
        var bundled = Path.Combine(baseDir, "KnowledgeBase");
        if (Directory.Exists(bundled))
            return bundled;

        // Fallback when running from source or test runner directory
        var current = new DirectoryInfo(baseDir);
        while (current is not null)
        {
            var docsCandidate = Path.Combine(current.FullName, "docs");
            if (Directory.Exists(docsCandidate))
                return docsCandidate;
            current = current.Parent;
        }

        return null;
    }

    private void LoadMarkdownDirectory(string? rootDir)
    {
        if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
            return;

        var files = Directory.GetFiles(rootDir, "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                var relativePath = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
                var text = File.ReadAllText(file, Encoding.UTF8);
                foreach (var (section, content) in SplitMarkdownSections(relativePath, text))
                {
                    AddChunk(new AiKnowledgeChunk(relativePath, section, "Guide", content));
                }
            }
            catch
            {
                // Ignore unreadable files at startup so the application never fails to boot.
            }
        }
    }

    private static IEnumerable<(string Section, string Content)> SplitMarkdownSections(string fileName, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        string h1 = Path.GetFileNameWithoutExtension(fileName);
        string h2 = "";
        string h3 = "";
        var buffer = new StringBuilder();

        string CurrentHeading()
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(h1)) parts.Add(h1);
            if (!string.IsNullOrWhiteSpace(h2)) parts.Add(h2);
            if (!string.IsNullOrWhiteSpace(h3)) parts.Add(h3);
            return string.Join(" › ", parts);
        }

        IEnumerable<(string Section, string Content)> Flush()
        {
            var body = buffer.ToString().Trim();
            buffer.Clear();
            if (string.IsNullOrWhiteSpace(body))
                yield break;

            var section = CurrentHeading();
            if (body.Length <= MaxChunkChars)
            {
                yield return (section, body);
            }
            else
            {
                for (int i = 0; i < body.Length; i += MaxChunkChars)
                {
                    var slice = body.Substring(i, Math.Min(MaxChunkChars, body.Length - i)).Trim();
                    if (!string.IsNullOrWhiteSpace(slice))
                        yield return (section, slice);
                }
            }
        }

        bool inCodeFence = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                buffer.AppendLine(line);
                continue;
            }

            if (!inCodeFence && trimmed.StartsWith('#'))
            {
                if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                {
                    foreach (var chunk in Flush()) yield return chunk;
                    h1 = trimmed[2..].Trim();
                    h2 = "";
                    h3 = "";
                    continue;
                }
                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    foreach (var chunk in Flush()) yield return chunk;
                    h2 = trimmed[3..].Trim();
                    h3 = "";
                    continue;
                }
                if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                {
                    foreach (var chunk in Flush()) yield return chunk;
                    h3 = trimmed[4..].Trim();
                    continue;
                }
            }

            buffer.AppendLine(line);
        }

        foreach (var chunk in Flush())
            yield return chunk;
    }

    private void LoadXmlDocumentation(string baseDir)
    {
        if (!Directory.Exists(baseDir))
            return;

        var xmlFiles = Directory.GetFiles(baseDir, "*.xml", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.StartsWith("Apps.", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("Framework.", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var xmlFile in xmlFiles)
        {
            try
            {
                var doc = XDocument.Load(xmlFile);
                var assemblyName = doc.Root?.Element("assembly")?.Element("name")?.Value
                    ?? Path.GetFileNameWithoutExtension(xmlFile);

                var members = doc.Root?.Element("members")?.Elements("member");
                if (members is null) continue;

                // Group member summaries by enclosing class/type so related rules stay together
                var byType = new Dictionary<string, List<(string MemberName, string Summary)>>(StringComparer.Ordinal);

                foreach (var member in members)
                {
                    var rawName = member.Attribute("name")?.Value;
                    if (string.IsNullOrWhiteSpace(rawName) || rawName.Length < 3)
                        continue;

                    var summaryText = NormalizeXmlText(member.Element("summary"));
                    if (string.IsNullOrWhiteSpace(summaryText))
                        continue;

                    var kindPrefix = rawName[0];
                    var fullName = rawName[2..];
                    var parenIdx = fullName.IndexOf('(');
                    if (parenIdx >= 0)
                        fullName = fullName[..parenIdx];

                    string typeName;
                    string memberLabel;
                    if (kindPrefix == 'T')
                    {
                        typeName = fullName;
                        memberLabel = ShortTypeName(fullName);
                    }
                    else
                    {
                        var lastDot = fullName.LastIndexOf('.');
                        typeName = lastDot > 0 ? fullName[..lastDot] : fullName;
                        var mName = lastDot > 0 ? fullName[(lastDot + 1)..] : fullName;
                        memberLabel = $"{ShortTypeName(typeName)}.{mName}";
                    }

                    if (!byType.TryGetValue(typeName, out var list))
                    {
                        list = [];
                        byType[typeName] = list;
                    }
                    list.Add((memberLabel, summaryText));
                }

                foreach (var (typeName, entries) in byType.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var shortType = ShortTypeName(typeName);
                    var sb = new StringBuilder();
                    foreach (var (memberLabel, summary) in entries)
                    {
                        sb.AppendLine($"- {memberLabel}: {summary}");
                    }

                    var content = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        AddChunk(new AiKnowledgeChunk(
                            Source: $"{assemblyName}.xml",
                            Section: $"{shortType} ({typeName})",
                            Kind: "CodeSummary",
                            Content: content.Length <= MaxChunkChars ? content : content[..MaxChunkChars]));
                    }
                }
            }
            catch
            {
                // Ignore malformed XML files at startup.
            }
        }
    }

    private static string ShortTypeName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot >= 0 ? fullTypeName[(lastDot + 1)..] : fullTypeName;
    }

    private static string NormalizeXmlText(XElement? element)
    {
        if (element is null) return "";
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                sb.Append(text.Value);
            }
            else if (node is XElement el)
            {
                var cref = el.Attribute("cref")?.Value;
                if (!string.IsNullOrWhiteSpace(cref))
                {
                    var clean = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
                    sb.Append(ShortTypeName(clean));
                }
                else
                {
                    sb.Append(el.Value);
                }
            }
        }
        return WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    private void AddChunk(AiKnowledgeChunk chunk)
    {
        var lowerSource = chunk.Source.ToLowerInvariant();
        var lowerSection = chunk.Section.ToLowerInvariant();
        var lowerContent = chunk.Content.ToLowerInvariant();

        var headerTokens = new HashSet<string>(
            Tokenize(chunk.Source).Concat(Tokenize(chunk.Section)),
            StringComparer.Ordinal);

        var contentTokenCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tok in Tokenize(chunk.Content))
        {
            contentTokenCounts[tok] = contentTokenCounts.GetValueOrDefault(tok) + 1;
        }

        _chunks.Add(new IndexedChunk(chunk, lowerSource, lowerSection, lowerContent, headerTokens, contentTokenCounts));
    }

    /// <summary>
    /// Tokenizes text into lowercase terms and also splits camelCase / PascalCase identifiers
    /// (e.g., <c>AccountingPeriod</c> produces <c>accountingperiod</c>, <c>accounting</c>, and <c>period</c>).
    /// </summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match match in WordRegex().Matches(text))
        {
            var raw = match.Value;
            if (raw.Length < 2) continue;

            var lower = raw.ToLowerInvariant();
            if (!StopWords.Contains(lower))
                yield return lower;

            // Split PascalCase / camelCase parts
            var parts = CamelCaseSplitRegex().Split(raw);
            if (parts.Length > 1)
            {
                foreach (var part in parts)
                {
                    if (part.Length < 2) continue;
                    var pLower = part.ToLowerInvariant();
                    if (!StopWords.Contains(pLower) && pLower != lower)
                        yield return pLower;
                }
            }
        }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "that", "this", "are", "was", "not", "can", "will", "into",
        "yang", "dan", "di", "ke", "dari", "ini", "itu", "untuk", "pada", "dengan", "atau", "adalah", "bisa", "ada", "apa", "bagaimana", "cara"
    };

    [GeneratedRegex(@"[A-Za-z0-9_]{2,}")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?<!^)(?=[A-Z][a-z])|(?<=[a-z])(?=[A-Z])|_")]
    private static partial Regex CamelCaseSplitRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
