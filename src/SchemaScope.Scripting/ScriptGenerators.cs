using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Scripting;

/// <summary>Everything a "Create scripts" run gets, whichever engine pair writes it.</summary>
/// <param name="SourceSnap">The compare's source snapshot (a generator that reads its own catalogs may ignore it).</param>
/// <param name="FormattingCandidates">"schema.name" of modules the compare marked FormattingOnly for this target.</param>
/// <param name="DifferentCandidates">"schema.name" of modules the compare marked Different for this target, with its line counts.</param>
public sealed record ScriptRequestContext(ScriptEndpoint Source, ScriptEndpoint Target, SchemaSnapshot? SourceSnap,
    SchemaSnapshot? TargetSnap, CompareResult? Result, int TargetIndex, ScriptOptions Options,
    IReadOnlySet<string> FormattingCandidates, IReadOnlyDictionary<string, LineChange> DifferentCandidates);

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
