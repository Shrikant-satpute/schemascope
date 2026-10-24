using System.Net;
using System.Text.Json;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// Hardening round 2 on real servers. Each case is a reported defect: a small drifted pair, compared, scripted, applied
/// the way Write mode applies (each file in its own transaction, PgApplyExecutor, every folder in run order, failures
/// collected rather than stopping), compared again.
/// </summary>
public abstract class PgHardening2TestsBase(PgServer pg)
{
    /// <summary>Compare, script, apply every folder, compare again: (outcome, failures, rows still not Same).</summary>
    private async Task<(ScriptOutcome Outcome, List<string> Failures, List<string> Left)> RoundAsync(PgScriptPair pair, bool destructive = false,
        CompareOptions? compare = null, IEnumerable<ScriptSection>? sections = null)
    {
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(compare), sections, compare, destructive);
        var failures = await PgHardeningTestsBase.ApplyAsync(pg, pair.Tgt, outcome);
        return (outcome, failures, PgScriptPair.NotSame(await pair.CompareAsync(compare)));
    }

    private static string Held(ScriptOutcome o) => string.Join("\n", o.HeldBack.Select(h => $"{h.Section} | {h.Object} | {h.Reason}"));

    private async Task<int> MajorAsync(PgScriptPair pair) => (int)(await pair.ScalarAsync("SELECT current_setting('server_version_num')::int") as int? ?? 0) / 10000;

    // ---------------- aggregates

    protected async Task Aggregates_keep_every_option_and_a_changed_one_is_replaced()
    {
        const string common = "CREATE SCHEMA ag; CREATE TABLE ag.t (x integer); ";
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE AGGREGATE ag.fp_sum(numeric) (SFUNC = numeric_add, STYPE = numeric, INITCOND = '0');
            CREATE AGGREGATE ag.hyp(VARIADIC "any" ORDER BY VARIADIC "any") (SFUNC = ordered_set_transition_multi, STYPE = internal,
                FINALFUNC = rank_final, FINALFUNC_EXTRA, HYPOTHETICAL);
            CREATE AGGREGATE ag.msum(integer) (SFUNC = int4_sum, STYPE = int8, MSFUNC = int4_avg_accum, MINVFUNC = int4_avg_accum_inv,
                MSTYPE = _int8, MFINALFUNC = int2int4_sum, MINITCOND = '{0,0}');
            CREATE AGGREGATE ag.psum(integer) (SFUNC = int4_sum, STYPE = int8, COMBINEFUNC = int8pl, PARALLEL = SAFE);
            CREATE AGGREGATE ag.top(integer) (SFUNC = int4larger, STYPE = int4, SORTOP = >);
            CREATE AGGREGATE ag.top2(integer) (SFUNC = int4larger, STYPE = int4, SORTOP = >);
            """, common + """
            INSERT INTO ag.t VALUES (1), (2), (3);
            CREATE AGGREGATE ag.fp_sum(numeric) (SFUNC = numeric_add, STYPE = numeric, INITCOND = '1');
            CREATE AGGREGATE ag.msum(integer) (SFUNC = int4_sum, STYPE = int8);
            CREATE AGGREGATE ag.psum(integer) (SFUNC = int4_sum, STYPE = int8, COMBINEFUNC = int8pl);
            CREATE AGGREGATE ag.top2(integer) (SFUNC = int4larger, STYPE = int4);
            """);
        var before = PgScriptPair.NotSame(await pair.CompareAsync());
        // the options the reader used to leave out are part of what an aggregate is
        Assert.Contains("Aggregate ag.msum integer: Different", before);
        Assert.Contains("Aggregate ag.psum integer: Different", before);
        Assert.Contains("Aggregate ag.top2 integer: Different", before);

        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Contains("CREATE OR REPLACE AGGREGATE ag.fp_sum(numeric)", PgScriptPair.Folder(outcome, PgScriptGenerator.DifferentFolder));
        Assert.Contains("SORTOP = OPERATOR(pg_catalog.>)", PgScriptPair.Folder(outcome, PgScriptGenerator.MissingFolder));
        // the aggregate 03 made is the source's: a hypothetical-set aggregate that answers as one
        Assert.Equal(3L, await pair.ScalarAsync("SELECT ag.hyp(3) WITHIN GROUP (ORDER BY x) FROM ag.t"));
        Assert.Equal(">", await pair.ScalarAsync("SELECT o.oprname::text FROM pg_aggregate a JOIN pg_operator o ON o.oid = a.aggsortop WHERE a.aggfnoid = 'ag.top'::regproc"));
        Assert.Equal("s", await pair.ScalarAsync("SELECT proparallel::text FROM pg_proc WHERE oid = 'ag.psum'::regproc"));
    }

    // ---------------- 04, 05, 11 on columns

    protected async Task A_column_default_is_no_generated_column()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA app;
            CREATE TABLE app.wide (id integer, amount numeric(18,2) NOT NULL DEFAULT 0, cnt bigint DEFAULT 0, label varchar(100) DEFAULT 'x');
            CREATE TABLE app.gen (qty integer, price integer, big_counter bigint DEFAULT 0, total integer GENERATED ALWAYS AS (qty * price) STORED);
            CREATE SCHEMA w;
            CREATE TABLE w.t (id integer, label varchar(40) DEFAULT 'x', plain varchar(40));
            """, """
            CREATE SCHEMA app;
            CREATE TABLE app.wide (id integer, amount numeric(12,2) NOT NULL DEFAULT 0, cnt integer DEFAULT 0, label varchar(40) DEFAULT 'x');
            CREATE TABLE app.gen (qty integer, price integer, big_counter integer DEFAULT 0, total integer GENERATED ALWAYS AS (qty * price) STORED);
            CREATE SCHEMA w;
            CREATE TABLE w.t (id integer, label varchar(100) DEFAULT 'x', plain varchar(100));
            INSERT INTO w.t VALUES (1, 'short', 'short');
            """);
        var (outcome, failures, left) = await RoundAsync(pair, destructive: true);
        Assert.DoesNotContain(outcome.HeldBack, h => h.Reason.Contains("generated column"));
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Contains("ALTER COLUMN \"big_counter\" TYPE bigint", PgScriptPair.Folder(outcome, PgScriptGenerator.ColumnTypesFolder));
    }

    protected async Task Columns_held_until_03_keep_the_source_s_order_over_two_runs()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA app;
            CREATE DOMAIN app.code AS text;
            CREATE SEQUENCE app.item_ref_seq;
            CREATE FUNCTION app.gen_code() RETURNS text LANGUAGE sql AS $$ SELECT 'x' $$;
            CREATE TYPE app.kind AS ENUM ('a', 'b');
            CREATE TABLE app.item (id integer, name text, qty integer, price integer, flag boolean, note text,
                code app.code, ref_no bigint DEFAULT nextval('app.item_ref_seq'), gen text DEFAULT app.gen_code(), kind app.kind,
                total integer GENERATED ALWAYS AS (qty * price) STORED);
            """, """
            CREATE SCHEMA app;
            CREATE TABLE app.item (id integer, name text, qty integer, price integer, flag boolean, note text);
            INSERT INTO app.item VALUES (1, 'a', 2, 3, true, 'n');
            """);
        var (first, failures, _) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains(first.HeldBack, h => h.Object == "app.item.total" && h.Reason.Contains("comes after code"));
        var (_, again, left) = await RoundAsync(pair);
        Assert.Empty(again);
        Assert.Empty(left);
    }

    protected async Task Safe_metadata_changes_are_scripted()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA r;
            CREATE TABLE r.fp_calc (id integer GENERATED ALWAYS AS IDENTITY, v integer);
            CREATE TABLE r.ref (id integer, ref integer, CONSTRAINT fp_calc_ref_uq UNIQUE (ref) DEFERRABLE INITIALLY DEFERRED);
            CREATE TYPE r.fp_pair AS (a integer, b text);
            CREATE TYPE r.typed AS (a integer, b text);
            CREATE TABLE r.of_it OF r.typed;
            """, """
            CREATE SCHEMA r;
            CREATE TABLE r.fp_calc (id integer GENERATED BY DEFAULT AS IDENTITY, v integer);
            INSERT INTO r.fp_calc (v) VALUES (1);
            CREATE TABLE r.ref (id integer, ref integer, CONSTRAINT fp_calc_ref_uq UNIQUE (ref));
            INSERT INTO r.ref VALUES (1, 1), (2, 2);
            CREATE TYPE r.fp_pair AS (a integer);
            CREATE TYPE r.typed AS (a integer);
            CREATE TABLE r.of_it OF r.typed;
            """);
        var (outcome, failures, left) = await RoundAsync(pair, destructive: true);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object.StartsWith("r.", StringComparison.Ordinal));
        Assert.Equal("a", await pair.ScalarAsync("SELECT attidentity::text FROM pg_attribute WHERE attrelid = 'r.fp_calc'::regclass AND attname = 'id'"));
        Assert.Equal(true, await pair.ScalarAsync("SELECT condeferred FROM pg_constraint WHERE conname = 'fp_calc_ref_uq'"));
        Assert.Equal(2L, await pair.ScalarAsync("SELECT count(*) FROM r.ref"));
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = 'r.of_it'::regclass AND attname = 'b'"));
    }

    protected async Task Changes_postgres_refuses_on_a_table_are_held_with_why()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA w;
            CREATE TABLE w.m (id bigint, v integer) PARTITION BY RANGE (id);
            CREATE TABLE w.m1 PARTITION OF w.m FOR VALUES FROM (0) TO (100);
            CREATE TABLE w.t (id bigint, a integer DEFAULT 1, b uuid DEFAULT gen_random_uuid(), c integer NOT NULL DEFAULT 0, d integer);
            CREATE TABLE w.u (r w.t);
            """, """
            CREATE SCHEMA w;
            CREATE TABLE w.m (id integer, v integer) PARTITION BY RANGE (id);
            CREATE TABLE w.m1 PARTITION OF w.m FOR VALUES FROM (0) TO (100);
            CREATE TABLE w.t (id integer);
            CREATE TABLE w.u (r w.t);
            INSERT INTO w.u VALUES (ROW(1));
            """);
        var (outcome, failures, _) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains(outcome.HeldBack, h => h.Object == "w.m.id" && h.Reason.Contains("partition key"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "w.t.id" && h.Reason.Contains("row type"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "w.t.a" && h.Reason.Contains("row type"));
        // a plain new column rewrites nothing, so PostgreSQL takes it
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = 'w.t'::regclass AND attname = 'd'"));
    }

    // ---------------- partitions and inheritance

    protected async Task Partition_indexes_come_once_and_a_partition_s_own_is_built()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA q;
            CREATE TABLE q.ord (id integer, region text, yr integer, note text) PARTITION BY LIST (region);
            CREATE TABLE q.ord_eu PARTITION OF q.ord FOR VALUES IN ('eu') PARTITION BY RANGE (yr);
            CREATE TABLE q.ord_eu_2025 PARTITION OF q.ord_eu FOR VALUES FROM (2025) TO (2026);
            CREATE TABLE q.ord_us PARTITION OF q.ord FOR VALUES IN ('us');
            CREATE INDEX ord_note_ix ON q.ord (note);
            CREATE TABLE q.ord_apac PARTITION OF q.ord FOR VALUES IN ('apac') PARTITION BY RANGE (yr);
            CREATE TABLE q.ord_apac_2024 PARTITION OF q.ord_apac FOR VALUES FROM (2024) TO (2025);
            CREATE UNIQUE INDEX ord_us_note_ux ON q.ord_us (note);
            """, """
            CREATE SCHEMA q;
            CREATE TABLE q.ord (id integer, region text, yr integer, note text) PARTITION BY LIST (region);
            CREATE TABLE q.ord_eu PARTITION OF q.ord FOR VALUES IN ('eu') PARTITION BY RANGE (yr);
            CREATE TABLE q.ord_us PARTITION OF q.ord FOR VALUES IN ('us');
            CREATE INDEX ord_note_ix ON q.ord (note);
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.DoesNotContain("ord_apac_2024_note_idx", PgScriptPair.Folder(outcome, PgScriptGenerator.IndexesFolder));
        Assert.DoesNotContain(outcome.HeldBack, h => h.Reason.Contains("partition or inheritance child"));
        var (last, _, _) = await RoundAsync(pair);
        Assert.DoesNotContain(last.Files, f => f.IsObjectFile);
    }

    protected async Task A_child_s_own_columns_checks_keys_indexes_and_defaults_are_its_own()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA ia;
            CREATE TABLE ia.p2 (id integer);
            CREATE TABLE ia.c2 (id integer, z integer, CONSTRAINT c2_chk CHECK (z > 0)) INHERITS (ia.p2);
            CREATE INDEX c2_z_ix ON ia.c2 (z);
            CREATE SCHEMA pl;
            CREATE TABLE pl.ref (id integer PRIMARY KEY);
            CREATE TABLE pl.m (id integer, d date, v integer, rid integer) PARTITION BY RANGE (d);
            CREATE TABLE pl.m1 PARTITION OF pl.m (CONSTRAINT m1_v CHECK (v > 0)) FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
            ALTER TABLE pl.m1 ALTER COLUMN v SET DEFAULT 5;
            ALTER TABLE pl.m1 ADD CONSTRAINT m1_fk FOREIGN KEY (rid) REFERENCES pl.ref (id);
            CREATE INDEX m1_v_ix ON pl.m1 (v);
            CREATE TABLE pl.m2 PARTITION OF pl.m (CONSTRAINT m2_v CHECK (v > 1), v WITH OPTIONS DEFAULT 6) FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
            CREATE INDEX m2_v_ix ON pl.m2 (v);
            CREATE TABLE pl.p (id integer, v integer);
            CREATE TABLE pl.c (CONSTRAINT c_v CHECK (v > 0)) INHERITS (pl.p);
            CREATE SCHEMA io;
            CREATE TABLE io.p (id integer, x integer);
            CREATE TABLE io.c (y integer) INHERITS (io.p);
            """, """
            CREATE SCHEMA ia;
            CREATE TABLE ia.p2 (id integer);
            CREATE TABLE ia.c2 (id integer) INHERITS (ia.p2);
            CREATE SCHEMA pl;
            CREATE TABLE pl.ref (id integer PRIMARY KEY);
            CREATE TABLE pl.m (id integer, d date, v integer, rid integer) PARTITION BY RANGE (d);
            CREATE TABLE pl.m1 PARTITION OF pl.m FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
            CREATE TABLE pl.p (id integer, v integer);
            CREATE SCHEMA io;
            CREATE TABLE io.p (id integer);
            CREATE TABLE io.c (y integer) INHERITS (io.p);
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.DoesNotContain(outcome.HeldBack, h => h.Reason.Contains("partition or inheritance child"));
        // a column the parent gains lands at the child's end: said, as for any table
        Assert.Contains(outcome.HeldBack, h => h.Object == "io.c" && h.Reason.Contains("another order"));
        Assert.Equal(["Table io.c: Different"], left);
        Assert.Contains("\"v\" WITH OPTIONS DEFAULT 6", PgScriptPair.Folder(outcome, PgScriptGenerator.MissingFolder));
        Assert.Empty(PgScriptPair.NotSame(await pair.CompareAsync(new CompareOptions { IgnoreColumnOrder = true })));
    }

    protected async Task A_partitioned_table_s_foreign_key_is_added_the_way_its_version_can()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA pf;
            CREATE TABLE pf.ref (id integer PRIMARY KEY);
            CREATE TABLE pf.m (id integer, d date NOT NULL, rid integer, CONSTRAINT m_fk FOREIGN KEY (rid) REFERENCES pf.ref (id)) PARTITION BY RANGE (d);
            CREATE TABLE pf.m1 PARTITION OF pf.m FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
            CREATE TABLE pf.n (id integer, d date NOT NULL, rid integer, CONSTRAINT n_fk FOREIGN KEY (rid) REFERENCES pf.ref (id) ON DELETE CASCADE) PARTITION BY RANGE (d);
            CREATE TABLE pf.n1 PARTITION OF pf.n FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
            """, """
            CREATE SCHEMA pf;
            CREATE TABLE pf.ref (id integer PRIMARY KEY);
            INSERT INTO pf.ref VALUES (1);
            CREATE TABLE pf.m (id integer, d date NOT NULL, rid integer) PARTITION BY RANGE (d);
            CREATE TABLE pf.m1 PARTITION OF pf.m FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
            INSERT INTO pf.m VALUES (1, '2024-02-02', 1);
            CREATE TABLE pf.n (id integer, d date NOT NULL, rid integer, CONSTRAINT n_fk FOREIGN KEY (rid) REFERENCES pf.ref (id)) PARTITION BY RANGE (d);
            CREATE TABLE pf.n1 PARTITION OF pf.n FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        var constraints = PgScriptPair.Folder(outcome, PgScriptGenerator.ConstraintsFolder);
        if (await MajorAsync(pair) < 18) Assert.DoesNotContain("NOT VALID;", constraints);
    }

    // ---------------- types, operators, domains

    protected async Task A_domain_check_is_written_only_once_the_stored_values_meet_it()
    {
        const string common = "CREATE SCHEMA m; CREATE SCHEMA billing; ";
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE DOMAIN m.code AS text CHECK (length(VALUE) <= 3);
            CREATE TABLE m.t (c m.code);
            CREATE DOMAIN billing.fp_dom AS integer CONSTRAINT fp_dom_pos CHECK (VALUE > 0);
            CREATE TABLE billing.fp_calc (dom_val billing.fp_dom);
            """, common + """
            CREATE DOMAIN m.code AS text;
            CREATE TABLE m.t (c m.code);
            INSERT INTO m.t VALUES ('abcdef');
            CREATE DOMAIN billing.fp_dom AS integer CONSTRAINT fp_dom_pos CHECK (VALUE >= 0);
            CREATE TABLE billing.fp_calc (dom_val billing.fp_dom);
            INSERT INTO billing.fp_calc VALUES (0);
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains(outcome.HeldBack, h => h.Object == "m.code.code_check" && h.Reason.Contains("m.t.c break it"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "billing.fp_dom.fp_dom_pos" && h.Reason.Contains("billing.fp_calc.dom_val break it"));
        // the target keeps its own rule while it is held
        Assert.Equal("CHECK (VALUE >= 0)", await pair.ScalarAsync("SELECT pg_get_constraintdef(oid, true) FROM pg_constraint WHERE conname = 'fp_dom_pos'"));
        Assert.Equal(["Domain billing.fp_dom: Different", "Domain m.code: Different"], left);

        await pg.ExecAsync(pair.Tgt, "UPDATE m.t SET c = 'abc'; UPDATE billing.fp_calc SET dom_val = 1;");
        var (fixedUp, again, none) = await RoundAsync(pair);
        Assert.Empty(again);
        Assert.Empty(none);
        // a changed rule is replaced in one file, added validated, so a value written since makes it change nothing
        var replaced = PgScriptPair.FilesOf(fixedUp, PgScriptGenerator.ConstraintsFolder).Select(f => File.ReadAllText(f.FullPath))
            .Single(t => t.Contains("DROP CONSTRAINT \"fp_dom_pos\"", StringComparison.Ordinal));
        Assert.Contains("ADD CONSTRAINT \"fp_dom_pos\" CHECK (VALUE > 0);", replaced);
    }

    protected async Task User_operators_are_objects_created_before_what_uses_them()
    {
        const string fn = "CREATE SCHEMA op; CREATE FUNCTION op.approx(numeric, numeric) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT abs($1 - $2) < 1 $$; ";
        await using var pair = await PgScriptPair.CreateAsync(pg, fn + """
            CREATE OPERATOR op.~= (LEFTARG = numeric, RIGHTARG = numeric, FUNCTION = op.approx, COMMUTATOR = OPERATOR(op.~=));
            CREATE TABLE op.t (a numeric, b numeric, CONSTRAINT t_chk CHECK (a OPERATOR(op.~=) b));
            CREATE VIEW op.v AS SELECT a OPERATOR(op.~=) b AS close FROM op.t;
            """, fn + "CREATE TABLE op.t (a numeric, b numeric);");
        Assert.Contains("Operator op.~= numeric, numeric: MissingInTarget", PgScriptPair.NotSame(await pair.CompareAsync()));
        var (_, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Equal(true, await pair.ScalarAsync("SELECT 1.2 OPERATOR(op.~=) 1.5"));
    }

    protected async Task Typed_tables_are_compared_and_created_of_their_type()
    {
        const string type = "CREATE SCHEMA ty; CREATE TYPE ty.c AS (a integer, b text); ";
        await using var pair = await PgScriptPair.CreateAsync(pg, type + """
            CREATE TABLE ty.t OF ty.c (PRIMARY KEY (a));
            CREATE TABLE ty.m OF ty.c (b WITH OPTIONS DEFAULT 'x');
            CREATE TABLE ty.p (a integer, b text);
            """, type + """
            CREATE TABLE ty.t (a integer NOT NULL, b text, PRIMARY KEY (a));
            CREATE TABLE ty.p OF ty.c;
            """);
        var before = PgScriptPair.NotSame(await pair.CompareAsync());
        Assert.Contains("Table ty.t: Different", before);
        Assert.Contains("Table ty.p: Different", before);
        var (_, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Equal("ty.c", await pair.ScalarAsync("SELECT reloftype::regtype::text FROM pg_class WHERE oid = 'ty.m'::regclass"));
    }

    protected async Task An_extra_enum_label_is_held_for_what_it_is()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, "CREATE SCHEMA core; CREATE TYPE core.priority_level AS ENUM ('low', 'normal');",
            "CREATE SCHEMA core; CREATE TYPE core.priority_level AS ENUM ('low', 'normal', 'critical');");
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), [ScriptSection.Constraints]);
        var held = Assert.Single(outcome.HeldBack, h => h.Object == "core.priority_level");
        Assert.Contains("'critical'", held.Reason);
        Assert.Contains("cannot remove a label", held.Reason);
    }

    protected async Task A_created_table_keeps_its_fill_factor()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, "CREATE SCHEMA sales; CREATE TABLE sales.fp_booking (id integer) WITH (fillfactor = 90);",
            "CREATE SCHEMA sales;");
        var (_, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Equal("fillfactor=90", await pair.ScalarAsync("SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = 'sales.fp_booking'::regclass"));
    }

    protected async Task Test_connection_counts_what_the_compare_lists()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE EXTENSION IF NOT EXISTS pgcrypto;
            CREATE SCHEMA app;
            CREATE TABLE app.a (id integer);
            CREATE VIEW app.v AS SELECT id FROM app.a;
            CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE TYPE app.r AS RANGE (subtype = int4);
            """, "SELECT 1");
        var test = await new PgConnectionService().TestAsync(pg.ConnectionString(pair.Src));
        Assert.True(test.Success, test.Message);
        // pgcrypto's functions and the range's constructors are not objects of the compare
        Assert.Equal(3, test.ObjectCount);
    }
}

[Collection(PgCollection.Name)]
public sealed class PgHardening2Tests(PgServer pg) : PgHardening2TestsBase(pg)
{
    [PostgresFact] public Task Aggregates_on_16() => Aggregates_keep_every_option_and_a_changed_one_is_replaced();
    [PostgresFact] public Task Defaults_on_16() => A_column_default_is_no_generated_column();
    [PostgresFact] public Task Column_order_on_16() => Columns_held_until_03_keep_the_source_s_order_over_two_runs();
    [PostgresFact] public Task Safe_changes_on_16() => Safe_metadata_changes_are_scripted();
    [PostgresFact] public Task Refused_changes_on_16() => Changes_postgres_refuses_on_a_table_are_held_with_why();
    [PostgresFact] public Task Partition_indexes_on_16() => Partition_indexes_come_once_and_a_partition_s_own_is_built();
    [PostgresFact] public Task Child_objects_on_16() => A_child_s_own_columns_checks_keys_indexes_and_defaults_are_its_own();
    [PostgresFact] public Task Partitioned_foreign_key_on_16() => A_partitioned_table_s_foreign_key_is_added_the_way_its_version_can();
    [PostgresFact] public Task Domain_checks_on_16() => A_domain_check_is_written_only_once_the_stored_values_meet_it();
    [PostgresFact] public Task Operators_on_16() => User_operators_are_objects_created_before_what_uses_them();
    [PostgresFact] public Task Typed_tables_on_16() => Typed_tables_are_compared_and_created_of_their_type();
    [PostgresFact] public Task Enum_label_on_16() => An_extra_enum_label_is_held_for_what_it_is();
    [PostgresFact] public Task Fill_factor_on_16() => A_created_table_keeps_its_fill_factor();
    [PostgresFact] public Task Test_count_on_16() => Test_connection_counts_what_the_compare_lists();
}

[Collection(Pg13Collection.Name)]
public sealed class Pg13Hardening2Tests(Pg13Server pg) : PgHardening2TestsBase(pg)
{
    [PostgresFact] public Task Aggregates_on_13() => Aggregates_keep_every_option_and_a_changed_one_is_replaced();
    [PostgresFact] public Task Defaults_on_13() => A_column_default_is_no_generated_column();
    [PostgresFact] public Task Column_order_on_13() => Columns_held_until_03_keep_the_source_s_order_over_two_runs();
    [PostgresFact] public Task Safe_changes_on_13() => Safe_metadata_changes_are_scripted();
    [PostgresFact] public Task Refused_changes_on_13() => Changes_postgres_refuses_on_a_table_are_held_with_why();
    [PostgresFact] public Task Partition_indexes_on_13() => Partition_indexes_come_once_and_a_partition_s_own_is_built();
    [PostgresFact] public Task Child_objects_on_13() => A_child_s_own_columns_checks_keys_indexes_and_defaults_are_its_own();
    [PostgresFact] public Task Partitioned_foreign_key_on_13() => A_partitioned_table_s_foreign_key_is_added_the_way_its_version_can();
    [PostgresFact] public Task Domain_checks_on_13() => A_domain_check_is_written_only_once_the_stored_values_meet_it();
    [PostgresFact] public Task Operators_on_13() => User_operators_are_objects_created_before_what_uses_them();
    [PostgresFact] public Task Typed_tables_on_13() => Typed_tables_are_compared_and_created_of_their_type();
    [PostgresFact] public Task Enum_label_on_13() => An_extra_enum_label_is_held_for_what_it_is();
    [PostgresFact] public Task Fill_factor_on_13() => A_created_table_keeps_its_fill_factor();
    [PostgresFact] public Task Test_count_on_13() => Test_connection_counts_what_the_compare_lists();
}

[Collection(Pg18Collection.Name)]
public sealed class Pg18Hardening2Tests(Pg18Server pg) : PgHardening2TestsBase(pg)
{
    [PostgresFact] public Task Aggregates_on_18() => Aggregates_keep_every_option_and_a_changed_one_is_replaced();
    [PostgresFact] public Task Defaults_on_18() => A_column_default_is_no_generated_column();
    [PostgresFact] public Task Column_order_on_18() => Columns_held_until_03_keep_the_source_s_order_over_two_runs();
    [PostgresFact] public Task Safe_changes_on_18() => Safe_metadata_changes_are_scripted();
    [PostgresFact] public Task Refused_changes_on_18() => Changes_postgres_refuses_on_a_table_are_held_with_why();
    [PostgresFact] public Task Partition_indexes_on_18() => Partition_indexes_come_once_and_a_partition_s_own_is_built();
    [PostgresFact] public Task Child_objects_on_18() => A_child_s_own_columns_checks_keys_indexes_and_defaults_are_its_own();
    [PostgresFact] public Task Partitioned_foreign_key_on_18() => A_partitioned_table_s_foreign_key_is_added_the_way_its_version_can();
    [PostgresFact] public Task Domain_checks_on_18() => A_domain_check_is_written_only_once_the_stored_values_meet_it();
    [PostgresFact] public Task Operators_on_18() => User_operators_are_objects_created_before_what_uses_them();
    [PostgresFact] public Task Typed_tables_on_18() => Typed_tables_are_compared_and_created_of_their_type();
    [PostgresFact] public Task Enum_label_on_18() => An_extra_enum_label_is_held_for_what_it_is();
    [PostgresFact] public Task Fill_factor_on_18() => A_created_table_keeps_its_fill_factor();
    [PostgresFact] public Task Test_count_on_18() => Test_connection_counts_what_the_compare_lists();

    /// <summary>PostgreSQL 18 only: NOT NULL ... NOT VALID.</summary>
    [PostgresFact]
    public async Task A_not_valid_not_null_is_not_read_as_a_validated_one_on_18()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, "CREATE SCHEMA n; CREATE TABLE n.u (b integer NOT NULL);", """
            CREATE SCHEMA n;
            CREATE TABLE n.u (b integer);
            INSERT INTO n.u VALUES (NULL), (1);
            ALTER TABLE n.u ADD CONSTRAINT u_b_not_null NOT NULL b NOT VALID;
            """);
        Assert.Equal(["Table n.u: Different"], PgScriptPair.NotSame(await pair.CompareAsync()));
        var held = await pair.GenerateAsync(await pair.CompareAsync());
        Assert.Contains(held.HeldBack, h => h.Object == "n.u.b" && h.Reason.Contains("hold a NULL"));

        await pg.ExecAsync(pair.Tgt, "DELETE FROM n.u WHERE b IS NULL");
        var outcome = await pair.GenerateAsync(await pair.CompareAsync());
        Assert.Empty(await PgHardeningTestsBase.ApplyAsync(pg, pair.Tgt, outcome));
        Assert.Empty(PgScriptPair.NotSame(await pair.CompareAsync()));
        Assert.Equal(true, await pair.ScalarAsync("SELECT convalidated FROM pg_constraint WHERE conname = 'u_b_not_null'"));
    }
}

/// <summary>Round 2's defects in the owner API with a real PostgreSQL behind it.</summary>
[Collection(PgCollection.Name)]
public sealed class PgHardening2ApiTests(PgServer pg)
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [PostgresFact]
    public async Task Script_this_object_creates_a_table_s_missing_rule()
    {
        const string tables = "CREATE SCHEMA r; CREATE TABLE r.t (id integer); ";
        await using var pair = await PgScriptPair.CreateAsync(pg, tables + "CREATE RULE t_nodel AS ON DELETE TO r.t DO INSTEAD NOTHING;", tables);
        await using var o = await OwnerApp.StartAsync();
        await o.SetPreviewAsync(true);
        var run = await PgApplyTestsBase.CompareAsync(o, pg.Settings(pair.Src), pg.Settings(pair.Tgt));
        Assert.Equal(HttpStatusCode.OK, (await o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);
        var r = await o.Post($"/api/compare/{run.GetProperty("runId").GetString()}/object-script",
            new { targetIndex = 0, key = "1|r.t", includeDependencies = Array.Empty<string>() });
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, text);
        var outcome = Body(text);
        Assert.True(outcome.GetProperty("success").GetBoolean(), text);
        Assert.Equal(0, outcome.GetProperty("heldBack").GetArrayLength());
        Assert.Contains("CREATE RULE t_nodel AS", outcome.GetProperty("script").GetString());
        Assert.False(string.IsNullOrEmpty(outcome.GetProperty("planId").GetString()), text);
    }

    [PostgresFact]
    public async Task History_shows_a_raw_string_target_s_host_and_database_and_a_protected_neighbour_is_named()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, "CREATE SCHEMA h; CREATE TABLE h.t (id integer, extra integer);", "CREATE SCHEMA h; CREATE TABLE h.t (id integer);");
        await using var o = await OwnerApp.StartAsync();
        await o.SetPreviewAsync(true);
        // another connection on the same host, another port, marked Protected: it protects this target too
        Assert.Equal(HttpStatusCode.OK, (await o.Post("/api/connections", new
        {
            engine = "postgreSql", label = "Neighbour", server = pg.Host, port = 1, database = "x", auth = "password", userName = "u",
            password = "p", ssl = "disable", trustServerCertificate = true, protectedServer = true,
        })).StatusCode);
        var raw = new
        {
            engine = "postgreSql", label = "Raw target", auth = "password", useRawConnectionString = true,
            rawConnectionString = pg.ConnectionString(pair.Tgt),
        };
        var run = await PgApplyTestsBase.CompareAsync(o, pg.Settings(pair.Src), raw);

        var history = Body(await o.Http.GetStringAsync("/api/history"));
        var target = history[0].GetProperty("targets")[0];
        Assert.Equal(pg.Host, target.GetProperty("server").GetString());
        Assert.Equal(pair.Tgt, target.GetProperty("database").GetString());
        Assert.Equal(pg.Port, target.GetProperty("port").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);
        var scripts = await o.Post($"/api/compare/{run.GetProperty("runId").GetString()}/scripts", new
        {
            targetIndex = 0, options = new { outputFolder = Path.Combine(o.Temp.Folder, "scripts"), sections = new[] { "newColumns" } },
        });
        var applyTarget = Body(await scripts.Content.ReadAsStringAsync()).GetProperty("applyTarget");
        Assert.True(applyTarget.GetProperty("protected").GetBoolean());
        Assert.False(applyTarget.GetProperty("protectedItself").GetBoolean());
    }
}
