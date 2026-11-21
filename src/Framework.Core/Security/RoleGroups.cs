using System.Collections.Frozen;

namespace Framework.Security;

/// <summary>
/// Role groups: a broad role name that stands for a set of granular roles (e.g. "Finance" = Finance-Receivable,
/// Finance-Payable, …). Applications register their groups at startup. Effective roles are computed once, when a user's
/// identity is built (claims, user directory, automation):
/// <list type="bullet">
/// <item>holding a group name directly grants all of its member roles (old assignments keep working after a split);</item>
/// <item>holding any member role also counts as holding the group name, so menus, dashboards, notification rules and
/// shares that name the group keep reaching the right people.</item>
/// </list>
/// Expansion is one step in each direction and never chains, so a member role never grants its sibling roles.
/// DocType and workflow permissions should name member roles only; a group name there would give every member the
/// whole group's rights. See docs/guides/roles.md.
/// </summary>
public static class RoleGroups
{
    private static readonly object Gate = new();
    private static FrozenDictionary<string, string[]> _members = FrozenDictionary<string, string[]>.Empty;
    private static FrozenDictionary<string, string[]> _groupsOf = FrozenDictionary<string, string[]>.Empty;

    /// <summary>Registers (or replaces) a group. Idempotent; safe to call from several app instances in one process.</summary>
    public static void Register(string group, IEnumerable<string> members)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var list = members.Where(m => !string.IsNullOrWhiteSpace(m) && !string.Equals(m, group, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (Gate)
        {
            var all = _members.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            all[group] = list;
            _members = all.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _groupsOf = all.SelectMany(kv => kv.Value.Select(m => (Member: m, Group: kv.Key)))
                .GroupBy(x => x.Member, StringComparer.OrdinalIgnoreCase)
                .ToFrozenDictionary(g => g.Key, g => g.Select(x => x.Group).ToArray(), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Registered groups and their member roles.</summary>
    public static IReadOnlyDictionary<string, string[]> All => _members;

    public static bool IsGroup(string role) => _members.ContainsKey(role);

    /// <summary>Member roles of a group (empty when <paramref name="group"/> is not a group).</summary>
    public static IReadOnlyList<string> MembersOf(string group) => _members.TryGetValue(group, out var m) ? m : [];

    /// <summary>Groups a role belongs to.</summary>
    public static IReadOnlyList<string> GroupsOf(string role) => _groupsOf.TryGetValue(role, out var g) ? g : [];

    /// <summary>Effective roles: the assigned roles, the members of assigned groups, and the groups of assigned members.</summary>
    public static IReadOnlySet<string> Expand(IEnumerable<string> assigned)
    {
        var members = _members;
        var groupsOf = _groupsOf;
        var direct = assigned.Where(r => !string.IsNullOrWhiteSpace(r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(direct, StringComparer.OrdinalIgnoreCase);
        foreach (var r in direct)
        {
            if (members.TryGetValue(r, out var m)) result.UnionWith(m);
            if (groupsOf.TryGetValue(r, out var g)) result.UnionWith(g);
        }
        return result;
    }

    private static FrozenDictionary<(string, string), string> _conflicts = FrozenDictionary<(string, string), string>.Empty;

    /// <summary>
    /// Registers a pair of roles that one person should not hold together (segregation of duties), with the reason.
    /// Reported on the user page; not enforced.
    /// </summary>
    public static void RegisterConflict(string a, string b, string why)
    {
        lock (Gate)
        {
            var all = _conflicts.ToDictionary(kv => kv.Key, kv => kv.Value);
            all[(a, b)] = why;
            _conflicts = all.ToFrozenDictionary();
        }
    }

    /// <summary>Registered conflicts present among the effective roles of <paramref name="assigned"/>, as "A + B: why".</summary>
    public static IReadOnlyList<string> FindConflicts(IEnumerable<string> assigned)
    {
        var roles = Expand(assigned);
        return _conflicts.Where(c => roles.Contains(c.Key.Item1) && roles.Contains(c.Key.Item2))
            .Select(c => $"{c.Key.Item1} + {c.Key.Item2}: {c.Value}").Order().ToList();
    }

    /// <summary>Removes all groups (tests).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _members = FrozenDictionary<string, string[]>.Empty;
            _groupsOf = FrozenDictionary<string, string[]>.Empty;
            _conflicts = FrozenDictionary<(string, string), string>.Empty;
        }
    }
}
