using System.Diagnostics;
using System.Text;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>The PostgreSQL scripts' splitter, validator, runners and rules, with no database.</summary>
public class PgScriptOfflineTests
{
    /// <summary>Statements psql and libpg_query must split alike: quotes, E'', dollar bodies, nested comments, SQL-standard bodies.</summary>
    public static readonly string[] Corpus =
    [
        "SELECT 1; SELECT 2;",
        "SELECT 'a;b', 'it''s; fine';\nSELECT 3;",
        "SELECT E'back\\\\slash; and \\' quote';\nSELECT 4;",
        "SELECT \"odd;name\" FROM \"t;1\";",
        "CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;\nSELECT 5;",
        "CREATE FUNCTION g() RETURNS text LANGUAGE sql AS $body$ SELECT '$$;' || $q$;$q$ $body$;\nSELECT 6;",
        "/* outer /* inner ; */ still comment ; */ SELECT 7; -- trailing ; comment\nSELECT 8;",
        "CREATE FUNCTION h(x int) RETURNS int LANGUAGE sql\nBEGIN ATOMIC\n  SELECT CASE WHEN x > 0 THEN 1 ELSE 0 END;\n  SELECT 2;\nEND;\nSELECT 9;",
        "CREATE OR REPLACE PROCEDURE p() LANGUAGE sql BEGIN ATOMIC INSERT INTO t VALUES (1); END;\nSELECT 10;",
        "CREATE RULE r AS ON INSERT TO t DO ALSO (SELECT 1; SELECT 2);\nSELECT 11;",
        "SELECT $1, a$b FROM t; SELECT 12",
    ];

    [Fact]
    public void The_splitter_agrees_with_the_postgres_parser()
    {
        Assert.True(PgParser.Available, PgParser.UnavailableReason);
        foreach (var sql in Corpus)
        {
            var mine = PgStatementSplitter.Instance.Split(sql).Select(b => b.Text.Trim().TrimEnd(';').TrimEnd()).ToList();
            var parser = PgParser.SplitWithParser(sql);
            Assert.NotNull(parser);
            Assert.Equal(parser!.Select(t => t.Trim()), mine);
        }
    }

    [Fact]
    public void Each_statement_keeps_the_line_it_starts_on()
    {
        var batches = PgStatementSplitter.Instance.Split("-- header\n\nSELECT 1;\n/* a\nb */ SELECT\n 2;\nCREATE FUNCTION f() RETURNS int AS $$\nSELECT 1;\n$$ LANGUAGE sql;\nSELECT 3");
        Assert.Equal([3, 5, 7, 10], batches.Select(b => b.StartLine));
        Assert.Equal("SELECT 1;", batches[0].Text);
        Assert.StartsWith("CREATE FUNCTION", batches[2].Text);
        Assert.Equal("SELECT 3", batches[3].Text);
    }

    [Fact]
    public void The_validator_and_apply_share_one_splitter() =>
        Assert.Same(PgStatementSplitter.Instance, new PgScriptValidator().Splitter);

    [Fact]
    public void Code_only_blanks_literals_and_comments_but_keeps_lines()
    {
        var code = PgStatementSplitter.CodeOnly("SELECT 'CONCURRENTLY' -- CONCURRENTLY\n, $$ VACUUM $$ /* x */ FROM t");
        Assert.DoesNotContain("CONCURRENTLY", code);
        Assert.DoesNotContain("VACUUM", code);
        Assert.Contains("FROM t", code);
        Assert.Equal(2, code.Split('\n').Length);
        Assert.Null(PgStatementSplitter.Unterminated("SELECT 'a''b', E'c\\'d', $x$ $$ $x$;"));
        Assert.NotNull(PgStatementSplitter.Unterminated("SELECT 'open"));
        Assert.NotNull(PgStatementSplitter.Unterminated("SELECT $$ open"));
        Assert.NotNull(PgStatementSplitter.Unterminated("SELECT 1 /* open"));
    }

    [Theory]
    [InlineData("character varying(20)", "character varying(50)", false)]
    [InlineData("character varying(20)", "text", false)]
    [InlineData("character varying(20)", "character varying", false)]
    [InlineData("numeric(10,2)", "numeric(12,2)", false)]
    [InlineData("numeric(10,2)", "numeric", false)]
    [InlineData("integer", "bigint", true)]
    [InlineData("smallint", "integer", true)]
    [InlineData("real", "double precision", true)]
    [InlineData("timestamp(3) without time zone", "timestamp without time zone", false)]
    public void Widenings_are_allowed(string from, string to, bool rewrites) => Assert.Equal(rewrites, PgScriptRun.Widening(from, to));

    [Theory]
    [InlineData("character varying(50)", "character varying(20)")]
    [InlineData("text", "character varying(20)")]
    [InlineData("numeric(10,2)", "numeric(12,3)")]
    [InlineData("numeric", "numeric(10,2)")]
    [InlineData("bigint", "integer")]
    [InlineData("integer", "text")]
    [InlineData("timestamp without time zone", "timestamp with time zone")]
    [InlineData("integer[]", "bigint[]")]
    public void Anything_else_is_not_a_widening(string from, string to) => Assert.Null(PgScriptRun.Widening(from, to));

    [Fact]
    public void A_check_s_expression_is_read_from_its_definition()
    {
        Assert.Equal("(total >= (0)::numeric)", PgScriptRun.CheckExpression("CHECK ((total >= (0)::numeric))"));
        Assert.Equal("(a > 0)", PgScriptRun.CheckExpression("CHECK ((a > 0)) NO INHERIT"));
        Assert.Null(PgScriptRun.CheckExpression("UNIQUE (a)"));
    }

    [Theory]
    [InlineData("(score >= 0)", false)]
    [InlineData("(length(name) > 1)", false)]
    [InlineData("(total >= 0.5)", false)]
    [InlineData("(total >= (0)::numeric)", false)]
    [InlineData("(app.f(x) > 0)", true)]
    [InlineData("(\"App\".\"F\"(x) > 0)", true)]
    [InlineData("(a OPERATOR(app.@@@) b)", true)]
    [InlineData("(x::app.positive > 0)", true)]
    [InlineData("(note <> 'a.b(c)')", false)]
    public void A_probe_never_runs_an_expression_that_names_a_user_object(string expression, bool refused) =>
        Assert.Equal(refused, PgTargetProbes.NamesAnObject(expression));

    [Fact]
    public void Names_are_found_as_the_deparse_spells_them()
    {
        Assert.True(PgDdl.Mentions("nextval('app.orders_id_seq'::regclass)", "app", "orders_id_seq"));
        Assert.False(PgDdl.Mentions("nextval('app.orders_id_seq'::regclass)", "app", "orders"));
        Assert.True(PgDdl.Mentions("'ok'::\"App\".\"Mood\"", "App", "Mood"));
        Assert.True(PgDdl.Mentions("app.mood[]", "app", "mood"));
        Assert.Equal("\"Mixed\"", PgDdl.Ident("Mixed"));
        Assert.Equal("plain_1", PgDdl.Ident("plain_1"));
        Assert.Equal("\"a\"\"b\"", PgDdl.Q("a\"b"));
    }

    // ------------------------------------------------------------------ validator

    private static (ScriptWriter Writer, string Root) Writer(bool bom = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"schemascope-pg-validate-{Guid.NewGuid():n}");
        return (new ScriptWriter(root, sqlBom: bom), root);
    }

    [Fact]
    public void A_postgres_run_writes_sql_without_a_bom_and_sql_server_keeps_it()
    {
        var (pg, pgRoot) = Writer();
        var (ss, ssRoot) = (new ScriptWriter(Path.Combine(Path.GetTempPath(), $"schemascope-ss-{Guid.NewGuid():n}")), "");
        try
        {
            var a = File.ReadAllBytes(pg.WriteSql("01_columns", "objects\\001_x.sql", "SELECT 1;\n").FullPath);
            var b = File.ReadAllBytes(ss.WriteSql("01_columns", "objects\\001_x.sql", "SELECT 1;\n").FullPath);
            Assert.NotEqual(0xEF, a[0]);
            Assert.Equal([0xEF, 0xBB, 0xBF], b[..3]);
            // runners keep their BOM either way (PowerShell 5 needs it for non-ASCII)
            Assert.Equal(0xEF, File.ReadAllBytes(pg.WriteRunnerText("01_columns", "Write-Host 'é'\r\n").FullPath)[0]);
        }
        finally
        {
            Directory.Delete(pgRoot, true);
            Directory.Delete(ss.Root, true);
            _ = ssRoot;
        }
    }

    [Fact]
    public void The_parser_finds_syntax_errors_and_what_must_never_run()
    {
        var (w, root) = Writer();
        try
        {
            w.WriteSql("05_constraints", "objects\\001_bad.sql", "-- x\nALTER TABLE t ADD CONSTRAINT c CHECK (a > 0;\n");
            w.WriteSql("06_indexes", "objects\\001_c.sql", "CREATE INDEX CONCURRENTLY i ON t (a);\n");
            w.WriteSql("03_missing", "objects\\001_v.sql", "CREATE VIEW v AS SELECT 1;\n", WrittenFileKind.RawModule, expectedModuleText: "CREATE VIEW v AS SELECT 2");
            w.WriteSql("03_missing", "objects\\002_m.sql", "\\i other.sql\nSELECT 1;\n");
            var review = new ReviewLog();
            var v = new PgScriptValidator().Validate(w.Files, review, 16, 16);
            Assert.True(v.Level!.Parsed);
            Assert.False(v.Passed);
            Assert.True(v.SyntaxErrors >= 1);
            Assert.Contains(v.Problems, p => p.StartsWith("05_constraints\\objects\\001_bad.sql: line 2:", StringComparison.Ordinal));
            Assert.Contains(v.Problems, p => p.Contains("CONCURRENTLY"));
            Assert.Contains(v.Problems, p => p.Contains("not the source's text"));
            Assert.Contains(v.Problems, p => p.Contains("backslash command"));
            Assert.Equal(1, v.ModulesChecked);
            Assert.Equal(0, v.ModulesExact);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Without_the_parser_the_check_is_lexical_and_never_passes()
    {
        var (w, root) = Writer();
        PgParser.ForceUnavailable = true;
        try
        {
            w.WriteSql("01_columns", "objects\\001_ok.sql", "ALTER TABLE t ADD COLUMN c int;\n");
            var v = new PgScriptValidator().Validate(w.Files, new ReviewLog(), 16, 16);
            Assert.False(v.Level!.Parsed);
            Assert.False(v.Level.ModuleTextProven);
            Assert.StartsWith("Lexical check only - not validated", v.Level.NotValidatedReason);
            Assert.Equal(0, v.SyntaxErrors);
            Assert.Empty(v.Problems);
            Assert.False(v.Passed);

            w.WriteSql("01_columns", "objects\\002_bad.sql", "SELECT (1;\n");
            Assert.Equal(1, new PgScriptValidator().Validate(w.Files, new ReviewLog(), 16, 16).SyntaxErrors);
        }
        finally
        {
            PgParser.ForceUnavailable = false;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Sql_server_validation_is_unchanged_without_a_level()
    {
        var v = new ValidationSummary { Batches = 3 };
        Assert.Null(v.Level);
        Assert.True(v.Passed);
        Assert.DoesNotContain("level", System.Text.Json.JsonSerializer.Serialize(v, OwnerApp.Json));
    }

    // ------------------------------------------------------------------ runners

    private static readonly PgRunnerTarget Target = new("db.example", 6543, "deploy", "verify-full");

    [Fact]
    public void The_runners_use_psql_safely_and_never_hold_a_password()
    {
        var folder = PgRunners.Folder(Target);
        var root = PgRunners.Root(Target, ["04_columntypes", "01_columns"]);
        foreach (var text in new[] { folder, root })
        {
            Assert.Contains("[string]$Server = 'db.example'", text);
            Assert.Contains("[int]$Port = 6543", text);
            Assert.Contains("[string]$User = 'deploy'", text);
            Assert.Contains("PGPASSFILE", text);
            Assert.Contains("-AsSecureString", text);
            Assert.DoesNotContain("PGPASSWORD =", text.Replace("$env:PGPASSWORD = [Runtime", ""));
            Assert.Contains("\r\n", text);
        }
        Assert.Contains("psql -X -v ON_ERROR_STOP=1 --single-transaction", folder);
        Assert.Contains("$folders = @(\"04_columntypes\",\"01_columns\")", root);
        // a quote in a host cannot end the PowerShell literal
        Assert.Contains("'x''); evil'", PgRunners.Folder(Target with { Host = "x'); evil" }));
    }

    [Fact]
    public void Runner_target_comes_from_the_connection_without_its_password()
    {
        var t = PgRunnerTarget.From(new ScriptEndpoint("T", "h", "db", "Host=pg.internal;Port=6000;Database=db;Username=app;Password=secret-123;SSL Mode=Require",
            DatabaseEngine.PostgreSql));
        Assert.Equal(new PgRunnerTarget("pg.internal", 6000, "app", "require"), t);
        Assert.DoesNotContain("secret-123", PgRunners.Root(t, ["01_columns"]) + PgRunners.Folder(t));
    }

    private static (int Code, string Output, List<string> Log) RunPowerShell(string root, string script, string args, string? passFile = null)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "..", Path.GetFileName(root) + "-bin")).FullName;
        var log = Path.Combine(bin, "ran.txt");
        // a stand-in psql: records its arguments and whether it was given a password, connects nowhere
        File.WriteAllText(Path.Combine(bin, "psql.cmd"),
            "@echo %* ^| PASSFILE=%PGPASSFILE% ^| PASSWORD=%PGPASSWORD% ^| OPTIONS=%PGOPTIONS% ^| SSL=%PGSSLMODE%>>\"%SS_RUNLOG%\"\r\n@exit /b 0\r\n");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(root, script)}\" {args}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["SS_RUNLOG"] = log;
        psi.Environment.Remove("PGPASSWORD");
        psi.Environment.Remove("PGPASSFILE");
        if (passFile is not null) psi.Environment["PGPASSFILE"] = passFile;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), output);
        var lines = File.Exists(log) ? File.ReadAllLines(log).ToList() : [];
        Directory.Delete(bin, true);
        return (p.ExitCode, output, lines);
    }

    [Fact]
    public void Run_ps1_runs_each_folder_by_number_with_psql_and_a_password_file_from_outside()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"schemascope-pg-run-{Guid.NewGuid():n}");
        var outside = Path.Combine(Path.GetTempPath(), $"schemascope-pgpass-{Guid.NewGuid():n}.conf");
        try
        {
            foreach (var (folder, files) in new[] { ("04_columntypes", new[] { "001_a.sql" }), ("01_columns", new[] { "10_c.sql", "9_b.sql" }) })
            {
                var objects = Directory.CreateDirectory(Path.Combine(root, folder, "objects")).FullName;
                foreach (var f in files) File.WriteAllText(Path.Combine(objects, f), "SELECT 1;\n");
                File.WriteAllText(Path.Combine(root, folder, "run_all.ps1"), PgRunners.Folder(Target), new UTF8Encoding(true));
            }
            File.WriteAllText(Path.Combine(root, "RUN.ps1"), PgRunners.Root(Target, ["04_columntypes", "01_columns"]), new UTF8Encoding(true));
            File.WriteAllText(outside, "*:*:*:*:not-a-real-password\n");

            var (code, output, log) = RunPowerShell(root, "RUN.ps1", $"-Database target_db -PassFile \"{outside}\"");
            Assert.True(code == 0, output);
            Assert.Equal(3, log.Count);
            Assert.EndsWith("001_a.sql", log[0].Split(" | ")[0].Trim().Trim('"'));
            Assert.EndsWith("9_b.sql", log[1].Split(" | ")[0].Trim().Trim('"'));
            Assert.EndsWith("10_c.sql", log[2].Split(" | ")[0].Trim().Trim('"'));
            Assert.All(log, l =>
            {
                Assert.Contains("-X -v ON_ERROR_STOP=1 --single-transaction -h db.example -p 6543 -U deploy -d target_db -f", l);
                Assert.Contains($"PASSFILE={outside}", l);
                Assert.Contains("PASSWORD= |", l);
                Assert.Contains("OPTIONS=-c lock_timeout=5s", l);
                Assert.Contains("SSL=verify-full", l);
            });

            // a password file inside the run folder is refused before psql runs
            var inside = Path.Combine(root, "pgpass.conf");
            File.WriteAllText(inside, "*:*:*:*:x\n");
            var refused = RunPowerShell(root, "RUN.ps1", "-Database target_db", passFile: inside);
            Assert.Equal(1, refused.Code);
            Assert.Contains("inside the scripts folder", refused.Output);
            Assert.Empty(refused.Log);
        }
        finally
        {
            Directory.Delete(root, true);
            File.Delete(outside);
        }
    }
    // ------------------------------------------------------------------ R2 review

    private static DbObject Pg(DbObjectType type, string schema, string name, string? definition = null, string? signature = null) =>
        new() { Type = type, Schema = schema, Name = name, Fold = IdentifierFold.Exact, Definition = definition, Signature = signature };

    [Fact]
    public async Task Missing_code_is_created_never_replaced()
    {
        var src = new SchemaSnapshot
        {
            Engine = DatabaseEngine.PostgreSql, MajorVersion = 16,
            Objects =
            [
                Pg(DbObjectType.Schema, "app", "app"),
                Pg(DbObjectType.ScalarFunction, "app", "f", "CREATE OR REPLACE FUNCTION app.f()\n RETURNS integer\n LANGUAGE sql\nAS $function$ SELECT 1 $function$\n", ""),
                Pg(DbObjectType.StoredProcedure, "app", "p", "CREATE OR REPLACE PROCEDURE app.p()\n LANGUAGE sql\nAS $procedure$ SELECT 1 $procedure$\n", ""),
                Pg(DbObjectType.View, "app", "v", "CREATE OR REPLACE VIEW \"app\".\"v\" AS\n SELECT 1 AS one;"),
            ]
        };
        var tgt = new SchemaSnapshot { Engine = DatabaseEngine.PostgreSql, MajorVersion = 16, Objects = [Pg(DbObjectType.Schema, "app", "app")] };
        var output = Path.Combine(Path.GetTempPath(), $"schemascope-pg-offline-{Guid.NewGuid():n}");
        try
        {
            var endpoint = new ScriptEndpoint("Target", "db.example", "tgt", "Host=db.example;Database=tgt", DatabaseEngine.PostgreSql);
            var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(endpoint with { Label = "Source" }, endpoint, src, tgt,
                new CompareResult(), 0, new ScriptOptions { Sections = [ScriptSection.MissingObjects], OutputFolder = output },
                new HashSet<string>(), new Dictionary<string, LineChange>()), default, null);
            Assert.True(outcome.Success, outcome.Message);
            var files = outcome.Files.Where(f => f.Folder == PgScriptGenerator.MissingFolder && f.FullPath.EndsWith(".sql", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(files);
            Assert.All(files, f => Assert.DoesNotContain("OR REPLACE", File.ReadAllText(f.FullPath)));
            var all = string.Join("\n", files.Select(f => File.ReadAllText(f.FullPath)));
            Assert.Contains("CREATE FUNCTION app.f()", all);
            Assert.Contains("CREATE PROCEDURE app.p()", all);
            Assert.Contains("CREATE VIEW \"app\".\"v\" AS", all);
            Assert.True(outcome.Validation.Passed, string.Join("\n", outcome.Validation.Problems));
            Assert.Equal(3, outcome.Validation.ModulesChecked);
            Assert.Equal(3, outcome.Validation.ModulesExact);
        }
        finally
        {
            try { Directory.Delete(output, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("CREATE OR REPLACE FUNCTION a.f()\n RETURNS integer", "CREATE FUNCTION a.f()\n RETURNS integer")]
    [InlineData("CREATE OR REPLACE PROCEDURE a.p()", "CREATE PROCEDURE a.p()")]
    [InlineData("CREATE OR REPLACE VIEW \"a\".\"v\" AS\n SELECT 1;", "CREATE VIEW \"a\".\"v\" AS\n SELECT 1;")]
    [InlineData("CREATE MATERIALIZED VIEW \"a\".\"m\" AS\n SELECT 1;", "CREATE MATERIALIZED VIEW \"a\".\"m\" AS\n SELECT 1;")]
    [InlineData("CREATE AGGREGATE a.s(numeric) (\n    SFUNC = numeric_add\n);", "CREATE AGGREGATE a.s(numeric) (\n    SFUNC = numeric_add\n);")]
    public void Create_only_drops_or_replace_and_keeps_the_rest_exactly(string definition, string expected)
    {
        var (text, module) = PgScriptRun.CreateOnly(definition);
        Assert.Equal(expected, text);
        Assert.Contains(module, text);
        Assert.EndsWith(module, definition);
    }

    [Fact]
    public void The_functions_a_check_calls_are_read_from_its_parse_tree()
    {
        Assert.Equal([(null, "pg_cancel_backend", 1)], PgParser.FunctionsCalled("((pg_cancel_backend(0) IS NOT NULL) OR true)")!);
        Assert.Equal([(null, "length", 1), (null, "lower", 1)], PgParser.FunctionsCalled("(length(lower(name)) > 0)")!.OrderBy(c => c.Name));
        Assert.Empty(PgParser.FunctionsCalled("((score >= 0) AND (note <> 'f(x)'::text))")!);
        Assert.Contains(PgParser.FunctionsCalled("(EXTRACT(year FROM d) > (2000)::numeric)")!, c => c.Name == "extract" && c.Arguments == 2);
        Assert.Null(PgParser.FunctionsCalled("1); SELECT pg_sleep(10"));
    }

    [Fact]
    public void An_index_of_a_partitioned_table_is_built_on_every_partition()
    {
        var parent = Pg(DbObjectType.Table, "Sales", "ptab");
        parent.Properties["partitionBy"] = "RANGE (id)";
        Assert.Equal("CREATE INDEX ptab_amount_ix ON \"Sales\".ptab USING btree (amount)",
            PgScriptRun.OnEveryPartition(parent, "CREATE INDEX ptab_amount_ix ON ONLY \"Sales\".ptab USING btree (amount)"));
        Assert.Equal("CREATE UNIQUE INDEX \"odd ON ONLY x\" ON \"Sales\".ptab USING btree (id)",
            PgScriptRun.OnEveryPartition(parent, "CREATE UNIQUE INDEX \"odd ON ONLY x\" ON ONLY \"Sales\".ptab USING btree (id)"));
        var plain = Pg(DbObjectType.Table, "Sales", "t");
        Assert.Equal("CREATE INDEX t_ix ON ONLY \"Sales\".t USING btree (a)", PgScriptRun.OnEveryPartition(plain, "CREATE INDEX t_ix ON ONLY \"Sales\".t USING btree (a)"));
    }

    // ------------------------------------------------------------------ R3b: 11_destructive

    [Theory]
    [InlineData("character varying(20)", "character varying(10)", "char_length(\"c\") > 10")]
    [InlineData("text", "character varying(5)", "char_length(\"c\") > 5")]
    [InlineData("bigint", "integer", "\"c\" NOT BETWEEN -2147483648 AND 2147483647")]
    [InlineData("integer", "smallint", "\"c\" NOT BETWEEN -32768 AND 32767")]
    [InlineData("numeric(10,3)", "numeric(10,2)", "\"c\" <> round(\"c\", 2) OR abs(\"c\") >= power(10::numeric, 8)")]
    [InlineData("numeric", "numeric(5)", "\"c\" <> round(\"c\", 0) OR abs(\"c\") >= power(10::numeric, 5)")]
    [InlineData("timestamp(6) with time zone", "timestamp(0) with time zone", "\"c\" IS DISTINCT FROM CAST(\"c\" AS timestamp(0) with time zone)")]
    [InlineData("timestamp without time zone", "timestamp(3) without time zone", "\"c\" IS DISTINCT FROM CAST(\"c\" AS timestamp(3) without time zone)")]
    public void A_narrowing_is_probed_with_a_predicate_built_from_the_two_types_only(string from, string to, string predicate) =>
        Assert.Equal(predicate, PgScriptRun.FitPredicate(from, to, "\"c\""));

    [Theory]
    [InlineData("character(5)", "character varying(3)")]   // char to varchar drops trailing spaces: another family
    [InlineData("text", "integer")]
    [InlineData("double precision", "real")]
    [InlineData("integer", "bigint")]                      // not a narrowing
    [InlineData("app.mood", "text")]
    [InlineData("timestamp(3) without time zone", "timestamp(3) with time zone")]
    public void Anything_else_has_no_predicate_and_is_held(string from, string to) => Assert.Null(PgScriptRun.FitPredicate(from, to, "\"c\""));

    [Fact]
    public async Task Case_twins_are_held_and_nothing_is_dropped_without_the_option()
    {
        ColumnInfo Col(string name) => new() { Name = name, DataType = "integer", IsNullable = true };
        DbObject Table(string schema, string name, params string[] columns)
        {
            var t = Pg(DbObjectType.Table, schema, name);
            t.Columns = [.. columns.Select((c, i) => { var col = Col(c); col.Ordinal = i + 1; return col; })];
            t.Hash = $"{name}:{string.Join(",", columns)}";
            return t;
        }
        var src = new SchemaSnapshot
        {
            Engine = DatabaseEngine.PostgreSql, MajorVersion = 16,
            Objects = [Pg(DbObjectType.Schema, "public", "public"), Table("public", "Users", "id", "Email")],
        };
        var tgt = new SchemaSnapshot
        {
            Engine = DatabaseEngine.PostgreSql, MajorVersion = 16,
            Objects = [Pg(DbObjectType.Schema, "public", "public"), Table("public", "Users", "id", "Email", "email"), Table("public", "users", "id")],
        };
        var output = Path.Combine(Path.GetTempPath(), $"schemascope-pg-offline-{Guid.NewGuid():n}");
        try
        {
            var endpoint = new ScriptEndpoint("Target", "db.example", "tgt", "Host=db.example;Database=tgt", DatabaseEngine.PostgreSql);
            async Task<ScriptOutcome> Run(bool destructive) => await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(endpoint with { Label = "Source" },
                endpoint, src, tgt, new CompareResult(), 0,
                new ScriptOptions { Sections = [ScriptSection.Constraints], OutputFolder = output, IncludeDestructive = destructive, CheckExistingData = false },
                new HashSet<string>(), new Dictionary<string, LineChange>()), default, null);

            var on = await Run(destructive: true);
            Assert.True(on.Success, on.Message);
            Assert.DoesNotContain(on.Files, f => f.Folder == PgScriptGenerator.DestructiveFolder && f.IsObjectFile);
            Assert.Contains(on.HeldBack, h => h.Object == "public.users" && h.Reason.Contains("Source has table public.users spelled in another letter case"));
            Assert.Contains(on.HeldBack, h => h.Object == "public.Users.email" && h.Reason.Contains("another letter case"));
            var folder = Assert.Single(on.Folders, f => f.Folder == PgScriptGenerator.DestructiveFolder);
            Assert.Equal(0, folder.Files);
            Assert.Contains(folder.Notes, n => n.Contains("2 change(s) held back"));

            // without the option there is no 11 at all, and the target-only items say how they could be dropped
            var off = await Run(destructive: false);
            Assert.True(off.Success, off.Message);
            Assert.DoesNotContain(off.Folders, f => f.Folder == PgScriptGenerator.DestructiveFolder);
            Assert.DoesNotContain(off.Files, f => f.Folder == PgScriptGenerator.DestructiveFolder);
            Assert.Contains(off.HeldBack, h => h.Object == "public.Users.email" && h.Reason.Contains("only 11_destructive drops it"));
        }
        finally
        {
            try { Directory.Delete(output, true); } catch (IOException) { }
        }
    }
}
