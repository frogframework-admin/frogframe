using System.Globalization;
using Framework.Metadata;

namespace Framework.Documents;

/// <summary>
/// Tree view of a list for parent–child masters (chart of accounts, cost centers, organization units). Built from
/// the rows <see cref="DocumentService.ListAsync"/> returned, so search, filters, row scope and field permissions
/// are the list's own. A row whose parent is not in the list becomes a root marked <see cref="Node.ParentMissing"/>;
/// rows caught in a parent cycle (bad data) are still shown once, as roots marked <see cref="Node.InCycle"/>.
/// </summary>
public static class TreeView
{
    public sealed record Settings(string ParentField, string? GroupField);

    public sealed class Node
    {
        public required string Name { get; init; }
        public required string Title { get; init; }
        public required IReadOnlyDictionary<string, object?> Row { get; init; }
        public string? Parent { get; init; }
        public bool IsGroup { get; init; }
        public List<Node> Children { get; } = [];
        public int Depth { get; internal set; }
        /// <summary>Number of nodes below this one (all levels).</summary>
        public int Descendants { get; internal set; }
        /// <summary>The parent is set but not among the rows (filtered out, out of scope or deleted).</summary>
        public bool ParentMissing { get; internal set; }
        /// <summary>The node's parent chain loops back to itself; shown as a root so it stays reachable.</summary>
        public bool InCycle { get; internal set; }
    }

    /// <summary>Configured <c>listView.tree</c>, or a Link to the same DocType (a "parent…" field first) and an is_group Check.</summary>
    public static Settings? Resolve(CompiledDocType meta)
    {
        if (meta.Def.IsSingle || meta.Def.IsChild) return null;
        if (meta.Def.ListView.Tree is { } cfg && meta.ByName.ContainsKey(cfg.ParentField))
            return new Settings(cfg.ParentField, string.IsNullOrWhiteSpace(cfg.GroupField) ? null : cfg.GroupField);
        var selfLinks = meta.Fields.Where(f => !f.IsLayout && f.Def.Type == "Link" && f.Def.LinkTo == meta.Name).ToList();
        var parent = selfLinks.FirstOrDefault(f => f.Name.StartsWith("parent", StringComparison.Ordinal)) ?? (selfLinks.Count == 1 ? selfLinks[0] : null);
        if (parent is null) return null;
        var group = meta.ByName.TryGetValue("is_group", out var g) && g.Def.Type == "Check" ? g.Name : null;
        return new Settings(parent.Name, group);
    }

    public static bool Supports(CompiledDocType meta) => Resolve(meta) is not null;

    /// <summary>Builds the forest in row order (the list's sort order is kept among siblings).</summary>
    public static List<Node> Build(Settings settings, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var nodes = new List<Node>();
        var byName = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.GetValueOrDefault(StandardFields.Name) is not string name || byName.ContainsKey(name)) continue;
            var parent = Convert.ToString(row.GetValueOrDefault(settings.ParentField), CultureInfo.InvariantCulture);
            var title = row.GetValueOrDefault("_title") is { } t && Convert.ToString(t, CultureInfo.InvariantCulture) is { Length: > 0 } ts ? ts : name;
            var node = new Node
            {
                Name = name, Title = title, Row = row,
                Parent = string.IsNullOrEmpty(parent) || parent == name ? null : parent,
                IsGroup = settings.GroupField is { } gf && row.GetValueOrDefault(gf) is true,
            };
            nodes.Add(node);
            byName[name] = node;
        }

        var roots = new List<Node>();
        foreach (var n in nodes)
        {
            if (n.Parent is null) roots.Add(n);
            else if (byName.TryGetValue(n.Parent, out var p)) p.Children.Add(n);
            else { n.ParentMissing = true; roots.Add(n); }
        }

        // Walk from the roots; whatever is left over sits in a parent cycle. Break each cycle at its first node in row order.
        var visited = new HashSet<Node>();
        var result = new List<Node>();
        foreach (var r in roots) { Walk(r, 0, visited); result.Add(r); }
        foreach (var n in nodes.Where(n => !visited.Contains(n)))
        {
            if (visited.Contains(n)) continue;
            n.InCycle = true;
            // Detach from the old parent so the cycle is cut here.
            if (n.Parent is { } p && byName.TryGetValue(p, out var pn)) pn.Children.Remove(n);
            Walk(n, 0, visited);
            result.Add(n);
        }
        return result;
    }

    private static int Walk(Node node, int depth, HashSet<Node> visited)
    {
        visited.Add(node);
        node.Depth = depth;
        var count = 0;
        node.Children.RemoveAll(c => visited.Contains(c)); // a second path into a visited node (cycle) is cut
        foreach (var c in node.Children) count += 1 + Walk(c, depth + 1, visited);
        node.Descendants = count;
        return count;
    }

    /// <summary>Nodes in display order, descending only into expanded nodes.</summary>
    public static IEnumerable<Node> Visible(IEnumerable<Node> roots, IReadOnlySet<string> expanded)
    {
        var stack = new Stack<Node>(roots.Reverse());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n.Children.Count > 0 && expanded.Contains(n.Name))
                for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    /// <summary>All nodes, depth first.</summary>
    public static IEnumerable<Node> All(IEnumerable<Node> roots) => Visible(roots, new AllSet());

    /// <summary>Names of the nodes with children up to <paramref name="levels"/> levels deep (for the initial expansion).</summary>
    public static HashSet<string> ExpandedTo(IEnumerable<Node> roots, int levels) =>
        All(roots).Where(n => n.Depth < levels && n.Children.Count > 0).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Parent of a node in the built forest (null for roots).</summary>
    public static Node? ParentOf(IEnumerable<Node> roots, Node node) =>
        All(roots).FirstOrDefault(n => n.Children.Contains(node));

    private sealed class AllSet : IReadOnlySet<string>
    {
        public bool Contains(string item) => true;
        public int Count => int.MaxValue;
        public IEnumerator<string> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<string> other) => false;
        public bool IsProperSupersetOf(IEnumerable<string> other) => true;
        public bool IsSubsetOf(IEnumerable<string> other) => false;
        public bool IsSupersetOf(IEnumerable<string> other) => true;
        public bool Overlaps(IEnumerable<string> other) => other.Any();
        public bool SetEquals(IEnumerable<string> other) => false;
    }
}
