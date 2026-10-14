using System.Text;
using System.Text.RegularExpressions;

namespace SchemaScope.Scripting;

// 03_missing and 02_tabletypes (generate.py "folder 01" and "folder 03" - the Python's comments use old numbers).
internal sealed partial class CoreRun
{
    private const string NotesFile = "missing_objects_notes.md";

    /// <summary>The DDL of each 03 type/table item, by item number.</summary>
    private readonly Dictionary<int, string> missingSql = [];
    /// <summary>03 modules whose source compile check passed with a note (deferred name resolution).</summary>
    private readonly List<(ModuleRow Module, CompileCheck Check)> compileNotes = [];
    private int missingHeld;

    /// <summary>02: (type, "DROP TYPE … GO CREATE TYPE … GO") per type that is rebuilt, sorted.</summary>
    private readonly List<(ObjectName Type, string Sql)> typePlan = [];
    /// <summary>02: target modules that block DROP TYPE, sorted - dropped and recreated from the TARGET's own text.</summary>
    private readonly List<ModuleRow> blockers = [];

    /// <summary>generate.py KIND: the otypes 03 creates. Triggers (07) and everything else are not mapped.</summary>
    private static string? Kind(string otype) => otype switch
    {
        "FN" or "IF" or "TF" => "function",
        "V" => "view",
        "P" => "proc",
        _ => null,
    };

    // ------------------------------------------------------------------ 03 compute

    /// <summary>
    /// generate.py lines 75-134 plus the documented generalisations: quarantine and exclude patterns leave objects
    /// out; a missing table or module whose name another target object already holds is held back (CREATE would
    /// fail with Msg 2714); modules without a definition are reported; the automatic compile check on the source
    /// replaces BROKEN_IN_DEV, and <see cref="HoldUnbindableAsync"/> adds what only fails on the target. Item order
    /// is the Python's: types, tables, then functions, views, procedures, each sorted - except that a view, inline
    /// function or schema-bound module moves after a missing module it reads (<see cref="DependencyOrderAsync"/>).
    /// Added before the types: schemas, alias types and sequences the target lacks; after the tables: synonyms
    /// (meta.py never read them). Without any of these the file numbers match the Python's.
    /// </summary>
    private async Task ComputeMissingAsync()
    {
        var write = ctx.Selected(ScriptSection.MissingObjects);
        var quarantine = ReadQuarantine();
        facts.QuarantineNames.UnionWith(quarantine);

        string? HeldReason(ObjectName k) =>
            quarantine.Contains(k.Name.ToLowerInvariant()) ? "quarantined: a .sql file with this name is under the quarantine folder"
            : ctx.IsExcluded(k) ? ExcludedReason
            : null;

        void Held(ObjectName k, string type, string why)
        {
            facts.HeldQuarantine.Add(new HeldObject(k, type, why));
            if (write) Hold(ReviewLog.QuarantinedNotApplied, type, k.ToString(), null, why, CoreFoldersGenerator.MissingFolder);
        }

        // modules first, then tables: the Python's held_quarantine order. Types were never quarantined.
        var modules = new List<ObjectName>();
        foreach (var k in src.SortedModules())
        {
            if (tgt.Modules.ContainsKey(k) || !ctx.InScope(k)) continue;
            if (HeldReason(k) is { } why) Held(k, src.Modules[k].OType, why);
            else modules.Add(k);
        }
        var tables = new List<ObjectName>();
        foreach (var k in src.SortedTables())
        {
            if (tgt.Tables.Contains(k) || !ctx.InScope(k)) continue;
            if (HeldReason(k) is { } why) Held(k, "U", why);
            else tables.Add(k);
        }
        var objects = await TargetObjectsAsync();
        string? Collision(ObjectName name) => objects is not null && objects.TryGetValue(name, out var t)
            ? $"{tgtLabel} already has a {TypeWord(t)} named {name} - tables, views, procedures, functions and constraints " +
              "share one namespace per schema, so CREATE would fail (Msg 2714)"
            : null;
        void HoldMissing(string kind, ObjectName k, string why)
        {
            missingHeld++;
            if (write) Hold(ReviewLog.NotApplied, kind, k.ToString(), null, why, CoreFoldersGenerator.MissingFolder);
        }

        foreach (var k in src.SortedTableTypes())
        {
            if (tgt.TableTypes.Contains(k) || !ctx.InScope(k)) continue;
            if (ctx.IsExcluded(k)) Held(k, "TT", ExcludedReason);
            else facts.MissingTypes.Add(k);
        }
        facts.MissingModules.AddRange(modules);

        var srcDef = ScriptSchema.ByTable(src.Defaults, src.NameComparer);
        var srcCk = ScriptSchema.ByTable(src.Checks, src.NameComparer);
        var srcKey = ScriptSchema.ByTable(src.Keys, src.NameComparer);

        // schema-level objects first: a table, type or module in a schema, or with a column type, a default or a
        // view over a synonym, that the target does not have yet fails at CREATE (Msg 2760 / 2715 / 4121 / 208)
        var aliasTypes = new List<AliasTypeRow>();
        foreach (var a in pre.AliasTypes)
        {
            if (!ctx.InScope(a.Key)) continue;
            if (ctx.IsExcluded(a.Key)) Held(a.Key, "alias type", ExcludedReason);
            else if (tgt.TableTypes.Contains(a.Key)) HoldMissing("alias type", a.Key, $"{tgtLabel} already has a table type named {a.Key} (Msg 219)");
            else aliasTypes.Add(a);
        }
        var sequences = new List<SequenceRow>();
        foreach (var s in pre.Sequences)
        {
            if (!ctx.InScope(s.Key)) continue;
            if (ctx.IsExcluded(s.Key)) Held(s.Key, "SO", ExcludedReason);
            else if (Collision(s.Key) is { } why) HoldMissing("sequence", s.Key, why);
            else sequences.Add(s);
        }
        var synonyms = new List<SynonymRow>();
        foreach (var s in pre.Synonyms)
        {
            if (!ctx.InScope(s.Key)) continue;
            if (ctx.IsExcluded(s.Key)) Held(s.Key, "SN", ExcludedReason);
            else if (Collision(s.Key) is { } why) HoldMissing("synonym", s.Key, why);
            else synonyms.Add(s);
        }
        var createdAlias = new HashSet<ObjectName>(aliasTypes.Select(a => a.Key), tgt.NameComparer);
        var createdSeq = new HashSet<ObjectName>(sequences.Select(a => a.Key), tgt.NameComparer);
        string? AliasNotCreated(IEnumerable<IColumnDef> cols) =>
            cols.Select(MissingAliasType).FirstOrDefault(a => a is not null && !createdAlias.Contains(a.Key)) is { } a
                ? $"column type {a.Key} is not in {tgtLabel} and 03_missing does not create it (Msg 2715)"
                : null;

        foreach (var k in tables)
        {
            var why = Collision(k)
                ?? AliasNotCreated(src.ColumnsOf(k))
                ?? (src.ColumnsOf(k).Where(c => c.IsComputed).Select(c => (c.Col, Fn: UsesMissing(c.ComputedDef))).FirstOrDefault(x => x.Fn is not null) is { Fn: { } fn } cc
                    ? $"its computed column {cc.Col} uses {fn}, which {tgtLabel} does not have yet - 03_missing creates functions after " +
                      "tables, so CREATE TABLE would fail (Msg 4121); create this table by hand after 03_missing"
                    : null);
            if (why is null && objects is not null)
            {
                var named = Of(srcDef, k).Cast<INamedConstraint>().Concat(Of(srcKey, k)).Concat(Of(srcCk, k));
                foreach (var r in named)
                {
                    if (r.IsSystemNamed || !objects.TryGetValue(new ObjectName(r.Sch, r.CName), out var t)) continue;
                    why = $"its constraint {r.CName} has the name of a {TypeWord(t)} {tgtLabel} already has - CREATE TABLE would fail (Msg 2714)";
                    break;
                }
            }
            if (why is not null)
            {
                missingHeld++;
                if (write) Hold(ReviewLog.NotApplied, "table", k.ToString(), null, why, CoreFoldersGenerator.MissingFolder);
                continue;
            }
            facts.MissingTables.Add(k);
        }

        // modules: what 03 would create, minus what cannot be created
        var candidates = new List<ModuleRow>();
        foreach (var k in modules)
        {
            var m = src.Modules[k];
            var kind = Kind(m.OType);
            string? why;
            if (kind is null)
            {
                if (m.OType == "TR") continue;                              // 07's job
                why = $"a {TypeWord(m.OType)} ({m.OType}) - 03_missing creates only functions, views and procedures; create it by hand";
                kind = "module";
            }
            else if (m.Definition is null)
                why = ScriptText.RawModuleProblem(null) + " - it cannot be scripted";
            else
                why = Collision(k);
            if (why is not null)
            {
                missingHeld++;
                if (write) Hold(ReviewLog.NotApplied, kind, k.ToString(), null, why, CoreFoldersGenerator.MissingFolder);
                continue;
            }
            candidates.Add(m);
        }

        var checks = new CompileCheck[candidates.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct },
            async (i, ct) => { checks[i] = await ctx.SourceProbes.CompileCheckAsync(candidates[i], ct); });

        var creatable = new HashSet<ObjectName>(src.NameComparer);
        for (var i = 0; i < candidates.Count; i++)
        {
            var m = candidates[i];
            var r = checks[i];
            if (r.Compiles == false)
            {
                var why = $"does not compile in {srcLabel} (Msg {r.ErrorNumber}: {r.Message}) - its CREATE fails on any database";
                facts.BrokenInSource.Add(new HeldObject(m.Name, m.OType, why));
                missingHeld++;
                if (write) Hold(ReviewLog.NotApplied, Kind(m.OType)!, m.Name.ToString(), null, why, CoreFoldersGenerator.MissingFolder);
                continue;
            }
            if (r.Note is not null) compileNotes.Add((m, r));
            creatable.Add(m.Name);
        }
        // before the modules: a module whose table-type parameter is held here cannot be created either
        facts.MissingTypes.RemoveAll(k =>
        {
            if (AliasNotCreated(src.ColumnsOfType(k)) is not { } why) return false;
            HoldMissing("table type", k, why);
            return true;
        });

        await HoldUnbindableAsync(candidates, creatable, objects, synonyms, write);
        compileNotes.RemoveAll(c => !creatable.Contains(c.Module.Name));

        // check constraints that call a user function must wait until 03 has created the functions (05 adds them)
        var callsFn = CallsFunctionRegex();
        bool CallsFunction(string d) => callsFn is not null && callsFn.IsMatch(d.ToLowerInvariant());

        var n = 0;
        void Add(string kind, ObjectName k, ModuleRow? m, string? sql, string? fileName = null)
        {
            n++;
            var file = $"{N3(n)}_{kind}_{ScriptText.Safe(fileName ?? AfterFirstDot(k))}.sql";
            facts.MissingItems.Add(new MissingItem(n, file, kind, k, m));
            if (sql is not null) missingSql[n] = sql;
        }

        foreach (var s in pre.Schemas)
            if (ctx.InScope(new ObjectName(s, ""))) Add("schema", new ObjectName(s, ""), null, Ddl.CreateSchema(s), s);
        foreach (var a in aliasTypes) Add("aliastype", a.Key, null, Ddl.CreateAliasType(a));
        foreach (var s in sequences) Add("sequence", s.Key, null, Ddl.CreateSequence(s));

        foreach (var k in facts.MissingTypes)
            Add("type", k, null, Ddl.CreateType(k.Schema, k.Name, src.ColumnsOfType(k), src.IndexesOfType(k), coll, src.ChecksOfType(k)));

        foreach (var k in facts.MissingTables)
        {
            // a default on a function the target lacks waits for 03's functions, as a function-calling check does;
            // a check disabled in the source waits too, because a CHECK inside CREATE TABLE is always enabled (05
            // adds it, then disables it)
            var defaults = new Dictionary<string, DefaultRow>(StringComparer.Ordinal);
            foreach (var r in Of(srcDef, k))
            {
                if (UsesMissing(r.Definition, (name, isFunction) => isFunction || !createdSeq.Contains(name)) is not null)
                    facts.DeferredDefaults.Add(r);
                else defaults[r.Col] = r;
            }
            var inline = new List<CheckRow>();
            foreach (var r in Of(srcCk, k))
            {
                if (CallsFunction(r.Definition) || r.IsDisabled) facts.DeferredChecks.Add(r);
                else inline.Add(r);
            }
            Add("table", k, null, Ddl.CreateTable(k.Schema, k.Name, src.ColumnsOf(k), defaults, Of(srcKey, k), inline, coll));
        }

        // after the tables (a synonym usually names one), before the modules that read them
        foreach (var s in synonyms) Add("synonym", s.Key, null, Ddl.CreateSynonym(s));

        var ordered = new List<ModuleRow>();
        foreach (var want in new[] { "function", "view", "proc" })
            foreach (var k in modules)
            {
                var m = src.Modules[k];
                if (Kind(m.OType) == want && creatable.Contains(k)) ordered.Add(m);
            }
        foreach (var m in await DependencyOrderAsync(ordered, write)) Add(Kind(m.OType)!, m.Name, m, null);
    }

    /// <summary>
    /// Holds back the 03 modules whose CREATE would fail on the target although they compile on the source
    /// (the compile check only sees the source):
    /// <list type="bullet">
    /// <item>a module with a table-valued parameter whose type the target lacks and 03 does not create (held,
    /// excluded, or left out of an object script): CREATE fails (Msg 2715);</item>
    /// <item>a module that names a column the target will not have after 01 (blocked, or on an excluded table)
    /// together with that column's table: SQL Server checks the columns of an existing table when any module is
    /// created (Msg 207). Matched on the words of the body, so a body that names both is held even when the
    /// column belongs to another table there - the safe side;</item>
    /// <item>a view, inline function or schema-bound module that reads an object the target does not have and 03
    /// does not create (quarantined, excluded or held back): those bind every name at CREATE (Msg 208). Repeated
    /// until nothing changes, because holding one view strands the views that read it.</item>
    /// </list>
    /// </summary>
    private async Task HoldUnbindableAsync(List<ModuleRow> candidates, HashSet<ObjectName> creatable,
        Dictionary<ObjectName, string>? objects, List<SynonymRow> synonyms, bool write)
    {
        void HoldModule(ModuleRow m, string why)
        {
            creatable.Remove(m.Name);
            missingHeld++;
            if (write) Hold(ReviewLog.NotApplied, Kind(m.OType)!, m.Name.ToString(), null, why, CoreFoldersGenerator.MissingFolder);
        }

        var newTypes = new HashSet<ObjectName>(facts.MissingTypes, src.NameComparer);
        foreach (var m in candidates)
        {
            if (!creatable.Contains(m.Name)) continue;
            if (src.TtParams.FirstOrDefault(p => src.NameComparer.Equals(p.Module, m.Name) &&
                                                 !tgt.TableTypes.Contains(p.Type) && !newTypes.Contains(p.Type)) is { } tt)
                HoldModule(m, $"takes table type {tt.Type}, which {tgtLabel} does not have and 03_missing does not create - CREATE would fail (Msg 2715)");
        }

        if (facts.UnavailableColumns.Count > 0)
        {
            var byTable = facts.UnavailableColumns.GroupBy(u => u.Table, src.NameComparer).ToList();
            foreach (var m in candidates)
            {
                if (!creatable.Contains(m.Name)) continue;
                var words = new HashSet<string>(
                    TsqlTokens.Tokens(m.Definition!).Where(t => t.Kind == 'I').Select(t => t.Text), StringComparer.OrdinalIgnoreCase);
                foreach (var g in byTable)
                {
                    if (!words.Contains(g.Key.Name) || g.FirstOrDefault(u => words.Contains(u.Column)) is not { } col) continue;
                    HoldModule(m, $"names {g.Key.Name}.{col.Column}, a column {tgtLabel} will not have (01_columns does not add it) - " +
                                  "SQL Server checks the columns of an existing table when the module is created, so CREATE would fail (Msg 207)");
                    break;
                }
            }
        }

        var deps = await ctx.SourceProbes.ModuleDependenciesAsync(Ct);
        if (deps is null) return;   // the probe left its own "not checked" note
        var refs = deps.ToLookup(d => d.Referencing, src.NameComparer);
        var newTables = new HashSet<ObjectName>(facts.MissingTables, src.NameComparer);
        var newSynonyms = new HashSet<ObjectName>(synonyms.Select(s => s.Key), src.NameComparer);
        bool Known(ObjectName o) => src.Tables.Contains(o) || src.Modules.ContainsKey(o) || pre.SourceSynonyms.Contains(o);
        bool InTarget(ObjectName o) => objects?.ContainsKey(o) ?? (tgt.Tables.Contains(o) || tgt.Modules.ContainsKey(o));
        bool Available(ObjectName o) => !Known(o) || InTarget(o) || newTables.Contains(o) || creatable.Contains(o) || newSynonyms.Contains(o);

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var m in candidates)
            {
                if (!creatable.Contains(m.Name) || !(m.OType is "V" or "IF" || IsSchemaBound(m.Definition))) continue;
                var missing = refs[m.Name].Select(d => (ObjectName?)d.Referenced)
                    .FirstOrDefault(r => !src.NameComparer.Equals(r!.Value, m.Name) && !Available(r.Value));
                if (missing is not { } r) continue;
                HoldModule(m, $"a {TypeWord(m.OType)} binds every name it reads when it is created, and it reads {r}, which {tgtLabel} " +
                              "does not have and 03_missing does not create - CREATE would fail (Msg 208)");
                changed = true;
            }
        }
    }

    /// <summary>
    /// The 03 modules in an order SQL Server can create them in. Views, inline functions and anything WITH
    /// SCHEMABINDING bind every reference at CREATE, so each must come after the missing modules it reads
    /// (a view on a later view, an inline function on a view). Procedures and scalar/multi-statement functions
    /// resolve names when they run, so their references impose no order.
    /// </summary>
    /// <remarks>
    /// A stable topological sort over the SOURCE's sys.sql_expression_dependencies: Kahn's algorithm that
    /// always takes the earliest ready module of <paramref name="items"/> (functions, views, procedures, each
    /// sorted - the Python's order). An order that already satisfies every edge comes back unchanged, so with no
    /// such edges the file numbers stay the Python's. A cycle cannot be ordered: its members keep their place
    /// relative to each other and a review row says so. When the dependencies cannot be read, nothing moves.
    /// </remarks>
    private async Task<List<ModuleRow>> DependencyOrderAsync(List<ModuleRow> items, bool write)
    {
        if (items.Count < 2) return items;
        var deps = await ctx.SourceProbes.ModuleDependenciesAsync(Ct);
        if (deps is null) return items;   // the probe left its own "not checked" note

        var index = new Dictionary<ObjectName, int>(src.NameComparer);
        for (var i = 0; i < items.Count; i++) index.TryAdd(items[i].Name, i);
        var bindsAtCreate = items.Select(m => m.OType is "V" or "IF" || IsSchemaBound(m.Definition)).ToArray();

        var edges = new List<(int Before, int After)>();
        foreach (var d in deps)
        {
            if (!index.TryGetValue(d.Referencing, out var a) || !bindsAtCreate[a]) continue;
            if (!index.TryGetValue(d.Referenced, out var b) || a == b) continue;
            edges.Add((b, a));
        }
        var forced = new List<int>();
        var result = StableOrder(items.Count, edges, forced).Select(i => items[i]).ToList();
        var cyclic = forced.Select(i => items[i]).ToList();

        if (cyclic.Count > 0 && write)
            foreach (var m in cyclic)
                ctx.Review.Note(ReviewLog.NotApplied, Kind(m.OType)!, m.Name.ToString(), null,
                    "created, but check it: it is in a dependency cycle among the modules 03_missing creates, so no order " +
                    "satisfies every reference - the cycle keeps its plain order, and a CREATE in it may fail until the others " +
                    "exist; run 03_missing again with -ContinueOnError to create what failed",
                    CoreFoldersGenerator.MissingFolder);
        return result;
    }

    /// <summary>
    /// A stable topological order of 0..<paramref name="count"/>-1. Each dependency cycle (a strongly connected
    /// component) is first collapsed into one block that keeps its members in plain order and sits where its
    /// lowest member is; then Kahn's algorithm always takes the ready block with the lowest index. So an order that
    /// already satisfies every edge comes back as 0, 1, 2, ..., and an item moves only as far as an edge forces it.
    /// The members of every cycle are added to <paramref name="forced"/>: no order satisfies their edges.
    /// </summary>
    /// <param name="edges">(Before, After): Before must come first. Duplicates and self-edges are ignored.</param>
    internal static List<int> StableOrder(int count, IEnumerable<(int Before, int After)> edges, List<int> forced)
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
    /// The first of <paramref name="names"/> that appears as a word in <paramref name="definition"/> (comments and
    /// strings ignored, any case), or null. A word match cannot tell which table a column belongs to, so a body that
    /// uses the same name on another table matches too - the safe side for a hold.
    /// </summary>
    private static string? NamesAny(string definition, IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return null;
        var words = new HashSet<string>(
            TsqlTokens.Tokens(definition).Where(t => t.Kind == 'I').Select(t => t.Text), StringComparer.OrdinalIgnoreCase);
        return names.FirstOrDefault(words.Contains);
    }

    /// <summary>True when the module text says WITH SCHEMABINDING (comments and strings ignored).</summary>
    internal static bool IsSchemaBound(string? definition) =>
        definition is not null &&
        TsqlTokens.Tokens(definition).Any(t => t.Kind == 'I' && t.Text.Equals("SCHEMABINDING", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// generate.py <c>calls_function</c> as one regex: <c>[\[\.\s(]</c> + any lower-cased FN/IF/TF name + <c>\s*\(</c>,
    /// matched against the lower-cased definition. Null when the source has no functions.
    /// </summary>
    private Regex? CallsFunctionRegex()
    {
        var names = src.Modules.Values
            .Where(m => m.OType is "FN" or "IF" or "TF")
            .Select(m => Regex.Escape(m.Obj.ToLowerInvariant()))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return names.Count == 0
            ? null
            : new Regex(@"[\[\.\s(](?:" + string.Join("|", names) + @")\s*\(", RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Lower-cased base names (without .sql) of every *.sql file under the quarantine folder, recursively - hidden
    /// files included, as os.walk does. Only the object name is matched; the schema is ignored, as in the Python.
    /// </summary>
    private HashSet<string> ReadQuarantine()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var folder = ctx.Options.QuarantineFolder;
        if (string.IsNullOrWhiteSpace(folder)) return set;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            // "*" + an explicit test: the Win32 pattern "*.sql" would also match "x.sqlx"
            foreach (var f in Directory.EnumerateFiles(folder, "*", options))
                if (f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    set.Add(Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ctx.Review.Warn($"The quarantine folder could not be read ({ex.Message}); nothing was left out of 03_missing because of it.");
        }
        return set;
    }

    /// <summary>generate.py <c>name.split('.', 1)[1]</c> on <c>"sch.name"</c>.</summary>
    private static string AfterFirstDot(ObjectName k)
    {
        var s = k.ToString();
        return s[(s.IndexOf('.') + 1)..];
    }

    // ------------------------------------------------------------------ 03 write

    private FolderResult WriteMissing()
    {
        const string folder = CoreFoldersGenerator.MissingFolder;
        facts.MissingWritten = true;
        if (facts.MissingItems.Count == 0)
        {
            var none = new List<string>();
            if (facts.HeldQuarantine.Count > 0)
                none.Add($"{facts.HeldQuarantine.Count} object(s) left out by the quarantine folder or exclude patterns (99_review\\{ReviewLog.QuarantinedNotApplied}).");
            if (missingHeld > 0)
                none.Add($"{missingHeld} object(s) held back (99_review\\{ReviewLog.NotApplied}).");
            return NothingToDo(folder, "Missing objects", none);
        }
        EnsureObjects(folder);
        var all = new StringBuilder(ScriptText.HdrTbl);
        var expected = new List<ExpectedModule>();
        foreach (var item in facts.MissingItems)
        {
            var rel = "objects\\" + item.File;
            if (item.Module is { } m)
            {
                var def = m.Definition!;
                WriteModuleFile(folder, rel, m, def);
                all.Append(AllChunk(m, def));
                expected.Add(new ExpectedModule(m.Name, def, ModuleSettings.Of(m)));
            }
            else
            {
                var sql = missingSql[item.Number];
                ctx.Writer.WriteSql(folder, rel, ScriptText.HdrTbl + sql + ScriptText.Go, WrittenFileKind.Statements, item.Object);
                all.Append(sql).Append(ScriptText.Go);
            }
        }

        var notes = new List<string>();
        var counts = facts.MissingItems.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        var extra = string.Concat(new[] { ("schema", "schema(s)"), ("aliastype", "alias type(s)"), ("sequence", "sequence(s)"), ("synonym", "synonym(s)") }
            .Where(k => counts.ContainsKey(k.Item1)).Select(k => $"{counts[k.Item1]} {k.Item2}, "));
        notes.Add($"{extra}{counts.GetValueOrDefault("type")} table type(s), {counts.GetValueOrDefault("table")} table(s), " +
                  $"{counts.GetValueOrDefault("function")} function(s), {counts.GetValueOrDefault("view")} view(s), " +
                  $"{counts.GetValueOrDefault("proc")} procedure(s).");
        if (facts.HeldQuarantine.Count > 0)
            notes.Add($"{facts.HeldQuarantine.Count} object(s) left out by the quarantine folder or exclude patterns (99_review\\{ReviewLog.QuarantinedNotApplied}).");
        if (missingHeld > 0)
            notes.Add($"{missingHeld} object(s) held back (99_review\\{ReviewLog.NotApplied}).");
        if (compileNotes.Count > 0)
        {
            notes.Add($"{compileNotes.Count} module(s) reference an object that does not exist in {srcLabel} either (99_review\\{NotesFile}).");
            ctx.Review.SetDocument(NotesFile, CompileNotesDocument());
        }
        var drifted = new HashSet<ObjectName>(facts.DriftedTableTypes, src.NameComparer);
        var needTypes = facts.MissingItems
            .Where(i => i.Module is not null && src.TtParams.Any(p => src.NameComparer.Equals(p.Module, i.Object) && drifted.Contains(p.Type)))
            .Select(i => i.Object.ToString()).ToList();
        if (needTypes.Count > 0)
        {
            var text = $"03_missing creates {needTypes.Count} module(s) that take a table type whose columns differ in {tgtLabel} " +
                       $"({string.Join(", ", needTypes.Take(5))}{(needTypes.Count > 5 ? ", ..." : "")}): they need 02_tabletypes first.";
            notes.Add(text);
            ctx.Review.Warn(text);
        }

        return FinishFolder(folder, "Missing objects", all.ToString(), expected, inRunAll: true, notes);
    }

    private string CompileNotesDocument()
    {
        var sb = new StringBuilder();
        sb.Append("# Missing objects created with a warning\n\n");
        sb.Append($"{compileNotes.Count} module(s) that 03_missing creates name an object that does not exist in {srcLabel} either. ");
        sb.Append("SQL Server accepts the CREATE (deferred name resolution), but the statement that uses the missing object fails when it runs - ");
        sb.Append($"exactly as it does in {srcLabel} today. Nothing was held back for this.\n\n");
        sb.Append("| Object | Kind | Msg | Note |\n|---|---|---|---|\n");
        foreach (var (m, c) in compileNotes)
            sb.Append($"| {ReviewLog.Cell(m.Name.ToString())} | {Kind(m.OType)} | {c.ErrorNumber} | {ReviewLog.Cell(c.Note ?? "")} |\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 02 compute

    /// <summary>
    /// generate.py lines 232-276. Drift = the sets of lower-cased column names differ (names only, as the Python).
    /// Blockers are the TARGET modules that use a drifted type - matched on the type's own schema
    /// (<see cref="TtParamRow.Type"/>), plus body references from <see cref="CatalogProbes.TypeReferencersAsync"/> -
    /// because DROP TYPE is refused while any of them exists. A type whose blocker cannot be dropped and recreated
    /// from its own text (no definition, no DROP word for its type, or a body that names a column the source's type
    /// drops - its CREATE would fail with Msg 207 once it is dropped) is held back instead of half-done.
    /// </summary>
    private async Task ComputeTableTypesAsync()
    {
        var write = ctx.Selected(ScriptSection.TableTypes);
        foreach (var k in src.SortedTableTypes())
        {
            if (!tgt.TableTypes.Contains(k)) continue;
            var a = src.ColumnsOfType(k).Select(c => c.Col.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var b = tgt.ColumnsOfType(k).Select(c => c.Col.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            if (!a.SetEquals(b)) facts.DriftedTableTypes.Add(k);
        }
        if (facts.DriftedTableTypes.Count == 0) return;

        var refs = await ctx.Probes.TypeReferencersAsync(Ct) ?? Array.Empty<TypeReference>();
        var chosen = new Dictionary<ObjectName, ModuleRow>(tgt.NameComparer);
        foreach (var k in facts.DriftedTableTypes)
        {
            if (!ctx.InScope(k)) continue;
            if (ctx.IsExcluded(k))
            {
                if (write) Hold(ReviewLog.NotApplied, "table type", k.ToString(), null, ExcludedReason, CoreFoldersGenerator.TableTypesFolder);
                continue;
            }
            var users = tgt.TtParams.Where(r => tgt.NameComparer.Equals(r.Type, k)).Select(r => r.Module)
                .Concat(refs.Where(r => tgt.NameComparer.Equals(r.Type, k)).Select(r => r.Module))
                .Distinct(tgt.NameComparer)
                .ToList();
            // columns the target's type has and the source's does not: a blocker recreated from the target's text
            // against the source's type fails if it names one (SQL Server checks a table-valued parameter's columns
            // at CREATE: Msg 207) - after 001 has already dropped it
            var kept = src.ColumnsOfType(k).Select(c => c.Col.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var removed = tgt.ColumnsOfType(k).Select(c => c.Col).Where(c => !kept.Contains(c.ToLowerInvariant())).ToList();
            var mine = new List<ModuleRow>();
            string? why = null;
            foreach (var u in users)
            {
                var m = tgt.FindModule(u);
                if (m is null || m.Definition is null)
                    why = $"{tgtLabel}'s {u} uses it and has no T-SQL definition to recreate it from, so DROP TYPE cannot be done by script";
                else if (Ddl.DropWord(m.OType) is null)
                    why = $"{tgtLabel}'s {u} ({m.OType}) uses it and cannot be dropped and recreated by script";
                else if (NamesAny(m.Definition, removed) is { } gone)
                    why = $"{tgtLabel}'s {u} names column {gone}, which {srcLabel}'s type does not have - recreating {u} from its own text " +
                          "against the new type would fail (Msg 207) after it was dropped, so the type is left as it is";
                else
                {
                    mine.Add(m);
                    continue;
                }
                break;
            }
            if (why is not null)
            {
                if (write) Hold(ReviewLog.NotApplied, "table type", k.ToString(), null, why, CoreFoldersGenerator.TableTypesFolder);
                continue;
            }
            foreach (var m in mine) chosen.TryAdd(m.Name, m);
            typePlan.Add((k, DropTypeSql(k) +
                Ddl.CreateType(k.Schema, k.Name, src.ColumnsOfType(k), src.IndexesOfType(k), coll, src.ChecksOfType(k)) +
                "\r\nGO\r\n"));
        }
        blockers.AddRange(chosen.Values.OrderBy(m => m.Name, ObjectName.Ordinal));
        facts.TableTypeBlockers.AddRange(blockers.Select(m => m.Name));
    }

    // ------------------------------------------------------------------ 02 write

    /// <summary>
    /// generate.py's file set: 001 drops the blockers behind a permission guard that sets NOEXEC, 002+ rebuild each
    /// type, then each blocker is recreated from the TARGET's text (site customisations survive), then NOEXEC is reset.
    /// Port fixes: the DROP word follows the blocker's type (the Python always said PROCEDURE); with no blockers the
    /// guard has no object part (which would be <c>IN ()</c>, a syntax error) and 001 is <c>001_check_permissions.sql</c>;
    /// <c>_ALL.sql</c> rebuilds each blocker with exec_block under its own SET options (the Python pasted the raw text
    /// under HDR_TBL's ON settings, and SSMS could change its line endings). Added: the guard also stops on
    /// permissions granted on a rebuilt type (DROP TYPE loses them; each type file repeats that check for a run file
    /// by file), every DROP is <c>IF ..._ID(...) IS NOT NULL</c> so a re-run does not stop at what is already gone,
    /// and a <c>rollback\</c> folder (<see cref="WriteTableTypesRollback"/>), because 02 replaces existing objects
    /// (DESIGN rule 2).
    /// </summary>
    private async Task<FolderResult> WriteTableTypesAsync()
    {
        const string folder = CoreFoldersGenerator.TableTypesFolder;
        facts.TableTypesWritten = true;
        if (typePlan.Count == 0)
            return NothingToDo(folder, "Changed table types",
                [facts.DriftedTableTypes.Count == 0 ? "No table type differs." : "Every changed table type was held back (99_review)."]);
        EnsureObjects(folder);
        var notes = new List<string>();

        // 001: the permission guard (object grants on the blockers, and grants on the types: DROP TYPE loses
        // GRANT ... ON TYPE:: as DROP loses an object's) sets NOEXEC, then the blockers are dropped. Every DROP is
        // guarded by IF ..._ID(...) IS NOT NULL, so a re-run after a partial run (or the rollback) does not stop
        // at an object that is already gone.
        var dropSql = new StringBuilder();
        dropSql.Append("IF EXISTS (SELECT 1 FROM sys.database_permissions WHERE ");
        if (blockers.Count > 0)
        {
            dropSql.Append("(class = 1 AND major_id IN (\r\n");
            dropSql.Append(string.Join(",\r\n", blockers.Select(b => $"    OBJECT_ID({ScriptText.Lit(b.Name.Bracketed)})")));
            dropSql.Append("))\r\n   OR ");
        }
        dropSql.Append("(class = 6 AND major_id IN (");
        dropSql.Append(string.Join(", ", typePlan.Select(t => $"TYPE_ID({ScriptText.Lit(t.Type.Bracketed)})")));
        dropSql.Append(")))\r\nBEGIN\r\n");
        dropSql.Append("    RAISERROR('Permissions exist on the modules or table types about to be dropped. Script them out, then re-run.', 16, 1);\r\n");
        dropSql.Append("    SET NOEXEC ON;\r\nEND\r\nGO\r\n");
        foreach (var b in blockers) dropSql.Append(DropModuleSql(b));

        var all = new StringBuilder(ScriptText.HdrTbl);
        var expected = new List<ExpectedModule>();
        ctx.Writer.WriteSql(folder, blockers.Count > 0 ? "objects\\001_drop_blocking_modules.sql" : "objects\\001_check_permissions.sql",
            ScriptText.Hdr + dropSql);
        all.Append(dropSql);
        var n = 2;
        foreach (var (type, sql) in typePlan)
        {
            // the type's own guard as well: run file by file, a tripped 001 does not stop this file, and with no
            // blocker left to refuse it, DROP TYPE would go ahead and lose the grants
            var guarded = TypePermissionGuard(type) + sql;
            ctx.Writer.WriteSql(folder, $"objects\\{N3(n)}_type_{ScriptText.Safe(type.Name)}.sql", ScriptText.HdrTbl + guarded,
                WrittenFileKind.Statements, type);
            all.Append(guarded);
            n++;
        }
        foreach (var b in blockers)
        {
            var def = b.Definition!;
            WriteModuleFile(folder, $"objects\\{N3(n)}_recreate_{ScriptText.Safe(b.Obj)}.sql", b, def);
            all.Append(AllChunk(b, def));
            expected.Add(new ExpectedModule(b.Name, def, ModuleSettings.Of(b)));
            n++;
        }
        ctx.Writer.WriteSql(folder, $"objects\\{N3(n)}_reset.sql", "SET NOEXEC OFF\r\nGO\r\n");
        all.Append("SET NOEXEC OFF\r\nGO\r\n");

        notes.Add($"{typePlan.Count} table type(s) rebuilt; {blockers.Count} {tgtLabel} module(s) that use them are dropped and recreated from {tgtLabel}'s own text.");
        var perms = await ctx.Probes.TypePermissionsAsync(Ct);
        var granted = typePlan.Where(t => perms?.Any(p => tgt.NameComparer.Equals(p, t.Type)) == true).Select(t => t.Type.ToString()).ToList();
        if (granted.Count > 0)
        {
            var text = $"02_tabletypes: {string.Join(", ", granted)} carry permissions in {tgtLabel}; DROP TYPE would lose them, so 02 stops " +
                       "before dropping anything until they are scripted out and revoked.";
            notes.Add(text);
            ctx.Review.Warn(text);
        }

        var result = FinishFolder(folder, "Changed table types", all.ToString(), expected, inRunAll: false, notes);
        WriteTableTypesRollback(folder);
        result.HasRollback = true;
        return result;
    }

    /// <summary><c>IF TYPE_ID(N'[s].[t]') IS NOT NULL DROP TYPE [s].[t]</c> + GO: a re-run does not stop where the type is already gone.</summary>
    private static string DropTypeSql(ObjectName type) =>
        $"IF TYPE_ID({ScriptText.Lit(type.Bracketed)}) IS NOT NULL\r\n    DROP TYPE {type.Bracketed}\r\nGO\r\n";

    /// <summary><c>IF OBJECT_ID(N'[s].[m]') IS NOT NULL DROP {word} [s].[m]</c> + GO.</summary>
    private static string DropModuleSql(ModuleRow b) =>
        $"IF OBJECT_ID({ScriptText.Lit(b.Name.Bracketed)}) IS NOT NULL\r\n    DROP {Ddl.DropWord(b.OType)} {b.Name.Bracketed}\r\nGO\r\n";

    /// <summary>Stops the session (NOEXEC) before a DROP TYPE that would lose permissions granted on the type.</summary>
    private static string TypePermissionGuard(ObjectName type) =>
        $"IF EXISTS (SELECT 1 FROM sys.database_permissions WHERE class = 6 AND major_id = TYPE_ID({ScriptText.Lit(type.Bracketed)}))\r\n" +
        "BEGIN\r\n" +
        "    RAISERROR('Permissions exist on the table type about to be dropped. Script them out, then re-run.', 16, 1);\r\n" +
        "    SET NOEXEC ON;\r\nEND\r\nGO\r\n";

    /// <summary>
    /// <c>02_tabletypes\rollback\</c>: puts the target back as it was before 02 ran. A guard first stops the
    /// rollback (NOEXEC) while a module other than the blockers uses one of the rebuilt types - e.g. one 03_missing
    /// created for the new type - because DROP TYPE would be refused half way. Then the blockers are dropped, each
    /// type is rebuilt from the TARGET's own catalog (its own spelling and collation, from
    /// <see cref="ScriptContext.RawTarget"/>), and the blockers are recreated from the TARGET's text with the same
    /// raw-file / exec_block rules as the forward files. The files sit directly in <c>rollback\</c>, as 08's do.
    /// </summary>
    private void WriteTableTypesRollback(string folder)
    {
        var raw = ctx.RawTarget;
        static string Lit(ObjectName o) => ScriptText.Lit(o.Bracketed);

        var types = string.Join(", ", typePlan.Select(t => $"TYPE_ID({Lit(t.Type)})"));
        var mods = string.Join(", ", blockers.Select(b => $"ISNULL(OBJECT_ID({Lit(b.Name)}), 0)"));
        var guard = new StringBuilder();
        guard.Append($"IF EXISTS (SELECT 1 FROM sys.parameters WHERE user_type_id IN ({types})");
        if (blockers.Count > 0) guard.Append($"\r\n              AND object_id NOT IN ({mods})");
        guard.Append(")\r\n");
        guard.Append($"   OR EXISTS (SELECT 1 FROM sys.sql_expression_dependencies WHERE referenced_class = 6 AND referenced_id IN ({types})");
        if (blockers.Count > 0) guard.Append($"\r\n              AND referencing_id NOT IN ({mods})");
        guard.Append(")\r\nBEGIN\r\n");
        guard.Append("    RAISERROR('Other modules use the table types this rollback puts back. Drop or change them first, then re-run.', 16, 1);\r\n");
        guard.Append("    SET NOEXEC ON;\r\nEND\r\nGO\r\n");
        // idempotent: after a forward run that stopped half way, a blocker or a type may already be gone
        foreach (var b in blockers) guard.Append(DropModuleSql(b));

        var all = new StringBuilder(ScriptText.HdrTbl);
        var expected = new List<ExpectedModule>();
        ctx.Writer.WriteSql(folder, blockers.Count > 0 ? "rollback\\001_drop_blocking_modules.sql" : "rollback\\001_check_type_users.sql",
            ScriptText.Hdr + guard);
        all.Append(guard);
        var n = 2;
        foreach (var (type, _) in typePlan)
        {
            var own = raw.TableTypes.Where(t => raw.NameComparer.Equals(t, type)).DefaultIfEmpty(type).First();
            var sql = DropTypeSql(type) +
                      Ddl.CreateType(own.Schema, own.Name, raw.ColumnsOfType(own), raw.IndexesOfType(own), raw.Collation, raw.ChecksOfType(own)) +
                      "\r\nGO\r\n";
            ctx.Writer.WriteSql(folder, $"rollback\\{N3(n)}_type_{ScriptText.Safe(own.Name)}.sql", ScriptText.HdrTbl + sql,
                WrittenFileKind.Statements, own);
            all.Append(sql);
            n++;
        }
        foreach (var b in blockers)
        {
            var def = b.Definition!;
            WriteModuleFile(folder, $"rollback\\{N3(n)}_recreate_{ScriptText.Safe(b.Obj)}.sql", b, def);
            all.Append(AllChunk(b, def));
            expected.Add(new ExpectedModule(b.Name, def, ModuleSettings.Of(b)));
            n++;
        }
        ctx.Writer.WriteSql(folder, $"rollback\\{N3(n)}_reset.sql", "SET NOEXEC OFF\r\nGO\r\n");
        all.Append("SET NOEXEC OFF\r\nGO\r\n");
        ctx.Writer.WriteSql(folder, "rollback\\_ALL.sql", all.ToString(),
            expected.Count > 0 ? WrittenFileKind.ExecBlockModules : WrittenFileKind.Statements,
            expectedModules: expected.Count > 0 ? expected : null);
    }
}
