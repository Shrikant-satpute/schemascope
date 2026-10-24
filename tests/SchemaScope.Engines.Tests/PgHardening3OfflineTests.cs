using System.Text;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;
using SchemaScope.SqlServer;

namespace SchemaScope.Engines.Tests;

/// <summary>Hardening round 3's defects that need no server: named pipes under Protected, the apply file list, text helpers.</summary>
public sealed class PgHardening3OfflineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"schemascope-pg-r3-{Guid.NewGuid():n}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------- a protected SQL Server connection written as a named pipe

    [Theory]
    [InlineData(@"np:\\localhost\pipe\sql\query", "localhost")]
    [InlineData(@"np:\\.\pipe\sql\query", "127.0.0.1")]
    [InlineData(@"\\PROD\pipe\MSSQL$A\sql\query", "prod.corp.local")]
    [InlineData(@"np:\\PROD\pipe\some\other\path", "PROD")]
    public void A_named_pipe_protects_postgres_on_its_host(string saved, string pgHost)
    {
        var c = new ConnectionSettings { Server = saved, ProtectedServer = true };
        Assert.True(SchemaScopeApi.Covers(c, DatabaseEngine.PostgreSql, [pgHost]));
        Assert.False(SchemaScopeApi.Covers(c, DatabaseEngine.PostgreSql, ["staging"]));
    }

    [Fact]
    public void A_named_pipe_names_its_instance_as_the_server_names_itself()
    {
        var machine = Environment.MachineName.ToUpperInvariant();
        Assert.Equal(machine, SqlServerIdentity.Normalise(@"np:\\localhost\pipe\sql\query"));
        Assert.Equal("PROD\\A", SqlServerIdentity.Normalise(@"np:\\prod.corp.local\pipe\MSSQL$A\sql\query"));
        Assert.Equal($"{machine}\\LOCALDB#0ACF7B5F", SqlServerIdentity.Normalise(@"np:\\.\pipe\LOCALDB#0ACF7B5F\tsql\query"));

        // the LocalDB pipe protects the instance it reaches, as that server reports itself
        var localDb = new ConnectionSettings { Server = @"np:\\.\pipe\LOCALDB#0ACF7B5F\tsql\query", ProtectedServer = true };
        Assert.True(SchemaScopeApi.Covers(localDb, DatabaseEngine.SqlServer, [$"{Environment.MachineName}\\LOCALDB#0ACF7B5F"]));
        Assert.False(SchemaScopeApi.Covers(localDb, DatabaseEngine.SqlServer, [$"{Environment.MachineName}\\SQL2025"]));
        var instance = new ConnectionSettings { Server = @"\\PROD\pipe\MSSQL$A\sql\query", ProtectedServer = true };
        Assert.True(SchemaScopeApi.Covers(instance, DatabaseEngine.SqlServer, ["PROD\\A"]));
        Assert.False(SchemaScopeApi.Covers(instance, DatabaseEngine.SqlServer, ["PROD\\B"]));
    }

    [Theory]
    [InlineData(@"np:\\PROD\pipe\custom\path")]
    [InlineData(@"np:\\PROD\notapipe")]
    public void A_pipe_of_no_known_instance_protects_every_instance_on_its_machine(string saved)
    {
        var c = new ConnectionSettings { Server = saved, ProtectedServer = true };
        Assert.True(SchemaScopeApi.Covers(c, DatabaseEngine.SqlServer, ["PROD\\B"]));
        Assert.True(SchemaScopeApi.Covers(c, DatabaseEngine.SqlServer, ["prod"]));
        Assert.False(SchemaScopeApi.Covers(c, DatabaseEngine.SqlServer, ["STAGING"]));
    }

    [Theory]
    [InlineData(@"np:\\\pipe\sql\query")]
    [InlineData(@"np:")]
    public void A_pipe_no_machine_can_be_read_from_protects_everything(string saved)
    {
        var c = new ConnectionSettings { Server = saved, ProtectedServer = true };
        Assert.True(SchemaScopeApi.Covers(c, DatabaseEngine.PostgreSql, ["anything"]));
        Assert.True(SchemaScopeApi.Covers(c, DatabaseEngine.SqlServer, ["ANY\\ONE"]));
    }

    // ---------------- the apply file list names a schema's file by the schema

    [Fact]
    public void A_schema_file_is_listed_under_the_schema_s_name()
    {
        var full = Path.Combine(_root, "03_missing", "objects", "001_schema_guest.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "CREATE SCHEMA \"guest\";\n", new UTF8Encoding(false));
        var schema = new WrittenFile("03_missing", "objects\\001_schema_guest.sql", full, WrittenFileKind.Statements)
        {
            Object = new ObjectName("guest", ""), Sha256 = ApplyPlans.Hash(full),
        };
        var table = PgPlanFiles.Write(_root, "03_missing", "002_table_app.t.sql", "CREATE TABLE app.t (id integer);\n", "t");
        var (plan, _) = ApplyPlans.Build([schema, table], []);
        Assert.Equal(["guest", "app.t"], plan.Select(f => f.ObjectName));
    }

    // ---------------- text helpers

    [Theory]
    [InlineData("CREATE INDEX i ON s.t USING btree (c COLLATE \"C\")", "CREATE INDEX i ON s.t USING btree (c)")]
    [InlineData("CREATE INDEX i ON s.t USING btree (lower(c) COLLATE pg_catalog.\"default\" DESC)", "CREATE INDEX i ON s.t USING btree (lower(c) DESC)")]
    [InlineData("CREATE INDEX i ON s.t USING btree (c) WHERE (c <> 'COLLATE \"C\"'::text)", "CREATE INDEX i ON s.t USING btree (c) WHERE (c <> 'COLLATE \"C\"'::text)")]
    [InlineData("CREATE INDEX \"COLLATE x\" ON s.t USING btree (c)", "CREATE INDEX \"COLLATE x\" ON s.t USING btree (c)")]
    public void An_index_s_collate_clauses_go_only_outside_literals_and_names(string definition, string expected)
    {
        Assert.Equal(expected, PgCanonicalWriter.IndexText(definition, ignoreCollation: true));
        Assert.Equal(definition, PgCanonicalWriter.IndexText(definition, ignoreCollation: false));
    }

    [Theory]
    [InlineData("integer[]", "bigint[]", true)]
    [InlineData("character varying(10)[]", "character varying(20)[]", true)]
    [InlineData("character varying(10)[]", "text[]", true)]
    [InlineData("bigint[]", "integer[]", null)]
    [InlineData("integer[]", "bigint", null)]
    [InlineData("integer", "bigint[]", null)]
    public void An_array_widens_as_its_element_type_does(string from, string to, bool? expected) =>
        Assert.Equal(expected, PgScriptRun.Widening(from, to));

    [Theory]
    [InlineData("RANGE (at)", "FOR VALUES FROM ('2027-01-01') TO ('2028-01-01')", "at IS NOT NULL AND at >= '2027-01-01' AND at < '2028-01-01'")]
    [InlineData("RANGE (at)", "FOR VALUES FROM (MINVALUE) TO ('2028-01-01')", "at IS NOT NULL AND at < '2028-01-01'")]
    [InlineData("LIST (k)", "FOR VALUES IN ('b', NULL)", "k IN ('b') OR k IS NULL")]
    [InlineData("LIST (k)", "FOR VALUES IN ('a,b')", "k IN ('a,b')")]
    [InlineData("RANGE (a, b)", "FOR VALUES FROM (1, 2) TO (3, 4)", null)]
    [InlineData("RANGE (lower(c))", "FOR VALUES FROM ('a') TO ('b')", null)]
    [InlineData("HASH (id)", "FOR VALUES WITH (modulus 4, remainder 0)", null)]
    public void A_partition_bound_becomes_a_predicate_on_its_key(string partitionBy, string bound, string? expected) =>
        Assert.Equal(expected, PgScriptRun.BoundPredicate(partitionBy, bound));

    [Fact]
    public void A_foreign_table_is_written_with_its_server_and_options()
    {
        var o = new DbObject { Type = DbObjectType.ForeignTable, Schema = "fv", Name = "remote", Fold = IdentifierFold.Exact };
        o.Columns.Add(new ColumnInfo { Name = "id", Ordinal = 1, DataType = "integer", IsNullable = false });
        o.Columns.Add(new ColumnInfo { Name = "name", Ordinal = 2, DataType = "text", IsNullable = true });
        o.Properties["server"] = "hunt_srv";
        o.Properties["options"] = "\"table_name\" 'r''x'";
        o.Properties[PgCanonicalWriter.ColumnOptionsPrefix + "name"] = "\"column_name\" 'n'";
        Assert.Equal("CREATE FOREIGN TABLE \"fv\".\"remote\" (\n    \"id\" integer NOT NULL,\n    \"name\" text NULL OPTIONS (\"column_name\" 'n')\n) " +
                     "SERVER hunt_srv OPTIONS (\"table_name\" 'r''x');", PgCanonicalWriter.WriteForeignTable(o, new CompareOptions()));
    }
}
