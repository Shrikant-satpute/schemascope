using System.Security.Cryptography;
using System.Text;

namespace SchemaScope.Scripting;

/// <summary>How the validator should treat a written file.</summary>
public enum WrittenFileKind
{
    /// <summary>Plain DDL batches (01, 04, 05, 06, 02 types, 09 renames): parse only, QUOTED_IDENTIFIER ON.</summary>
    Statements,
    /// <summary>MODHDR + a raw module definition (03/07 objects, 02 recreate): the last batch must equal <see cref="WrittenFile.ExpectedModuleText"/>.</summary>
    RawModule,
    /// <summary>One or more <see cref="ScriptText.ExecBlock"/> batches (any _ALL.sql with modules, 08 objects/rollback): each evaluates to <see cref="WrittenFile.ExpectedModules"/> in order.</summary>
    ExecBlockModules,
    /// <summary>A PowerShell runner. Not validated.</summary>
    Runner,
    /// <summary>A markdown report or README. Not validated.</summary>
    Markdown,
}

/// <summary>The SET options a module was created under.</summary>
public sealed record ModuleSettings(bool AnsiNulls, bool QuotedIdentifier)
{
    public static ModuleSettings Of(ModuleRow m) => new(m.AnsiNulls, m.QuotedId);
}

/// <summary>A module whose exact text a file must reproduce.</summary>
/// <param name="Object">The module ("schema.name" via <see cref="ObjectName.ToString"/>).</param>
/// <param name="Text">The definition it must produce byte for byte (SOURCE text; the TARGET text for rollbacks and 02 recreates).</param>
public sealed record ExpectedModule(ObjectName Object, string Text, ModuleSettings Settings);

/// <summary>A file the run wrote, with what the validator must prove about it.</summary>
/// <param name="Folder">Top folder under the run root ("03_missing", "99_review", "" for root files).</param>
/// <param name="RelativePath">Path inside <paramref name="Folder"/> with '\' separators, e.g. "objects\001_x.sql".</param>
/// <param name="FullPath">Absolute path on disk.</param>
public sealed record WrittenFile(string Folder, string RelativePath, string FullPath, WrittenFileKind Kind)
{
    /// <summary>For <see cref="WrittenFileKind.RawModule"/>: the definition the last batch must equal.</summary>
    public string? ExpectedModuleText { get; init; }
    /// <summary>For module files: the SET options to parse under (QUOTED_IDENTIFIER decides how ScriptDom reads "...").</summary>
    public ModuleSettings? ExpectedSettings { get; init; }
    /// <summary>For <see cref="WrittenFileKind.ExecBlockModules"/>: the modules the file's exec blocks must rebuild, in order.</summary>
    public IReadOnlyList<ExpectedModule> ExpectedModules { get; init; } = [];
    /// <summary>The object a per-object file is about (null for _ALL and runners).</summary>
    public ObjectName? Object { get; init; }
    /// <summary>True for files under a rollback\ folder.</summary>
    public bool IsRollback { get; init; }
    /// <summary>
    /// SHA-256 (lower-case hex) of the exact bytes written, BOM included - taken from memory as the file was written,
    /// never read back, so a file changed on disk later cannot become the trusted version.
    /// </summary>
    public string? Sha256 { get; init; }
    /// <summary>"objects\…", "rollback\…" or a root file of the folder.</summary>
    public bool IsObjectFile => RelativePath.StartsWith("objects\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Writes every file of a run. All writes go under <see cref="Root"/> - a path that resolves
/// outside it is refused - and every file is recorded so the validator knows what to check.
/// .sql and .ps1 are UTF-8 with BOM (sqlcmd -f 65001 and PowerShell 5 both need it to read
/// non-ASCII correctly; .sql without one for psql and mysql); markdown is UTF-8 without BOM. Text is written exactly as given: no
/// newline translation, so CRLF headers and bare-LF DDL bodies reach disk byte for byte.
/// </summary>
public sealed class ScriptWriter
{
    // throwOnInvalid: a lone surrogate in a module body must fail loudly, not turn into U+FFFD
    private static readonly UTF8Encoding Bom = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly List<WrittenFile> files = [];
    private readonly UTF8Encoding sqlEncoding;
    private readonly string rootWithSep;

    /// <param name="root">The run root; created if missing. Must be fully qualified.</param>
    public ScriptWriter(string root) : this(root, sqlBom: true) { }

    /// <param name="root">The run root; created if missing. Must be fully qualified.</param>
    /// <param name="sqlBom">
    /// Whether .sql files start with a BOM: yes for sqlcmd; no for psql and mysql, which read a BOM as part of the first
    /// statement. The SHA-256 of each file is of the bytes written either way.
    /// </param>
    public ScriptWriter(string root, bool sqlBom)
    {
        sqlEncoding = sqlBom ? Bom : NoBom;
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("The output folder must be a full path.", nameof(root));
        var full = Path.GetFullPath(root);
        // A drive or share root ("D:\") would trim to "D:", and Path.Combine("D:", x) is relative to D:'s current directory.
        if (Path.GetPathRoot(full) is { } pr && string.Equals(Path.TrimEndingDirectorySeparator(pr), Path.TrimEndingDirectorySeparator(full), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The run folder cannot be a drive root; use a folder inside it.", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(full);
        rootWithSep = Root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Root);
    }

    /// <summary>The run root (absolute, no trailing separator, never a drive root).</summary>
    public string Root { get; }

    /// <summary>Every file written so far, in write order (a re-written path keeps one entry, the latest).</summary>
    public IReadOnlyList<WrittenFile> Files { get { lock (files) return files.ToList(); } }

    /// <summary>Files under one top folder.</summary>
    public IReadOnlyList<WrittenFile> FilesIn(string folder) =>
        Files.Where(f => string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Number of <c>objects\*.sql</c> files written in a folder - what <see cref="FolderResult.Files"/> reports.</summary>
    public int ObjectFileCount(string folder) =>
        FilesIn(folder).Count(f => f.IsObjectFile && f.RelativePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes a .sql file (UTF-8 with BOM) at <c>Root\folder\relativePath</c> and records it.
    /// </summary>
    /// <param name="folder">Top folder, e.g. "05_constraints".</param>
    /// <param name="relativePath">e.g. "objects\001_default_X.sql", "_ALL.sql", "rollback\_ALL.sql". '/' is accepted.</param>
    /// <param name="text">Exact content.</param>
    /// <param name="kind">What the validator checks.</param>
    /// <param name="obj">The object a per-object file is about.</param>
    /// <param name="expectedModuleText">RawModule: the definition the last batch must equal.</param>
    /// <param name="settings">Module SET options (parse QUOTED_IDENTIFIER per these).</param>
    /// <param name="expectedModules">ExecBlockModules: modules to rebuild, in order.</param>
    public WrittenFile WriteSql(
        string folder, string relativePath, string text, WrittenFileKind kind = WrittenFileKind.Statements,
        ObjectName? obj = null, string? expectedModuleText = null, ModuleSettings? settings = null,
        IReadOnlyList<ExpectedModule>? expectedModules = null)
    {
        var rel = NormalizeRelative(relativePath);
        var f = new WrittenFile(folder, rel, Resolve(folder, rel), kind)
        {
            Object = obj,
            ExpectedModuleText = expectedModuleText,
            ExpectedSettings = settings,
            ExpectedModules = expectedModules ?? [],
            IsRollback = rel.StartsWith("rollback\\", StringComparison.OrdinalIgnoreCase),
        };
        return Save(f, text, sqlEncoding);
    }

    /// <summary>Writes the folder's <c>run_all.ps1</c> (<see cref="ScriptText.Runner"/>, BOM).</summary>
    public WrittenFile WriteRunner(string folder, string server) => WriteRunnerText(folder, ScriptText.Runner(server));

    /// <summary>Writes the folder's <c>run_all.ps1</c> with the given text (another engine's client; BOM, as PowerShell 5 needs).</summary>
    public WrittenFile WriteRunnerText(string folder, string text)
    {
        var f = new WrittenFile(folder, "run_all.ps1", Resolve(folder, "run_all.ps1"), WrittenFileKind.Runner);
        return Save(f, text, Bom);
    }

    /// <summary>Writes a markdown file (UTF-8, no BOM, LF as given), e.g. folder "99_review", "flow_safety.md"; folder "" for README.md.</summary>
    public WrittenFile WriteMarkdown(string folder, string relativePath, string text)
    {
        var rel = NormalizeRelative(relativePath);
        var f = new WrittenFile(folder, rel, Resolve(folder, rel), WrittenFileKind.Markdown);
        return Save(f, text, NoBom);
    }

    /// <summary>Writes a file at the run root (RUN.ps1 with BOM). <paramref name="kind"/> is normally Runner.</summary>
    public WrittenFile WriteRootFile(string fileName, string text, bool bom, WrittenFileKind kind)
    {
        var rel = NormalizeRelative(fileName);
        var f = new WrittenFile("", rel, Resolve("", rel), kind);
        return Save(f, text, bom ? Bom : NoBom);
    }

    /// <summary>Reads a written file back exactly as the validator should see it (BOM removed, no newline translation).</summary>
    public static string ReadBack(string fullPath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return NoBom.GetString(bytes, skip, bytes.Length - skip);
    }

    /// <summary>Resolves a path under the root; throws when it would land outside it.</summary>
    public string Resolve(string folder, string relativePath)
    {
        if (Path.IsPathRooted(folder) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"Refusing to write a rooted path: {folder}\\{relativePath}");
        var full = Path.GetFullPath(Path.Combine(Root, folder ?? "", relativePath));
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to write outside the output folder: {folder}\\{relativePath}");
        return full;
    }

    private WrittenFile Save(WrittenFile f, string text, UTF8Encoding enc)
    {
        byte[] body;
        try
        {
            body = enc.GetBytes(text ?? "");
        }
        catch (EncoderFallbackException e)
        {
            // name the file (and so the object) instead of failing the run with an anonymous index
            throw new IOException(
                $"{Path.Combine(f.Folder, f.RelativePath)}: the text holds an unpaired UTF-16 surrogate, which cannot be written as UTF-8 " +
                "(use ScriptText.ExecBlock for such a module, or hold it back - ScriptText.RawModuleProblem says which).", e);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(f.FullPath)!);
        var pre = enc.GetPreamble();
        using (var fs = new FileStream(f.FullPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(pre);
            fs.Write(body);
        }
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            sha.AppendData(pre);
            sha.AppendData(body);
            f = f with { Sha256 = Convert.ToHexStringLower(sha.GetHashAndReset()) };
        }
        lock (files)
        {
            files.RemoveAll(x => string.Equals(x.FullPath, f.FullPath, StringComparison.OrdinalIgnoreCase));
            files.Add(f);
        }
        return f;
    }

    private static string NormalizeRelative(string p) => (p ?? "").Replace('/', '\\').TrimStart('\\');
}
