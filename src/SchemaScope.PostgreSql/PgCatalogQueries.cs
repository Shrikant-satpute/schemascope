namespace SchemaScope.PostgreSql;

/// <summary>
/// The pg_catalog queries behind <see cref="PgSchemaReader"/>, for PostgreSQL 13 and later. They run in one READ ONLY
/// transaction with search_path set to pg_catalog alone, so the server's deparse (format_type, pg_get_expr,
/// pg_get_*def) names every user object with its schema, the same way on every server.
///
/// System schemas (pg_catalog, information_schema, pg_toast*, pg_temp_*) are never read, and nor is anything an
/// extension created (pg_depend deptype 'e'): an extension is compared as one object, by name and version.
/// </summary>
internal static class PgCatalogQueries
{
    /// <summary>A schema the user made: not the catalog, not TOAST, not a session's temporary schema.</summary>
    private static string UserSchema(string ns) =>
        $"({ns}.nspname NOT IN ('pg_catalog', 'information_schema') AND {ns}.nspname NOT LIKE 'pg\\_toast%' " +
        $"AND {ns}.nspname NOT LIKE 'pg\\_temp\\_%')";

    /// <summary>Not created by an extension.</summary>
    private static string NotExtensionMember(string catalog, string oid) =>
        $"NOT EXISTS (SELECT 1 FROM pg_depend xd WHERE xd.classid = '{catalog}'::regclass AND xd.objid = {oid} AND xd.deptype = 'e')";

    public const string ListDatabases = """
        SELECT datname, has_database_privilege(datname, 'CONNECT')
        FROM pg_database
        WHERE datallowconn AND NOT datistemplate
        ORDER BY datname;
        """;

    public static readonly string TestInfo = $"""
        SELECT current_setting('server_version'), current_database(),
               (SELECT datcollate::text FROM pg_database WHERE datname = current_database()),
               (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'v', 'm') AND {UserSchema("n")})
             + (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE {UserSchema("n")});
        """;

    public const string ServerInfo = """
        SELECT current_setting('server_version'), current_setting('server_version_num')::int, current_database(),
               (SELECT datcollate::text FROM pg_database WHERE datname = current_database());
        """;

    /// <summary>
    /// Every catalog set in one round trip, in this order: schemas, relations, columns, constraints, indexes,
    /// routines, triggers, sequences, types, extensions.
    /// </summary>
    public static string Catalog(int versionNum) => string.Join("\n", Schemas, Relations, Columns(versionNum), Constraints,
        Indexes, Routines, Triggers, Sequences, Types, Extensions);

    private static readonly string Schemas = $"""
        SELECT n.nspname, pg_get_userbyid(n.nspowner)
        FROM pg_namespace n
        WHERE {UserSchema("n")} AND {NotExtensionMember("pg_namespace", "n.oid")};
        """;

    // r table, p partitioned table, v view, m materialized view
    private static readonly string Relations = $"""
        SELECT c.oid, n.nspname, c.relname, c.relkind::text,
               CASE WHEN c.relkind IN ('v', 'm') THEN pg_get_viewdef(c.oid, true) END
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'v', 'm') AND {UserSchema("n")} AND {NotExtensionMember("pg_class", "c.oid")};
        """;

    /// <summary>
    /// Columns of tables and of standalone composite types (relkind 'c'). attgenerated: 's' stored, and from
    /// PostgreSQL 18 'v' virtual. An identity column's sequence hangs off it with deptype 'i'.
    /// </summary>
    private static string Columns(int versionNum) => $"""
        SELECT a.attrelid, a.attnum, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull,
               a.attidentity::text, {(versionNum >= 120000 ? "a.attgenerated::text" : "''::text")},
               pg_get_expr(ad.adbin, ad.adrelid),
               CASE WHEN a.attcollation <> 0 AND a.attcollation <> t.typcollation THEN
                    CASE WHEN cn.nspname = 'pg_catalog' THEN quote_ident(co.collname)
                         ELSE quote_ident(cn.nspname) || '.' || quote_ident(co.collname) END END,
               s.seqstart, s.seqincrement
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_type t ON t.oid = a.atttypid
        LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
        LEFT JOIN pg_collation co ON co.oid = a.attcollation
        LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
        LEFT JOIN pg_depend sd ON sd.refclassid = 'pg_class'::regclass AND sd.refobjid = a.attrelid
             AND sd.refobjsubid = a.attnum AND sd.classid = 'pg_class'::regclass AND sd.deptype = 'i' AND a.attidentity <> ''
        LEFT JOIN pg_sequence s ON s.seqrelid = sd.objid
        WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p', 'c') AND {UserSchema("n")}
        ORDER BY a.attrelid, a.attnum;
        """;

    // p primary key, u unique, f foreign key, c check, x exclusion; PostgreSQL 18's NOT NULL rows ('n') are columns' attnotnull
    private static readonly string Constraints = $"""
        SELECT co.conrelid, co.contypid, co.conname, co.contype::text, pg_get_constraintdef(co.oid, true), co.convalidated,
               ARRAY(SELECT a.attname::text FROM unnest(co.conkey) WITH ORDINALITY k(num, ord)
                     JOIN pg_attribute a ON a.attrelid = co.conrelid AND a.attnum = k.num ORDER BY k.ord),
               rn.nspname, rc.relname,
               ARRAY(SELECT a.attname::text FROM unnest(co.confkey) WITH ORDINALITY k(num, ord)
                     JOIN pg_attribute a ON a.attrelid = co.confrelid AND a.attnum = k.num ORDER BY k.ord),
               co.confdeltype::text, co.confupdtype::text
        FROM pg_constraint co
        JOIN pg_namespace n ON n.oid = co.connamespace
        LEFT JOIN pg_class rc ON rc.oid = co.confrelid
        LEFT JOIN pg_namespace rn ON rn.oid = rc.relnamespace
        WHERE co.contype IN ('p', 'u', 'f', 'c', 'x') AND {UserSchema("n")} AND co.conparentid = 0
          AND (co.conrelid <> 0 OR co.contypid <> 0);
        """;

    // indexes that do not back a key or exclusion constraint (those are read as constraints above)
    private static readonly string Indexes = $"""
        SELECT i.indrelid, ic.relname, am.amname, i.indisunique, pg_get_indexdef(i.indexrelid),
               pg_get_expr(i.indpred, i.indrelid), i.indnkeyatts,
               ARRAY(SELECT pg_get_indexdef(i.indexrelid, k, true) FROM generate_series(1, i.indnatts) k ORDER BY k),
               ARRAY(SELECT (i.indoption[k - 1] & 1) = 1 FROM generate_series(1, i.indnatts) k ORDER BY k)
        FROM pg_index i
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_class tc ON tc.oid = i.indrelid
        JOIN pg_namespace n ON n.oid = tc.relnamespace
        JOIN pg_am am ON am.oid = ic.relam
        WHERE tc.relkind IN ('r', 'p', 'm') AND {UserSchema("n")}
          AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid AND c.contype IN ('p', 'u', 'x'))
          AND {NotExtensionMember("pg_class", "i.indexrelid")};
        """;

    /// <summary>
    /// Functions, procedures and aggregates. pg_get_functiondef refuses aggregates, so theirs is put together here
    /// from pg_aggregate. The overload signature is the argument types (oidvectortypes), what PostgreSQL itself keys a
    /// routine on.
    /// </summary>
    private static readonly string Routines = $"""
        SELECT p.oid, n.nspname, p.proname, p.prokind::text, p.proretset, oidvectortypes(p.proargtypes),
               CASE WHEN p.prokind <> 'a' THEN pg_get_functiondef(p.oid)
                    ELSE 'CREATE AGGREGATE ' || quote_ident(n.nspname) || '.' || quote_ident(p.proname)
                         || '(' || pg_get_function_identity_arguments(p.oid) || ') ('
                         || E'\n    SFUNC = ' || ag.aggtransfn::regprocedure::text
                         || E',\n    STYPE = ' || format_type(ag.aggtranstype, NULL)
                         || CASE WHEN ag.aggfinalfn::oid <> 0 THEN E',\n    FINALFUNC = ' || ag.aggfinalfn::regprocedure::text ELSE '' END
                         || CASE WHEN ag.aggcombinefn::oid <> 0 THEN E',\n    COMBINEFUNC = ' || ag.aggcombinefn::regprocedure::text ELSE '' END
                         || CASE WHEN ag.agginitval IS NOT NULL THEN E',\n    INITCOND = ' || quote_literal(ag.agginitval) ELSE '' END
                         || CASE WHEN ag.aggsortop::oid <> 0 THEN E',\n    SORTOP = ' || ag.aggsortop::regoperator::text ELSE '' END
                         || E'\n);' END,
               pg_get_function_arguments(p.oid), pg_get_function_result(p.oid)
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        LEFT JOIN pg_aggregate ag ON ag.aggfnoid = p.oid
        WHERE {UserSchema("n")} AND {NotExtensionMember("pg_proc", "p.oid")};
        """;

    // user triggers only: not the internal ones behind foreign keys, not the clones a partition gets from its parent
    private static readonly string Triggers = $"""
        SELECT n.nspname, c.relname, t.tgname, pg_get_triggerdef(t.oid, true), t.tgenabled::text
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE NOT t.tgisinternal AND t.tgparentid = 0 AND {UserSchema("n")} AND {NotExtensionMember("pg_class", "c.oid")};
        """;

    // an identity column's own sequence (deptype 'i') belongs to the column, so it is not listed on its own
    private static readonly string Sequences = $"""
        SELECT n.nspname, c.relname, format_type(s.seqtypid, NULL), s.seqstart, s.seqincrement, s.seqmin, s.seqmax,
               s.seqcache, s.seqcycle,
               (SELECT quote_ident(rn.nspname) || '.' || quote_ident(rc.relname) || '.' || quote_ident(ra.attname)
                FROM pg_depend d
                JOIN pg_class rc ON rc.oid = d.refobjid
                JOIN pg_namespace rn ON rn.oid = rc.relnamespace
                JOIN pg_attribute ra ON ra.attrelid = d.refobjid AND ra.attnum = d.refobjsubid
                WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.refclassid = 'pg_class'::regclass
                  AND d.deptype = 'a'
                LIMIT 1)
        FROM pg_sequence s
        JOIN pg_class c ON c.oid = s.seqrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE {UserSchema("n")} AND {NotExtensionMember("pg_class", "c.oid")}
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'i');
        """;

    // d domain, e enum, c composite - only a standalone one (CREATE TYPE ... AS), not every table's row type
    private static readonly string Types = $"""
        SELECT t.oid, n.nspname, t.typname, t.typtype::text, format_type(t.typbasetype, t.typtypmod), t.typnotnull,
               t.typdefault,
               CASE WHEN t.typtype = 'd' AND t.typcollation <> 0 AND t.typcollation <> bt.typcollation THEN
                    CASE WHEN cn.nspname = 'pg_catalog' THEN quote_ident(co.collname)
                         ELSE quote_ident(cn.nspname) || '.' || quote_ident(co.collname) END END,
               ARRAY(SELECT quote_literal(e.enumlabel) FROM pg_enum e WHERE e.enumtypid = t.oid ORDER BY e.enumsortorder),
               t.typrelid
        FROM pg_type t
        JOIN pg_namespace n ON n.oid = t.typnamespace
        LEFT JOIN pg_type bt ON bt.oid = t.typbasetype
        LEFT JOIN pg_collation co ON co.oid = t.typcollation
        LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
        WHERE (t.typtype IN ('d', 'e') OR (t.typtype = 'c' AND (SELECT relkind FROM pg_class WHERE oid = t.typrelid) = 'c'))
          AND {UserSchema("n")} AND {NotExtensionMember("pg_type", "t.oid")};
        """;

    private const string Extensions = """
        SELECT e.extname, e.extversion, n.nspname
        FROM pg_extension e
        JOIN pg_namespace n ON n.oid = e.extnamespace;
        """;
}
