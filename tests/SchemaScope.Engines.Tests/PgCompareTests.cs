using System.Text.RegularExpressions;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>The reader and the compare against a real PostgreSQL 16, one throwaway database pair per test.</summary>
[Collection(PgCollection.Name)]
public sealed class PgCompareTests(PgServer pg)
{
    internal static async Task<CompareRun> RunScenarioAsync(PgServer pg, Func<CompareRun, Task> check)
    {
        var src = await pg.CreateDatabaseAsync("src");
        var tgt = await pg.CreateDatabaseAsync("tgt");
        var locked = await pg.CreateDatabaseAsync("locked");
        var role = await pg.CreateRoleAsync();
        try
        {
            await pg.ExecAsync(src, PgScenario.Source);
            await pg.ExecAsync(tgt, PgScenario.Target);
            await pg.ExecAsync(tgt, $"GRANT CONNECT ON DATABASE \"{tgt}\" TO \"{role.Name}\"; GRANT USAGE ON SCHEMA app TO \"{role.Name}\";");
            // the second target: this login may not connect to it at all
            await pg.ExecAsync(locked, $"REVOKE CONNECT ON DATABASE \"{locked}\" FROM PUBLIC;");

            var runner = new CompareRunner(_ => new PgSchemaReader());
            var run = await runner.RunAsync(pg.Source("source", src),
                [pg.Source("t0", tgt, role.Name, role.Password), pg.Source("t1", locked, role.Name, role.Password)],
                new CompareOptions());
            await check(run);
            return run;
        }
        finally
        {
            await pg.DropAsync(src);
            await pg.DropAsync(tgt);
            await pg.DropAsync(locked);
            await pg.DropRoleAsync(role.Name);
        }
    }

    internal static CompareRow Row(CompareResult r, DbObjectType type, string name, string? signature = null) =>
        r.Rows.Single(x => x.Type == type && x.Schema == "app" && x.Name == name && x.Signature == signature);

    internal static void AssertSixStatuses(CompareRun run)
    {
        var r = run.Result;
        Assert.All(run.Targets.Take(1), t => Assert.False(t.Failed, t.Error));
        Assert.True(run.Targets[1].Failed);
        Assert.Equal(DatabaseEngine.PostgreSql, r.Source.Engine);

        ObjectStatus At(CompareRow row, int target = 0) => row.Cells[target].Status;

        // Same
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.Table, "customers")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.View, "v_orders")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.MaterializedView, "mv_totals")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.ScalarFunction, "add", "integer, integer")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.StoredProcedure, "archive", "integer")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.AggregateFunction, "total_sum", "numeric")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.Trigger, "orders_touch", "ON orders")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.Sequence, "ticket_seq")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.Sequence, "orders_id_seq")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.EnumType, "mood")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.Domain, "positive")));
        Assert.Equal(ObjectStatus.Same, At(Row(r, DbObjectType.CompositeType, "pair")));
        Assert.Equal(ObjectStatus.Same, At(r.Rows.Single(x => x.Type == DbObjectType.Schema && x.Name == "app")));

        // FormattingOnly: layout and a comment, and never by a parse (there is no PostgreSQL parser here)
        var fmt = Row(r, DbObjectType.ScalarFunction, "fmt", "integer");
        Assert.Equal(ObjectStatus.FormattingOnly, At(fmt));
        Assert.Equal("Only whitespace, comments or casing differ", fmt.Cells[0].Note);

        // Different
        Assert.Equal(ObjectStatus.Different, At(Row(r, DbObjectType.Table, "orders")));
        Assert.Equal(ObjectStatus.Different, At(Row(r, DbObjectType.ScalarFunction, "changed", "")));

        // Missing / Extra
        Assert.Equal(ObjectStatus.MissingInTarget, At(Row(r, DbObjectType.View, "v_missing")));
        Assert.Equal(ObjectStatus.MissingInTarget, At(Row(r, DbObjectType.ScalarFunction, "add", "text, text")));
        Assert.Equal(ObjectStatus.OnlyInTarget, At(Row(r, DbObjectType.Table, "extra")));

        // Unavailable: the database that could not be read
        Assert.All(r.Rows, row => Assert.Equal(ObjectStatus.Unavailable, row.Cells[1].Status));

        var t0 = r.Targets[0];
        Assert.True(t0.Same > 0 && t0.FormattingOnly == 1 && t0.Different == 2 && t0.MissingInTarget == 2 && t0.OnlyInTarget == 1,
            $"same {t0.Same}, formatting {t0.FormattingOnly}, different {t0.Different}, missing {t0.MissingInTarget}, extra {t0.OnlyInTarget}");
        Assert.Equal(0, t0.Unavailable);
        Assert.Equal(r.Rows.Count, r.Targets[1].Unavailable);
    }

    [PostgresFact]
    public async Task The_six_statuses_come_out_of_a_drifted_pair() =>
        await RunScenarioAsync(pg, run =>
        {
            AssertSixStatuses(run);
            return Task.CompletedTask;
        });

    [PostgresFact]
    public async Task Case_twins_and_overloads_are_separate_objects() =>
        await RunScenarioAsync(pg, run =>
        {
            var r = run.Result;
            var tables = r.Rows.Where(x => x.Type == DbObjectType.Table && x.Name.Equals("users", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal(["Users", "users"], tables.Select(t => t.Name).Order(StringComparer.Ordinal));
            Assert.All(tables, t => Assert.Equal(ObjectStatus.Same, t.Cells[0].Status));
            Assert.Equal(2, r.Rows.Count(x => x.Name == "add"));
            Assert.DoesNotContain(r.Warnings, w => w.Contains("one name when case is ignored"));
            // the case twins' keys keep their case
            Assert.Contains("1|app.Users", tables.Select(t => t.Key));
            return Task.CompletedTask;
        });

    [PostgresFact]
    public async Task Extension_members_are_not_objects_but_the_extension_is() =>
        await RunScenarioAsync(pg, run =>
        {
            var objects = run.Source.Objects;
            Assert.DoesNotContain(objects, o => o.Name is "digest" or "crypt" or "gen_salt" or "pgp_sym_encrypt");
            var ext = Assert.Single(objects, o => o.Type == DbObjectType.Extension && o.Name == "pgcrypto");
            Assert.Equal("public", ext.Properties["schema"]);
            Assert.Equal(ObjectStatus.Same, run.Result.Rows.Single(x => x.Key == ext.Key).Cells[0].Status);
            // nothing from the catalog either
            Assert.DoesNotContain(objects, o => o.Schema is "pg_catalog" or "information_schema" || o.Schema.StartsWith("pg_toast"));
            return Task.CompletedTask;
        });

    [PostgresFact]
    public async Task The_model_reads_identity_generated_columns_keys_and_indexes()
    {
        await RunScenarioAsync(pg, run =>
        {
            var orders = run.Source.Objects.Single(o => o.Type == DbObjectType.Table && o.Name == "orders");
            var customers = run.Source.Objects.Single(o => o.Type == DbObjectType.Table && o.Name == "customers");

            var id = customers.Columns.Single(c => c.Name == "id");
            Assert.True(id.IsIdentity);
            Assert.Equal("ALWAYS", id.IdentityKind);
            Assert.Equal("1", id.IdentitySeed);

            var doubled = orders.Columns.Single(c => c.Name == "doubled");
            Assert.True(doubled.IsComputed && doubled.IsPersisted);
            Assert.Equal("((qty)::integer * 2)", doubled.ComputedDefinition);
            Assert.Equal("app.positive", orders.Columns.Single(c => c.Name == "qty").DataType);
            Assert.Equal("numeric(10,2)", orders.Columns.Single(c => c.Name == "total").DataType);

            Assert.Equal("PRIMARY KEY (id)", orders.Indexes.Single(i => i.IsPrimaryKey).Definition);
            Assert.True(orders.Indexes.Single(i => i.IsPrimaryKey).IsSystemNamed);
            var fk = Assert.Single(orders.ForeignKeys);
            Assert.Equal("CASCADE", fk.DeleteAction);
            Assert.Equal("FOREIGN KEY (customer_id) REFERENCES app.customers(id) ON DELETE CASCADE", fk.Definition);
            var ix = orders.Indexes.Single(i => i.Name == "orders_customer_ix");
            Assert.Equal(["customer_id"], ix.KeyColumns.Select(k => k.Name));
            Assert.Equal(["total"], ix.IncludedColumns);
            Assert.Equal("(total > (0)::numeric)", ix.FilterDefinition);
            Assert.Contains(orders.Constraints, c => c.Kind == ConstraintKind.Check && c.Definition == "CHECK (total >= 0::numeric)");
            Assert.Contains(orders.Constraints, c => c.Kind == ConstraintKind.Default && c.ColumnName == "id" && c.Definition.StartsWith("nextval('app.orders_id_seq'"));

            // the identity column's own sequence belongs to the column; the serial's sequence is an object of its own
            Assert.DoesNotContain(run.Source.Objects, o => o.Type == DbObjectType.Sequence && o.Name == "customers_id_seq");
            Assert.Equal("app.orders.id", run.Source.Objects.Single(o => o.Name == "orders_id_seq").Properties["ownedBy"]);

            Assert.Equal("16", run.Source.ProductVersion.Split('.')[0]);
            Assert.Equal(IdentifierFold.Exact, orders.Fold);
            return Task.CompletedTask;
        });
    }

    /// <summary>Every table, view, materialized view, sequence and routine pg_dump --schema-only knows, the reader read - and no more.</summary>
    [PostgresFact]
    public async Task The_objects_agree_with_pg_dump()
    {
        var db = await pg.CreateDatabaseAsync("dump");
        try
        {
            await pg.ExecAsync(db, PgScenario.Source);
            var snapshot = await new PgSchemaReader().ReadAsync(pg.Source("s", db), new CompareOptions());
            var (exit, dump, err) = await pg.ExecInContainerAsync("pg_dump", "--schema-only", "--no-owner", "-U", PgServer.User, db);
            Assert.True(exit == 0, err);

            static string Unquote(string n) => n.Replace("\"", "");
            HashSet<string> FromDump(string pattern) =>
                [.. Regex.Matches(dump, pattern, RegexOptions.Multiline).Select(m => Unquote(m.Groups[1].Value))];
            HashSet<string> FromReader(params DbObjectType[] types) =>
                [.. snapshot.Objects.Where(o => types.Contains(o.Type)).Select(o => o.FullName)];

            Assert.Equal(FromDump(@"^CREATE TABLE ([\w.""]+) \("), FromReader(DbObjectType.Table));
            Assert.Equal(FromDump(@"^CREATE VIEW ([\w.""]+) AS"), FromReader(DbObjectType.View));
            Assert.Equal(FromDump(@"^CREATE MATERIALIZED VIEW ([\w.""]+) AS"), FromReader(DbObjectType.MaterializedView));
            Assert.Equal(FromDump(@"^CREATE SEQUENCE ([\w.""]+)$"), FromReader(DbObjectType.Sequence));
            Assert.Equal(FromDump(@"^CREATE (?:FUNCTION|PROCEDURE|AGGREGATE) ([\w.""]+)\("),
                FromReader(DbObjectType.ScalarFunction, DbObjectType.TableValuedFunction, DbObjectType.StoredProcedure, DbObjectType.AggregateFunction));
            Assert.Equal(FromDump(@"^CREATE TRIGGER (\w+) "), [.. snapshot.Objects.Where(o => o.Type == DbObjectType.Trigger).Select(o => o.Name)]);
            Assert.Equal(FromDump(@"^CREATE TYPE ([\w.""]+) AS"),
                FromReader(DbObjectType.EnumType, DbObjectType.CompositeType));
            Assert.Equal(FromDump(@"^CREATE DOMAIN ([\w.""]+) AS"), FromReader(DbObjectType.Domain));
            Assert.Equal(FromDump(@"^CREATE EXTENSION IF NOT EXISTS (\w+) "), [.. snapshot.Objects.Where(o => o.Type == DbObjectType.Extension).Select(o => o.Name).Where(n => n != "plpgsql")]);
        }
        finally
        {
            await pg.DropAsync(db);
        }
    }
}

/// <summary>The same scenario on PostgreSQL 18, the newest.</summary>
[Collection(Pg18Collection.Name)]
public sealed class Pg18CompareTests(Pg18Server pg)
{
    [PostgresFact]
    public async Task The_six_statuses_come_out_on_postgres_18() =>
        await PgCompareTests.RunScenarioAsync(pg, run =>
        {
            PgCompareTests.AssertSixStatuses(run);
            Assert.Equal("18", run.Source.ProductVersion.Split('.')[0]);
            return Task.CompletedTask;
        });
}

/// <summary>The same scenario on PostgreSQL 13, the oldest version the reader is written for.</summary>
[Collection(Pg13Collection.Name)]
public sealed class Pg13CompareTests(Pg13Server pg)
{
    [PostgresFact]
    public async Task The_six_statuses_come_out_on_postgres_13() =>
        await PgCompareTests.RunScenarioAsync(pg, run =>
        {
            PgCompareTests.AssertSixStatuses(run);
            Assert.Equal("13", run.Source.ProductVersion.Split('.')[0]);
            return Task.CompletedTask;
        });
}
