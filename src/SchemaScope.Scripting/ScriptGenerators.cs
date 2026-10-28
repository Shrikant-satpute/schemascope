using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Scripting;

/// <summary>Everything a "Create scripts" run gets, whichever engine pair writes it.</summary>
/// <param name="SourceSnap">The compare's source snapshot (a generator that reads its own catalogs may ignore it).</param>
/// <param name="FormattingCandidates">"schema.name" of modules the compare marked FormattingOnly for this target.</param>
/// <param name="DifferentCandidates">"schema.name" of modules the compare marked Different for this target, with its line counts.</param>
/// <param name="CompareOptions">The options the compare ran with (what it ignored); null = the defaults.</param>
public sealed record ScriptRequestContext(ScriptEndpoint Source, ScriptEndpoint Target, SchemaSnapshot? SourceSnap,
    SchemaSnapshot? TargetSnap, CompareResult? Result, int TargetIndex, ScriptOptions Options,
    IReadOnlySet<string> FormattingCandidates, IReadOnlyDictionary<string, LineChange> DifferentCandidates,
    CompareOptions? CompareOptions = null);

/// <summary>Writes the scripts that make one target look like its source, for one engine pair.</summary>
public interface IScriptGenerator
{
    Task<ScriptOutcome> GenerateAsync(ScriptRequestContext req, CancellationToken ct, IProgress<ScriptProgress>? p);
}

/// <summary>The generator for a source/target engine pair; null when this version has none (the route answers 400).</summary>
public interface IScriptGeneratorFactory
{
    IScriptGenerator? For(DatabaseEngine source, DatabaseEngine target);
}

/// <summary>
/// What a read-only probe of the target found. NotApplicable: the check has no counterpart on this engine (SQL Server's
/// memory-optimized tables on PostgreSQL), so it is never a reason to hold anything back. NotChecked: it could have
/// mattered but did not run (switched off, timed out, refused); what it guarded is said to be unchecked.
/// </summary>
public enum ProbeResultKind { Ok, NotChecked, NotApplicable }

/// <summary>Splits a script file into the statements or batches a runner sends one by one.</summary>
public interface IStatementSplitter
{
    IReadOnlyList<SqlBatch> Split(string text);
}

/// <summary>sqlcmd's rule: batches end at a GO line (<see cref="ScriptText.SplitBatches"/>).</summary>
public sealed class SqlServerStatementSplitter : IStatementSplitter
{
    public static SqlServerStatementSplitter Instance { get; } = new();

    public IReadOnlyList<SqlBatch> Split(string text) => ScriptText.SplitBatches(text);
}

/// <summary>SQL Server to SQL Server: the untouched <see cref="ScriptGenerator"/>, called exactly as before.</summary>
public sealed class SqlServerScriptGenerator(ScriptGenerator? inner = null) : IScriptGenerator
{
    public ScriptGenerator Inner { get; } = inner ?? new ScriptGenerator();

    public Task<ScriptOutcome> GenerateAsync(ScriptRequestContext req, CancellationToken ct, IProgress<ScriptProgress>? p) =>
        Inner.GenerateAsync(req.Source, req.Target, req.Options, req.FormattingCandidates, ct, p, req.DifferentCandidates);
}

/// <summary>The pairs this version scripts: SQL Server to SQL Server only.</summary>
public sealed class ScriptGeneratorFactory : IScriptGeneratorFactory
{
    public IScriptGenerator? For(DatabaseEngine source, DatabaseEngine target) =>
        source == DatabaseEngine.SqlServer && target == DatabaseEngine.SqlServer ? new SqlServerScriptGenerator() : null;
}
