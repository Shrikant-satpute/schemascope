namespace SchemaScope.Scripting.Planning;

/// <summary>
/// Engine-neutral planning helpers, taken out of <see cref="CoreRun"/> so every engine's generator orders, diffs and
/// holds back rows the same way. They know nothing about T-SQL; CoreRun forwards to them unchanged.
/// </summary>
public static class PlanOrder
{
    /// <summary>
    /// A stable topological order of 0..<paramref name="count"/>-1. Each dependency cycle (a strongly connected
    /// component) is first collapsed into one block that keeps its members in plain order and sits where its
    /// lowest member is; then Kahn's algorithm always takes the ready block with the lowest index. So an order that
    /// already satisfies every edge comes back as 0, 1, 2, ..., and an item moves only as far as an edge forces it.
    /// The members of every cycle are added to <paramref name="forced"/>: no order satisfies their edges.
    /// </summary>
    /// <param name="edges">(Before, After): Before must come first. Duplicates and self-edges are ignored.</param>
    public static List<int> StableOrder(int count, IEnumerable<(int Before, int After)> edges, List<int> forced)
    {
        var next = new List<int>[count];
        for (var i = 0; i < count; i++) next[i] = [];
        foreach (var (b, a) in edges)
            if (a != b && !next[b].Contains(a)) next[b].Add(a);

        // Tarjan: comp[i] = component id; members of a component in ascending (plain) order
        var comp = new int[count];
        var low = new int[count];
        var num = new int[count];
        Array.Fill(num, -1);
        var onStack = new bool[count];
        var stack = new Stack<int>();
        var members = new List<List<int>>();
        var counter = 0;
        void Visit(int v)
        {
            num[v] = low[v] = counter++;
            stack.Push(v);
            onStack[v] = true;
            foreach (var w in next[v])
            {
                if (num[w] < 0) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack[w]) low[v] = Math.Min(low[v], num[w]);
            }
            if (low[v] != num[v]) return;
            var group = new List<int>();
            int x;
            do { x = stack.Pop(); onStack[x] = false; comp[x] = members.Count; group.Add(x); } while (x != v);
            group.Sort();
            members.Add(group);
        }
        for (var v = 0; v < count; v++) if (num[v] < 0) Visit(v);

        // Kahn over the components, keyed by each component's lowest member
        var compCount = members.Count;
        var waiting = new int[compCount];
        var after = new List<int>[compCount];
        for (var c = 0; c < compCount; c++) after[c] = [];
        var seen = new HashSet<(int, int)>();
        for (var b = 0; b < count; b++)
            foreach (var a in next[b])
                if (comp[a] != comp[b] && seen.Add((comp[b], comp[a]))) { after[comp[b]].Add(comp[a]); waiting[comp[a]]++; }

        var ready = new SortedSet<(int Key, int Comp)>();
        for (var c = 0; c < compCount; c++) if (waiting[c] == 0) ready.Add((members[c][0], c));
        var result = new List<int>(count);
        while (ready.Count > 0)
        {
            var (_, c) = ready.Min;
            ready.Remove(ready.Min);
            result.AddRange(members[c]);
            if (members[c].Count > 1) forced.AddRange(members[c]);
            foreach (var d in after[c])
                if (--waiting[d] == 0) ready.Add((members[d][0], d));
        }
        return result;
    }

    /// <summary>
    /// Holds back until nothing changes: each pass offers every item to <paramref name="holdOne"/>, which returns true
    /// when it held that item. Holding one object can strand the ones that read it, so passes repeat until one holds
    /// nothing.
    /// </summary>
    public static void HoldUntilStable<T>(IEnumerable<T> items, Func<T, bool> holdOne)
    {
        var list = items as IReadOnlyList<T> ?? [.. items];
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var item in list)
                if (holdOne(item)) changed = true;
        }
    }
}

/// <summary>A row's identity key: <see cref="Eq"/> decides "already present", <see cref="Sort"/> orders rows ordinally.</summary>
public readonly record struct PlanKey(string Eq, string Sort);

/// <summary>Source rows against target rows by key: what to create, and what to pair up.</summary>
public static class PlanRows
{
    /// <summary>
    /// Source rows keyed (a repeated key keeps the last row, the first row's sort key), sorted by key, minus keys the
    /// target already has.
    /// </summary>
    public static List<T> Todo<T>(IEnumerable<T> source, IEnumerable<T> target, Func<T, PlanKey> kf)
    {
        var have = new HashSet<string>(target.Select(r => kf(r).Eq), StringComparer.Ordinal);
        var keyed = new Dictionary<string, (string Sort, T Row)>(StringComparer.Ordinal);
        foreach (var r in source)
        {
            var k = kf(r);
            keyed[k.Eq] = (keyed.TryGetValue(k.Eq, out var old) ? old.Sort : k.Sort, r);
        }
        return keyed.Where(e => !have.Contains(e.Key))
            .OrderBy(e => e.Value.Sort, StringComparer.Ordinal)
            .Select(e => e.Value.Row)
            .ToList();
    }

    /// <summary>
    /// Source rows the target also has (same key), each with its target row, sorted by key. Within a key, rows
    /// pair by <paramref name="nameOf"/> first; the rest pair in order with the remaining target rows, one in the
    /// same <paramref name="stateOf"/> as the source row preferred. A source row left without a target row is not paired.
    /// </summary>
    public static List<(T Source, T Target)> Pairs<T, TState>(IEnumerable<T> source, IEnumerable<T> target, Func<T, PlanKey> kf,
        Func<T, string> nameOf, Func<T, TState> stateOf, IEqualityComparer<string> names)
        where T : class
    {
        var states = EqualityComparer<TState>.Default;
        var have = target.GroupBy(r => kf(r).Eq, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var pairs = new List<(T, T)>();
        foreach (var g in source.GroupBy(r => kf(r).Eq, StringComparer.Ordinal)
                     .Where(g => have.ContainsKey(g.Key))
                     .OrderBy(g => kf(g.First()).Sort, StringComparer.Ordinal))
        {
            var free = new List<T>(have[g.Key]);
            var rest = new List<T>();
            foreach (var s in g)
                if (free.FirstOrDefault(x => names.Equals(nameOf(x), nameOf(s))) is { } t) { pairs.Add((s, t)); free.Remove(t); }
                else rest.Add(s);
            foreach (var s in rest)
            {
                if (free.Count == 0) break;
                var t = free.FirstOrDefault(x => states.Equals(stateOf(x), stateOf(s))) ?? free[0];
                pairs.Add((s, t));
                free.Remove(t);
            }
        }
        return pairs;
    }
}
