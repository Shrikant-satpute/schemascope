namespace SchemaScope.Scripting;

/// <summary>
/// Folders 01–06 (port of generate.py) plus their 99_review entries (<c>not_applied_constraints.md</c>,
/// <c>quarantined_not_applied.md</c>, <c>not_applied.md</c>). Always runs: it fills <see cref="ScriptContext.Facts"/>
/// for every folder, selected or not - 05 needs 01's inline defaults and 03's new tables, flow_safety and the
/// validator need all of it - and writes only the folders whose <see cref="ScriptSection"/> is selected.
/// </summary>
/// <remarks>
/// The work is done by <see cref="CoreRun"/>, one instance per run, so the many lookups the folders share
/// (new tables, blocked columns, target names) are built once and never leak between runs.
/// </remarks>
public sealed class CoreFoldersGenerator : IFolderGenerator
{
    /// <summary>New columns on tables both sides have.</summary>
    public const string ColumnsFolder = "01_columns";
    /// <summary>Table types whose columns drifted (opt-in, drops and recreates target modules).</summary>
    public const string TableTypesFolder = "02_tabletypes";
    /// <summary>Table types, tables, functions, views and procedures the target lacks.</summary>
    public const string MissingFolder = "03_missing";
    /// <summary>Widen-only / relax-only column changes.</summary>
    public const string ColumnTypesFolder = "04_columntypes";
    /// <summary>Defaults, checks, keys and foreign keys the target lacks.</summary>
    public const string ConstraintsFolder = "05_constraints";
    /// <summary>Indexes the target lacks.</summary>
    public const string IndexesFolder = "06_indexes";

    public Task<IReadOnlyList<FolderResult>> GenerateAsync(ScriptContext ctx) => new CoreRun(ctx).RunAsync();
}
