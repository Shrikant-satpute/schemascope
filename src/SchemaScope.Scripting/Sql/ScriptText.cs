using System.Text;
using System.Text.RegularExpressions;

namespace SchemaScope.Scripting;

/// <summary>One batch of a script file, split on GO lines.</summary>
/// <param name="Text">The batch text, line endings kept, without the GO line.</param>
/// <param name="StartLine">1-based line number of the batch's first line in the file, for error messages.</param>
public sealed record SqlBatch(string Text, int StartLine);

/// <summary>
/// Text helpers of generate.py / generate_overwrite_helpers.py / check_scripts.py: file headers,
/// <c>safe</c>/<c>lit</c>/<c>norm</c>, <see cref="ExecBlock"/> and its offline inverse
/// <see cref="EvaluateExecBlock"/>, GO splitting, and the PowerShell runners.
/// </summary>
public static partial class ScriptText
{
    /// <summary>generate.py HDR.</summary>
    public const string Hdr = "SET ANSI_NULLS ON\r\nGO\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n";

    /// <summary>generate.py HDR_TBL = HDR + ANSI_PADDING ON.</summary>
    public const string HdrTbl = Hdr + "SET ANSI_PADDING ON\r\nGO\r\n";

    /// <summary>Statement terminator used after every DDL statement: CRLF, GO, CRLF.</summary>
    public const string Go = "\r\nGO\r\n";

    /// <summary>The per-module header: the module's own ANSI_NULLS and QUOTED_IDENTIFIER, each in its own batch.</summary>
    public static string ModHdr(bool ansiNulls, bool quotedId) =>
        $"SET ANSI_NULLS {(ansiNulls ? "ON" : "OFF")}\r\nGO\r\nSET QUOTED_IDENTIFIER {(quotedId ? "ON" : "OFF")}\r\nGO\r\n";

    /// <summary><see cref="ModHdr(bool, bool)"/> for a module row.</summary>
    public static string ModHdr(ModuleRow m) => ModHdr(m.AnsiNulls, m.QuotedId);

    /// <summary>generate.py <c>safe</c>: every character outside <c>[A-Za-z0-9_.-]</c> becomes <c>_</c>. Used for file names.</summary>
    public static string Safe(string name) => UnsafeRx().Replace(name ?? "", "_");

    /// <summary>generate.py <c>lit</c>: <c>N'...'</c> with quotes doubled.</summary>
    public static string Lit(string s) => "N'" + (s ?? "").Replace("'", "''") + "'";

    /// <summary>generate.py <c>norm</c>: whitespace runs to one space, trimmed, lower-cased. Used in the 05/06 identity keys.</summary>
    public static string Norm(string? s) => WsRx().Replace(s ?? "", " ").Trim().ToLowerInvariant();

    /// <summary><c>\n</c> → <c>\r\n</c>, as the Python did for every runner (<c>RUNNER.replace('\n', '\r\n')</c>).</summary>
    public static string Crlf(string text) => text.Replace("\n", "\r\n");

    /// <summary>What SSMS does to a file it opens: every line ending becomes CRLF (<c>\r\n</c>→<c>\n</c>→<c>\r\n</c>).</summary>
    public static string SsmsRewrite(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    // ------------------------------------------------------------------ exec_block

    /// <summary>
    /// A CREATE that survives any editor (generate.py <c>exec_block</c>). sys.sql_modules stores the
    /// batch text verbatim, so the definition is shipped inside an N'' literal and its exact line
    /// endings are rebuilt at run time.
    /// <list type="number">
    /// <item>no CRLF anywhere: one literal, every CR stripped at run time;</item>
    /// <item>CRLF only: one literal, CRs stripped, then every LF turned back into CRLF;</item>
    /// <item>mixed: one SET per CRLF-separated piece, each appended to the nvarchar(max) variable on
    /// its own (concatenating literals in one expression would truncate at 4000 characters).</item>
    /// </list>
    /// Output is byte-identical to the Python for every definition the Python handled correctly.
    /// When the Python output would be wrong, a fourth form is used: one SET per line, with the exact
    /// separator (CR, LF or CRLF) appended between lines, so no raw line of the definition starts a line of
    /// the file and no REPLACE runs. That happens when:
    /// <list type="bullet">
    /// <item>the definition holds a lone CR (every Python branch drops it);</item>
    /// <item>a line of the produced batch would be read by sqlcmd/SSMS as a GO separator or a sqlcmd command
    /// (<c>:r</c>, <c>!!</c>, <c>exit</c>) even inside the literal;</item>
    /// <item>branch 2 would apply and a LF is followed by a non-ASCII character
    /// (<see cref="HasNonAsciiAfterLf"/>): REPLACE compares under the target's collation, which treats LF plus a
    /// combining mark as one unit - an accent-insensitive collation deletes the mark, an accent-sensitive one
    /// leaves the LF without its CR (both measured);</item>
    /// <item>the definition holds U+0000 (sqlcmd stops reading the line there) or an unpaired surrogate (cannot be
    /// written as UTF-8): the per-line form emits them as <c>NCHAR(0)</c> and <c>CONVERT(nchar(1), 0xLLHH)</c>,
    /// so the file itself never contains them.</item>
    /// </list>
    /// </summary>
    public static string ExecBlock(string defn)
    {
        defn ??= "";
        if (!HasLoneCr(defn) && !HasNul(defn) && !HasLoneSurrogate(defn) &&
            !(PythonBranch(defn) == 2 && HasNonAsciiAfterLf(defn)))
        {
            var py = ExecBlockPython(defn);
            if (!HasBatchBreakingLine(py)) return py;
        }
        return ExecBlockPerLine(defn);
    }

    /// <summary>Which of generate.py exec_block's branches a definition takes: 1 (no CRLF), 2 (CRLF only) or 3 (mixed).</summary>
    public static int PythonBranch(string defn)
    {
        var parts = (defn ?? "").Split("\r\n");
        return parts.Length == 1 ? 1 : parts.All(p => !p.Contains('\n')) ? 2 : 3;
    }

    /// <summary>The three branches of generate.py exec_block, verbatim. <see cref="ExecBlock"/> is the safe entry point.</summary>
    public static string ExecBlockPython(string defn)
    {
        var parts = defn.Split("\r\n");
        if (parts.Length == 1)
            return "DECLARE @sql nvarchar(max) = " + Lit(defn) + ";\r\n" +
                   "SET @sql = REPLACE(@sql, NCHAR(13), N'');\r\n" +
                   "EXEC (@sql);\r\n";
        if (parts.All(p => !p.Contains('\n')))
            return "DECLARE @sql nvarchar(max) = " + Lit(defn) + ";\r\n" +
                   "SET @sql = REPLACE(REPLACE(@sql, NCHAR(13), N''), NCHAR(10), NCHAR(13) + NCHAR(10));\r\n" +
                   "EXEC (@sql);\r\n";
        var sb = new StringBuilder("DECLARE @sql nvarchar(max) = N'';\r\n");
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append("SET @sql = @sql + NCHAR(13) + NCHAR(10);\r\n");
            sb.Append("SET @sql = @sql + REPLACE(").Append(Lit(parts[i])).Append(", NCHAR(13), N'');\r\n");
        }
        sb.Append("EXEC (@sql);\r\n");
        return sb.ToString();
    }

    private static string ExecBlockPerLine(string defn)
    {
        var sb = new StringBuilder("DECLARE @sql nvarchar(max) = N'';\r\n");
        var start = 0;
        for (var i = 0; i <= defn.Length; i++)
        {
            if (i < defn.Length && !IsPerLineSeparator(defn, i)) continue;
            sb.Append("SET @sql = @sql + ").Append(Lit(defn[start..i])).Append(";\r\n");
            if (i == defn.Length) break;
            var c = defn[i];
            string sep;
            if (c == '\r' && i + 1 < defn.Length && defn[i + 1] == '\n') { sep = "NCHAR(13) + NCHAR(10)"; i++; }
            else if (c == '\r') sep = "NCHAR(13)";
            else if (c == '\n') sep = "NCHAR(10)";
            else if (c == '\0') sep = "NCHAR(0)";
            // an unpaired surrogate: NCHAR(55296..) is NULL under an _SC database collation, a binary convert never is
            else sep = $"CONVERT(nchar(1), 0x{c & 0xFF:X2}{c >> 8:X2})";
            sb.Append("SET @sql = @sql + ").Append(sep).Append(";\r\n");
            start = i + 1;
        }
        sb.Append("EXEC (@sql);\r\n");
        return sb.ToString();
    }

    // CR, LF, NUL and unpaired surrogates are kept out of the per-line literals.
    private static bool IsPerLineSeparator(string s, int i) =>
        s[i] is '\r' or '\n' or '\0' || IsLoneSurrogateAt(s, i);

    private static bool IsLoneSurrogateAt(string s, int i) =>
        char.IsHighSurrogate(s[i]) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])
        : char.IsLowSurrogate(s[i]) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));

    /// <summary>True when the text holds a CR that is not followed by LF.</summary>
    public static bool HasLoneCr(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) return true;
        return false;
    }

    /// <summary>True when the text holds U+0000. sqlcmd stops reading a line at a NUL, so a raw file holding one is truncated.</summary>
    public static bool HasNul(string text) => (text ?? "").Contains('\0');

    /// <summary>True when the text holds an unpaired UTF-16 surrogate, which cannot be written as UTF-8 (<see cref="ScriptWriter"/> refuses it).</summary>
    public static bool HasLoneSurrogate(string text)
    {
        text ??= "";
        for (var i = 0; i < text.Length; i++)
            if (char.IsSurrogate(text[i]) && IsLoneSurrogateAt(text, i)) return true;
        return false;
    }

    /// <summary>
    /// True when some LF is directly followed by a non-ASCII character. A collation-aware REPLACE of
    /// <c>NCHAR(10)</c> may then match LF and the following combining mark as one unit (or not at all),
    /// so exec_block branch 2 is not exact; <see cref="ExecBlock"/> avoids it.
    /// </summary>
    public static bool HasNonAsciiAfterLf(string text)
    {
        text ??= "";
        for (var i = text.IndexOf('\n'); i >= 0 && i + 1 < text.Length; i = text.IndexOf('\n', i + 1))
            if (text[i + 1] > '\x7F') return true;
        return false;
    }

    /// <summary>
    /// Why a module definition cannot go into a raw per-object file (MODHDR + definition + GO) byte-exactly,
    /// or null when it can: a NUL (sqlcmd truncates the line) or an unpaired surrogate (not encodable as UTF-8).
    /// Generators write such a module only through <see cref="ExecBlock"/>, or hold it back for review.
    /// GO-shaped lines inside the definition are a separate, SSMS-only concern (<see cref="HasBatchBreakingLine"/>).
    /// </summary>
    public static string? RawModuleProblem(string? definition) =>
        definition is null ? "no definition (encrypted or CLR)"
        : HasNul(definition) ? "the definition contains a NUL character (U+0000), which sqlcmd cannot read from a file"
        : HasLoneSurrogate(definition) ? "the definition contains an unpaired UTF-16 surrogate, which cannot be written as UTF-8"
        : null;

    /// <summary>
    /// True when some line of <paramref name="text"/> (split on CR, LF or CRLF) could be taken as a batch
    /// separator (<c>GO</c>, <c>GO 5</c>, <c>GO -- x</c>) or a sqlcmd command (<c>:r</c>, <c>:setvar</c>, <c>!!</c>,
    /// <c>exit</c>, <c>quit</c>, <c>reset</c>, <c>ed</c>), wherever it sits - inside a comment or a string included.
    /// Deliberately conservative: SSMS splits on such a GO line regardless of strings and comments; sqlcmd 16
    /// (measured) does not, so for a raw per-object file run by run_all.ps1 a hit here is an SSMS-only risk.
    /// </summary>
    public static bool HasBatchBreakingLine(string text)
    {
        foreach (var line in LineBreakRx().Split(text))
            if (BreakingLineRx().IsMatch(line)) return true;
        return false;
    }

    // ------------------------------------------------------------------ offline evaluation

    /// <summary>
    /// What <c>EXEC (@sql)</c> would run for a batch <see cref="ExecBlock"/> produced, computed
    /// offline by interpreting the batch's statements the way SQL Server does: <c>DECLARE @sql nvarchar(max) = e;</c>,
    /// <c>SET @sql = e;</c> and <c>EXEC (@sql);</c>, where <c>e</c> is a <c>+</c> chain of N'' literals, <c>@sql</c>,
    /// <c>NCHAR(n)</c>, <c>CONVERT(nchar(1), 0xLLHH)</c> and <c>REPLACE(e, e, e)</c>. Types are tracked too: a literal
    /// over 4000 characters is nvarchar(max), and a non-max concatenation or REPLACE result is cut at 4000 characters,
    /// exactly the truncation trap branch 3 exists to avoid. REPLACE is modelled ordinally, so it refuses (returns null)
    /// when a match is followed by a non-ASCII character - SQL Server's collation-aware REPLACE may then do something
    /// else, and "exact" would be a lie. Text before the DECLARE (the 08 DROP wrapper) is ignored.
    /// Returns null when the batch has no <c>DECLARE @sql nvarchar(max)</c> or does not follow that grammar.
    /// </summary>
    public static string? EvaluateExecBlock(string batch) =>
        TryEvaluateExecBlock(batch, out var value, out _) ? value : null;

    /// <summary><see cref="EvaluateExecBlock"/> with the reason when the batch cannot be evaluated.</summary>
    public static bool TryEvaluateExecBlock(string batch, out string? value, out string? error)
    {
        value = null;
        var at = batch.IndexOf("DECLARE @sql nvarchar(max)", StringComparison.Ordinal);
        if (at < 0) { error = "no DECLARE @sql nvarchar(max)"; return false; }
        try
        {
            value = new ExecBlockInterpreter(batch, at).Run();
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>check_scripts.py <c>literals</c>: every <c>N'...'</c> literal in order, with <c>''</c> unescaped.</summary>
    public static List<string> Literals(string batch)
    {
        var output = new List<string>();
        int i = 0, n = batch.Length;
        while (i < n)
        {
            if (batch[i] == 'N' && i + 1 < n && batch[i + 1] == '\'')
            {
                int j = i + 2;
                var buf = new StringBuilder();
                while (j < n)
                {
                    if (batch[j] == '\'')
                    {
                        if (j + 1 < n && batch[j + 1] == '\'') { buf.Append('\''); j += 2; continue; }
                        break;
                    }
                    buf.Append(batch[j]); j++;
                }
                output.Add(buf.ToString());
                i = j + 1;
            }
            else i++;
        }
        return output;
    }

    /// <summary>
    /// check_scripts.py <c>evaluate</c>, verbatim: the Python's pattern-matching shortcut. It understands
    /// branches 1–3 (and a legacy form) but not the per-line form; kept for parity checks.
    /// <see cref="EvaluateExecBlock"/> is the exact one.
    /// </summary>
    public static string? EvaluateLikePython(string batch)
    {
        if (batch.Contains("SET @sql = @sql +", StringComparison.Ordinal))
        {
            var l = Literals(batch);
            return string.Join("\r\n", l.Where((_, idx) => idx >= 1 && (idx - 1) % 2 == 0).Select(p => p.Replace("\r", "")));
        }
        var lits = Literals(batch);
        if (lits.Count == 0) return null;
        if (batch.Contains("NCHAR(10), NCHAR(13) + NCHAR(10))", StringComparison.Ordinal))
            return lits[0].Replace("\r", "").Replace("\n", "\r\n");
        if (batch.Contains("+ NCHAR(13) + NCHAR(10) +", StringComparison.Ordinal))
            return string.Join("\r\n", lits.Where((_, idx) => idx % 2 == 0).Select(p => p.Replace("\r", "")));
        return lits[0].Replace("\r", "");
    }

    /// <summary>A tiny interpreter for exactly the statements <see cref="ExecBlock"/> emits.</summary>
    private sealed class ExecBlockInterpreter(string s, int pos)
    {
        private const int NonMaxLimit = 4000;
        private int p = pos;
        private (string Text, bool Max) sql = ("", true);
        private bool declared;

        public string Run()
        {
            while (true)
            {
                Ws();
                if (Eat("DECLARE"))
                {
                    if (declared) throw Fail("second DECLARE");
                    Need("@sql"); Need("nvarchar"); Need("("); Need("max"); Need(")"); Need("=");
                    sql = (Expr().Text, true); Need(";");
                    declared = true;
                }
                else if (Eat("SET"))
                {
                    if (!declared) throw Fail("SET before DECLARE");
                    Need("@sql"); Need("=");
                    sql = (Expr().Text, true); Need(";");
                }
                else if (Eat("EXEC"))
                {
                    if (!declared) throw Fail("EXEC before DECLARE");
                    Need("("); Need("@sql"); Need(")"); Need(";");
                    return sql.Text;
                }
                else throw Fail("unexpected statement");
            }
        }

        private (string Text, bool Max) Expr()
        {
            var v = Term();
            while (true)
            {
                Ws();
                if (!Eat("+")) return v;
                var r = Term();
                var max = v.Max || r.Max;
                var text = v.Text + r.Text;
                v = (max || text.Length <= NonMaxLimit ? text : text[..NonMaxLimit], max);
            }
        }

        private (string Text, bool Max) Term()
        {
            Ws();
            if (p + 1 < s.Length && s[p] == 'N' && s[p + 1] == '\'')
            {
                p += 2;
                var buf = new StringBuilder();
                while (true)
                {
                    if (p >= s.Length) throw Fail("unterminated literal");
                    if (s[p] == '\'')
                    {
                        if (p + 1 < s.Length && s[p + 1] == '\'') { buf.Append('\''); p += 2; continue; }
                        p++;
                        break;
                    }
                    buf.Append(s[p++]);
                }
                return (buf.ToString(), buf.Length > NonMaxLimit);
            }
            if (Eat("@sql")) return (sql.Text, true);
            if (Eat("NCHAR"))
            {
                Need("(");
                Ws();
                var st = p;
                while (p < s.Length && char.IsAsciiDigit(s[p])) p++;
                if (st == p) throw Fail("NCHAR needs a number");
                var code = int.Parse(s.AsSpan(st, p - st));
                Need(")");
                return (((char)code).ToString(), false);
            }
            if (Eat("CONVERT"))
            {
                Need("("); Need("nchar"); Need("("); Need("1"); Need(")"); Need(",");
                Ws();
                if (p + 6 > s.Length || s[p] != '0' || (s[p + 1] is not ('x' or 'X'))) throw Fail("CONVERT needs 0xLLHH");
                var hex = s.Substring(p + 2, 4);
                if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    throw Fail("CONVERT needs 0xLLHH");
                p += 6;
                Need(")");
                // binary -> nchar reads UTF-16 little-endian: the first byte is the low one
                return (((char)(((v & 0xFF) << 8) | (v >> 8))).ToString(), false);
            }
            if (Eat("REPLACE"))
            {
                Need("(");
                var a = Expr(); Need(",");
                var b = Expr(); Need(",");
                var c = Expr(); Need(")");
                if (b.Text.Length > 0 && FollowedByNonAscii(a.Text, b.Text))
                    throw Fail("REPLACE result depends on the collation (a match is followed by a non-ASCII character, e.g. a combining mark)");
                var text = b.Text.Length == 0 ? a.Text : a.Text.Replace(b.Text, c.Text, StringComparison.Ordinal);
                return (a.Max || text.Length <= NonMaxLimit ? text : text[..NonMaxLimit], a.Max);
            }
            throw Fail("unexpected term");
        }

        // SQL Server's REPLACE compares under the collation, not by code unit: a combining mark (or another
        // character a collation may fold into the match) right after an occurrence changes what matches.
        // The ordinal model is only trustworthy when every occurrence is followed by ASCII or nothing.
        private static bool FollowedByNonAscii(string text, string find)
        {
            for (var i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + 1, StringComparison.Ordinal))
            {
                var next = i + find.Length;
                if (next < text.Length && text[next] > '\x7F') return true;
            }
            return false;
        }

        private void Ws() { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

        private bool Eat(string word)
        {
            Ws();
            if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0) return false;
            var end = p + word.Length;
            // a keyword must not run into the next identifier character
            if (char.IsLetterOrDigit(word[^1]) && end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_')) return false;
            p = end;
            return true;
        }

        private void Need(string word)
        {
            if (!Eat(word)) throw Fail($"expected '{word}'");
        }

        private FormatException Fail(string what)
        {
            var line = 1;
            for (var i = 0; i < p && i < s.Length; i++) if (s[i] == '\n') line++;
            return new FormatException($"exec block, line {line}: {what}");
        }
    }

    // ------------------------------------------------------------------ batches

    /// <summary>
    /// Splits a script on GO lines: a line that is only <c>GO</c>, optionally with a repeat count and a
    /// trailing <c>-- comment</c>, any case, surrounding blanks allowed. This is SSMS's rule and is
    /// conservative: it splits on such a line regardless of strings and comments, whereas sqlcmd 16 (measured)
    /// does not split on a GO line inside a string, a comment or brackets. So a raw module file whose definition
    /// holds a GO line fails this split although sqlcmd would create it exactly - report that as "SSMS-only",
    /// not as a sqlcmd failure. Line endings inside each batch are kept; whitespace-only batches are dropped
    /// (check_scripts.py <c>batches</c>). Lines end at LF; a lone CR does not end a line (as in sqlcmd, which
    /// sends a lone CR inside the batch text unchanged).
    /// </summary>
    public static List<SqlBatch> SplitBatches(string text)
    {
        var output = new List<SqlBatch>();
        var cur = new StringBuilder();
        var line = 1;
        var batchStart = 1;
        var i = 0;
        while (i < text.Length)
        {
            var nl = text.IndexOf('\n', i);
            var end = nl < 0 ? text.Length : nl + 1;
            var raw = text.AsSpan(i, end - i);
            var bare = raw.TrimEnd("\r\n");
            if (GoLineRx().IsMatch(bare))
            {
                Flush(output, cur, batchStart);
                batchStart = line + 1;
            }
            else
            {
                if (cur.Length == 0) batchStart = line;
                cur.Append(raw);
            }
            i = end;
            line++;
        }
        Flush(output, cur, batchStart);
        return output;

        static void Flush(List<SqlBatch> output, StringBuilder cur, int start)
        {
            var b = cur.ToString();
            if (!string.IsNullOrWhiteSpace(b)) output.Add(new SqlBatch(b, start));
            cur.Clear();
        }
    }

    // ------------------------------------------------------------------ runners

    /// <summary>
    /// The per-folder <c>run_all.ps1</c> (generate.py RUNNER), CRLF. Differences from the Python: the
    /// default <c>$Server</c> is the target's server, sqlcmd gets <c>-x</c> so a <c>$(name)</c> inside a
    /// module body is not expanded as a sqlcmd variable, the files are listed with <c>-LiteralPath</c> (with
    /// <c>-Path</c> a folder such as <c>Deploy [C-Tech]</c> is a wildcard that matches nothing, and the folder
    /// "ran" 0 files with exit code 0), and finding no file at all is a failure. The template's own line endings are normalised
    /// first, so the output is CRLF whether this .cs file is saved with LF or CRLF (Python read its source
    /// with universal newlines and never had the problem).
    /// </summary>
    public static string Runner(string server) => Crlf(RunnerTemplate(server).ReplaceLineEndings("\n"));

    private static string RunnerTemplate(string server) => $$"""
        param(
            [string]$Server = {{PsQuote(server)}},
            [Parameter(Mandatory=$true)][string]$Database,
            [switch]$ContinueOnError
        )
        $dir = Join-Path $PSScriptRoot "objects"
        $files = @(Get-ChildItem -LiteralPath $dir -Filter *.sql | Sort-Object Name)
        if ($files.Count -eq 0) {
            Write-Host "No .sql files found in $dir - nothing was run." -ForegroundColor Red
            exit 1
        }
        $fail = 0
        foreach ($f in $files) {
            & sqlcmd -S $Server -E -d $Database -b -x -f 65001 -i $f.FullName
            if ($LASTEXITCODE -ne 0) {
                Write-Host "FAILED  $($f.Name)" -ForegroundColor Red
                $fail++
                if (-not $ContinueOnError) { Write-Host "stopped - re-run with -ContinueOnError to skip past failures"; break }
            } else {
                Write-Host "ok      $($f.Name)"
            }
        }
        Write-Host ""
        Write-Host "$($files.Count) files, $fail failed"
        exit $fail

        """;

    /// <summary>
    /// The root <c>RUN.ps1</c> (generate.py ROOT_RUNNER), CRLF: runs <paramref name="folders"/> in the given
    /// order, each with <c>-ContinueOnError</c>. When <paramref name="foldersWithTableTypes"/> is given, a
    /// <c>-WithTableTypes</c> switch runs that list instead (the same folders plus 02, in run order).
    /// No python verify line; the total failure count is the exit code.
    /// </summary>
    public static string RootRunner(string server, IReadOnlyList<string> folders, IReadOnlyList<string>? foldersWithTableTypes)
    {
        static string List(IEnumerable<string> f) => "@(" + string.Join(",", f.Select(x => "\"" + x + "\"")) + ")";
        var sb = new StringBuilder();
        sb.Append("param(\n");
        sb.Append($"    [string]$Server = {PsQuote(server)},\n");
        sb.Append("    [Parameter(Mandatory=$true)][string]$Database");
        sb.Append(foldersWithTableTypes is null ? "\n" : ",\n    [switch]$WithTableTypes\n");
        sb.Append(")\n");
        sb.Append($"$folders = {List(folders)}\n");
        if (foldersWithTableTypes is not null)
            sb.Append($"if ($WithTableTypes) {{ $folders = {List(foldersWithTableTypes)} }}\n");
        sb.Append("$failed = 0\n");
        sb.Append("foreach ($folder in $folders) {\n");
        sb.Append("    Write-Host \"\"\n");
        sb.Append("    Write-Host \"=== $folder ===\" -ForegroundColor Cyan\n");
        sb.Append("    & (Join-Path $PSScriptRoot \"$folder\\run_all.ps1\") -Server $Server -Database $Database -ContinueOnError\n");
        sb.Append("    $failed += $LASTEXITCODE\n");
        sb.Append("}\n");
        sb.Append("Write-Host \"\"\n");
        sb.Append("Write-Host \"$failed file(s) failed. Re-run the SchemaScope compare to see what is still different.\"\n");
        sb.Append("exit $failed\n");
        return Crlf(sb.ToString());
    }

    /// <summary>
    /// A PowerShell single-quoted string literal (no expansion). PowerShell also takes the typographic quotes
    /// U+2018–U+201B as single quotes, so each of those is doubled too, not only <c>'</c>: otherwise a server
    /// name such as <c>x’ + $(...) + ’</c> would close the literal and run code when RUN.ps1 starts.
    /// </summary>
    public static string PsQuote(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var c in s ?? "")
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex UnsafeRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRx();

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex LineBreakRx();

    [GeneratedRegex(@"^\s*GO(?:\s+\d+)?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLineRx();

    [GeneratedRegex(@"^\s*(?:GO(?:\s+\d+)?\s*(?:--.*)?$|:|!!|(?:exit|quit|reset|ed)\s*$|exit\s*\()", RegexOptions.IgnoreCase)]
    private static partial Regex BreakingLineRx();
}
