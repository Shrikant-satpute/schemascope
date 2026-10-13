namespace SchemaScope.Scripting;

// What other objects need to exist before they can be created: schemas, alias types, sequences and synonyms
// (meta.py never read them), and the functions and sequences a default or a computed column uses.
internal sealed partial class CoreRun
{
    private Prereqs pre = new();

    /// <summary>The source's schema-level objects the target lacks, and what a default or computed column may need.</summary>
    private sealed class Prereqs
    {
        /// <summary>Source user schemas the target does not have, sorted.</summary>
        public List<string> Schemas { get; } = [];
        /// <summary>Source alias types the target does not have, sorted (exclusions not applied yet).</summary>
        public List<AliasTypeRow> AliasTypes { get; } = [];
        /// <summary>Source sequences the target does not have, sorted.</summary>
        public List<SequenceRow> Sequences { get; } = [];
        /// <summary>Source synonyms the target does not have, sorted.</summary>
        public List<SynonymRow> Synonyms { get; } = [];
        /// <summary>Every source synonym (a view may read one).</summary>
        public HashSet<ObjectName> SourceSynonyms { get; init; } = [];
        /// <summary>
        /// Lower-cased name → the source function (FN/IF/TF) or sequence the target does not have. Matched by name
        /// alone, as generate.py's calls_function did.
        /// </summary>
        public Dictionary<string, (ObjectName Name, bool IsFunction)> MissingCallables { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads schemas, alias types, sequences and synonyms on both sides (read-only probes; a probe that fails
    /// leaves its own "not checked" note). When the target side cannot be read nothing is taken as missing, so
    /// nothing is created twice; when the source side cannot be read there is nothing to create.
    /// </summary>
    private async Task LoadPrereqsAsync()
    {
        var srcSchemas = await ctx.SourceProbes.UserSchemasAsync(Ct) ?? [];
        var tgtSchemas = await ctx.Probes.UserSchemasAsync(Ct);
        var srcAlias = await ctx.SourceProbes.AliasTypesAsync(Ct) ?? [];
        var tgtAlias = await ctx.Probes.AliasTypesAsync(Ct);
        var srcSeq = await ctx.SourceProbes.SequencesAsync(Ct) ?? [];
        var tgtSeq = await ctx.Probes.SequencesAsync(Ct);
        var srcSyn = await ctx.SourceProbes.SynonymsAsync(Ct) ?? [];
        var tgtSyn = await ctx.Probes.SynonymsAsync(Ct);

        pre = new Prereqs { SourceSynonyms = new HashSet<ObjectName>(srcSyn.Select(s => s.Key), src.NameComparer) };
        if (tgtSchemas is not null)
        {
            var have = new HashSet<string>(tgtSchemas, tgt.NameStringComparer);
            pre.Schemas.AddRange(srcSchemas.Where(s => !have.Contains(s)).Order(StringComparer.Ordinal));
        }
        if (tgtAlias is not null)
        {
            var have = new HashSet<ObjectName>(tgtAlias.Select(a => a.Key), tgt.NameComparer);
            pre.AliasTypes.AddRange(srcAlias.Where(a => !have.Contains(a.Key)).OrderBy(a => a.Key, ObjectName.Ordinal));
        }
        if (tgtSeq is not null)
        {
            var have = new HashSet<ObjectName>(tgtSeq.Select(a => a.Key), tgt.NameComparer);
            pre.Sequences.AddRange(srcSeq.Where(a => !have.Contains(a.Key)).OrderBy(a => a.Key, ObjectName.Ordinal));
        }
        if (tgtSyn is not null)
        {
            var have = new HashSet<ObjectName>(tgtSyn.Select(a => a.Key), tgt.NameComparer);
            pre.Synonyms.AddRange(srcSyn.Where(a => !have.Contains(a.Key)).OrderBy(a => a.Key, ObjectName.Ordinal));
        }

        foreach (var m in src.Modules.Values)
            if (m.OType is "FN" or "IF" or "TF" or "FS" or "FT" && !tgt.Modules.ContainsKey(m.Name))
                pre.MissingCallables.TryAdd(m.Obj.ToLowerInvariant(), (m.Name, true));
        foreach (var s in pre.Sequences)
            pre.MissingCallables.TryAdd(s.Name.ToLowerInvariant(), (s.Key, false));
    }

    /// <summary>
    /// The first function or sequence the target lacks that <paramref name="expression"/> uses (a default, a
    /// computed column or a check), or null. A function counts when its name is followed by <c>(</c>; a sequence
    /// wherever its name appears (<c>NEXT VALUE FOR [s].[seq]</c>). Comments and string literals are ignored.
    /// </summary>
    /// <param name="stillMissing">Whether a missing object still counts at the point the expression runs (e.g. not once 03 created it).</param>
    private ObjectName? UsesMissing(string? expression, Func<ObjectName, bool, bool>? stillMissing = null)
    {
        if (string.IsNullOrEmpty(expression) || pre.MissingCallables.Count == 0) return null;
        var tokens = TsqlTokens.Tokens(expression);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != 'I' || !pre.MissingCallables.TryGetValue(tokens[i].Text.ToLowerInvariant(), out var hit)) continue;
            if (hit.IsFunction && (i + 1 >= tokens.Count || tokens[i + 1].Text != "(")) continue;
            if (stillMissing is null || stillMissing(hit.Name, hit.IsFunction)) return hit.Name;
        }
        return null;
    }

    /// <summary>The source alias type of a column when the target lacks it, else null (system and CLR types: null).</summary>
    private AliasTypeRow? MissingAliasType(IColumnDef c) =>
        string.IsNullOrEmpty(c.TypeSchema)
            ? null
            : pre.AliasTypes.FirstOrDefault(a => tgt.NameComparer.Equals(a.Key, new ObjectName(c.TypeSchema, c.Dt)));
}
