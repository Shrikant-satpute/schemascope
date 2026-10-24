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
                WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f') AND {UserSchema("n")} AND {NotExtensionMember("pg_class", "c.oid")})
             + (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE {UserSchema("n")}
                AND {NotExtensionMember("pg_proc", "p.oid")}
                AND NOT EXISTS (SELECT 1 FROM pg_depend id WHERE id.classid = 'pg_proc'::regclass AND id.objid = p.oid AND id.deptype = 'i'));
        """;

    public const string ServerInfo = """
        SELECT current_setting('server_version'), current_setting('server_version_num')::int, current_database(),
               (SELECT datcollate::text FROM pg_database WHERE datname = current_database());
        """;

    /// <summary>
    /// Every catalog set in one round trip, in this order: schemas, relations, columns, constraints, indexes,
    /// routines, triggers, sequences, types, extensions, row security policies, collations, rules, dependencies.
    /// </summary>
    public static string Catalog(int versionNum) => string.Join("\n", Schemas, Relations, Columns(versionNum), Constraints,
        Indexes, Routines, Triggers, Sequences, Types(versionNum), Extensions, Policies, Collations(versionNum), Rules,
        Operators, Dependencies(versionNum));

    // The public schema's owner is left out while it is the one each version gives it (the bootstrap superuser up to
    // 14, pg_database_owner from 15) or the database's owner: one schema created on two versions is the same schema.
    private static readonly string Schemas = $"""
        SELECT n.nspname,
               CASE WHEN n.nspname = 'public' AND (n.nspowner = 10 OR pg_get_userbyid(n.nspowner) = 'pg_database_owner'
                         OR n.nspowner = (SELECT d.datdba FROM pg_database d WHERE d.datname = current_database()))
                    THEN '' ELSE pg_get_userbyid(n.nspowner) END
        FROM pg_namespace n
        WHERE {UserSchema("n")} AND {NotExtensionMember("pg_namespace", "n.oid")};
        """;

    /// <summary>
    /// r table, p partitioned table, v view, m materialized view, f foreign table. Beside the definition, what
    /// pg_get_viewdef and the columns leave out: UNLOGGED (relpersistence), row level security and FORCE, the WITH (...)
    /// options (a view's check_option and security_barrier live there), the partition key, a partition's bounds, and the
    /// parents of a partition or of plain inheritance, the type a typed table (CREATE TABLE ... OF type) is of, the
    /// replica identity (relreplident, and the index USING INDEX names), and a foreign table's server and options.
    /// </summary>
    private static readonly string Relations = $"""
        SELECT c.oid, n.nspname, c.relname, c.relkind::text,
               CASE WHEN c.relkind IN ('v', 'm') THEN pg_get_viewdef(c.oid, true) END,
               c.relpersistence::text, c.relrowsecurity, c.relforcerowsecurity, COALESCE(c.reloptions, ARRAY[]::text[]),
               CASE WHEN c.relkind = 'p' THEN pg_get_partkeydef(c.oid) END,
               c.relispartition, CASE WHEN c.relispartition THEN pg_get_expr(c.relpartbound, c.oid) END,
               ARRAY(SELECT quote_ident(pn.nspname) || '.' || quote_ident(pc.relname)
                     FROM pg_inherits i
                     JOIN pg_class pc ON pc.oid = i.inhparent
                     JOIN pg_namespace pn ON pn.oid = pc.relnamespace
                     WHERE i.inhrelid = c.oid ORDER BY i.inhseqno),
               CASE WHEN c.reloftype <> 0 THEN format_type(c.reloftype, NULL) END,
               c.relreplident::text,
               (SELECT quote_ident(ri.relname) FROM pg_index rx JOIN pg_class ri ON ri.oid = rx.indexrelid
                WHERE rx.indrelid = c.oid AND rx.indisreplident LIMIT 1),
               (SELECT quote_ident(fs.srvname) FROM pg_foreign_table ft JOIN pg_foreign_server fs ON fs.oid = ft.ftserver WHERE ft.ftrelid = c.oid),
               COALESCE((SELECT ft.ftoptions FROM pg_foreign_table ft WHERE ft.ftrelid = c.oid), ARRAY[]::text[])
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f') AND {UserSchema("n")} AND {NotExtensionMember("pg_class", "c.oid")};
        """;

    /// <summary>
    /// Columns of tables, of standalone composite types (relkind 'c'), of views (what a view returns) and of foreign
    /// tables (with their per-column options). attgenerated:
    /// 's' stored, and from PostgreSQL 18 'v' virtual. An identity column's sequence hangs off it with deptype 'i': its
    /// start, step and limits are the column's. From PostgreSQL 18 a NOT NULL can be NOT VALID (attnotnull is set, its
    /// pg_constraint row is not validated): existing rows may still hold NULLs, so the column is not read as NOT NULL.
    /// </summary>
    private static string Columns(int versionNum) => $"""
        SELECT a.attrelid, a.attnum, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull AND NOT {NotNullNotValid(versionNum)},
               a.attidentity::text, {(versionNum >= 120000 ? "a.attgenerated::text" : "''::text")},
               pg_get_expr(ad.adbin, ad.adrelid),
               CASE WHEN a.attcollation <> 0 AND a.attcollation <> t.typcollation THEN
                    CASE WHEN cn.nspname = 'pg_catalog' THEN quote_ident(co.collname)
                         ELSE quote_ident(cn.nspname) || '.' || quote_ident(co.collname) END END,
               s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcache, s.seqcycle, {NotNullNotValid(versionNum)},
               COALESCE(a.attfdwoptions, ARRAY[]::text[])
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
        WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p', 'c', 'v', 'm', 'f') AND {UserSchema("n")}
        ORDER BY a.attrelid, a.attnum;
        """;

    /// <summary>PostgreSQL 18: the column's NOT NULL is a pg_constraint row that is NOT VALID. Never on older versions.</summary>
    private static string NotNullNotValid(int versionNum) => versionNum >= 180000
        ? "(a.attnotnull AND EXISTS (SELECT 1 FROM pg_constraint nn WHERE nn.conrelid = a.attrelid AND nn.contype = 'n' " +
          "AND nn.conkey[1] = a.attnum AND NOT nn.convalidated))"
        : "false";

    // p primary key, u unique, f foreign key, c check, x exclusion; PostgreSQL 18's NOT NULL rows ('n') are columns' attnotnull.
    // A partition's copies of its parent's keys (conparentid set, on another table) are its own constraints too; the
    // copies a foreign key makes for each partition it references (conparentid set, on the same table) are not.
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
        WHERE co.contype IN ('p', 'u', 'f', 'c', 'x') AND {UserSchema("n")}
          AND (co.conparentid = 0 OR EXISTS (SELECT 1 FROM pg_constraint pco WHERE pco.oid = co.conparentid AND pco.conrelid <> co.conrelid))
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
    /// routine on. A function a type made for itself (a range type's constructors and its multirange's, pg_depend
    /// deptype 'i') belongs to that type, as CREATE TYPE ... AS RANGE makes it, and is never listed.
    /// </summary>
    private static readonly string Routines = $"""
        SELECT p.oid, n.nspname, p.proname, p.prokind::text, p.proretset, oidvectortypes(p.proargtypes),
               CASE WHEN p.prokind <> 'a' THEN pg_get_functiondef(p.oid)
                    ELSE 'CREATE AGGREGATE ' || quote_ident(n.nspname) || '.' || quote_ident(p.proname)
                         || '(' || pg_get_function_identity_arguments(p.oid) || ') ('
                         || E'\n    SFUNC = ' || ag.aggtransfn::regproc::text
                         || E',\n    STYPE = ' || format_type(ag.aggtranstype, NULL)
                         || CASE WHEN ag.aggtransspace <> 0 THEN E',\n    SSPACE = ' || ag.aggtransspace ELSE '' END
                         || CASE WHEN ag.aggfinalfn::oid <> 0 THEN E',\n    FINALFUNC = ' || ag.aggfinalfn::regproc::text ELSE '' END
                         || CASE WHEN ag.aggfinalextra THEN E',\n    FINALFUNC_EXTRA' ELSE '' END
                         -- READ_ONLY is the default, READ_WRITE for an ordered-set or hypothetical-set aggregate
                         || CASE WHEN ag.aggfinalfn::oid <> 0 AND ag.aggfinalmodify <> CASE WHEN ag.aggkind = 'n' THEN 'r' ELSE 'w' END
                                 THEN E',\n    FINALFUNC_MODIFY = ' || CASE ag.aggfinalmodify WHEN 'r' THEN 'READ_ONLY' WHEN 's' THEN 'SHAREABLE' ELSE 'READ_WRITE' END
                                 ELSE '' END
                         || CASE WHEN ag.aggcombinefn::oid <> 0 THEN E',\n    COMBINEFUNC = ' || ag.aggcombinefn::regproc::text ELSE '' END
                         || CASE WHEN ag.aggserialfn::oid <> 0 THEN E',\n    SERIALFUNC = ' || ag.aggserialfn::regproc::text ELSE '' END
                         || CASE WHEN ag.aggdeserialfn::oid <> 0 THEN E',\n    DESERIALFUNC = ' || ag.aggdeserialfn::regproc::text ELSE '' END
                         || CASE WHEN ag.agginitval IS NOT NULL THEN E',\n    INITCOND = ' || quote_literal(ag.agginitval) ELSE '' END
                         -- the moving-aggregate implementation
                         || CASE WHEN ag.aggmtransfn::oid <> 0 THEN
                                 E',\n    MSFUNC = ' || ag.aggmtransfn::regproc::text
                                 || E',\n    MINVFUNC = ' || ag.aggminvtransfn::regproc::text
                                 || E',\n    MSTYPE = ' || format_type(ag.aggmtranstype, NULL)
                                 || CASE WHEN ag.aggmtransspace <> 0 THEN E',\n    MSSPACE = ' || ag.aggmtransspace ELSE '' END
                                 || CASE WHEN ag.aggmfinalfn::oid <> 0 THEN E',\n    MFINALFUNC = ' || ag.aggmfinalfn::regproc::text ELSE '' END
                                 || CASE WHEN ag.aggmfinalextra THEN E',\n    MFINALFUNC_EXTRA' ELSE '' END
                                 || CASE WHEN ag.aggmfinalfn::oid <> 0 AND ag.aggmfinalmodify <> 'r'
                                         THEN E',\n    MFINALFUNC_MODIFY = ' || CASE ag.aggmfinalmodify WHEN 's' THEN 'SHAREABLE' ELSE 'READ_WRITE' END
                                         ELSE '' END
                                 || CASE WHEN ag.aggminitval IS NOT NULL THEN E',\n    MINITCOND = ' || quote_literal(ag.aggminitval) ELSE '' END
                            ELSE '' END
                         -- CREATE AGGREGATE takes the operator's name, OPERATOR(schema.op), not its signature
                         || CASE WHEN ag.aggsortop::oid <> 0 THEN E',\n    SORTOP = OPERATOR(' || quote_ident(son.nspname) || '.' || so.oprname || ')' ELSE '' END
                         || CASE WHEN p.proparallel <> 'u' THEN E',\n    PARALLEL = ' || CASE p.proparallel WHEN 's' THEN 'SAFE' ELSE 'RESTRICTED' END ELSE '' END
                         || CASE WHEN ag.aggkind = 'h' THEN E',\n    HYPOTHETICAL' ELSE '' END
                         || E'\n);' END,
               pg_get_function_arguments(p.oid), pg_get_function_result(p.oid)
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        LEFT JOIN pg_aggregate ag ON ag.aggfnoid = p.oid
        LEFT JOIN pg_operator so ON so.oid = ag.aggsortop
        LEFT JOIN pg_namespace son ON son.oid = so.oprnamespace
        WHERE {UserSchema("n")} AND {NotExtensionMember("pg_proc", "p.oid")}
          AND NOT EXISTS (SELECT 1 FROM pg_depend id WHERE id.classid = 'pg_proc'::regclass AND id.objid = p.oid AND id.deptype = 'i');
        """;

    // user triggers only: not the internal ones behind foreign keys, not the clones a partition gets from its parent
    private static readonly string Triggers = $"""
        SELECT n.nspname, c.relname, t.tgname, pg_get_triggerdef(t.oid, true), t.tgenabled::text, t.oid
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
                LIMIT 1),
               c.oid
        FROM pg_sequence s
        JOIN pg_class c ON c.oid = s.seqrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE {UserSchema("n")} AND {NotExtensionMember("pg_class", "c.oid")}
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'i');
        """;

    // d domain, e enum, r range, c composite - only a standalone one (CREATE TYPE ... AS), not every table's row type.
    // A range's multirange (PostgreSQL 14, typtype 'm') is made with it and is not listed.
    private static string Types(int versionNum) => $"""
        SELECT t.oid, n.nspname, t.typname, t.typtype::text, format_type(t.typbasetype, t.typtypmod), t.typnotnull,
               t.typdefault,
               CASE WHEN t.typtype = 'd' AND t.typcollation <> 0 AND t.typcollation <> bt.typcollation THEN
                    CASE WHEN cn.nspname = 'pg_catalog' THEN quote_ident(co.collname)
                         ELSE quote_ident(cn.nspname) || '.' || quote_ident(co.collname) END END,
               ARRAY(SELECT quote_literal(e.enumlabel) FROM pg_enum e WHERE e.enumtypid = t.oid ORDER BY e.enumsortorder),
               t.typrelid,
               format_type(rg.rngsubtype, NULL),
               CASE WHEN rg.rngsubopc <> 0 AND NOT op.opcdefault THEN quote_ident(opn.nspname) || '.' || quote_ident(op.opcname) END,
               CASE WHEN rg.rngcollation <> 0 AND rg.rngcollation <> st.typcollation THEN
                    CASE WHEN rcn.nspname = 'pg_catalog' THEN quote_ident(rco.collname)
                         ELSE quote_ident(rcn.nspname) || '.' || quote_ident(rco.collname) END END,
               CASE WHEN rg.rngcanonical <> 0 THEN rg.rngcanonical::regproc::text END,
               CASE WHEN rg.rngsubdiff <> 0 THEN rg.rngsubdiff::regproc::text END,
               {(versionNum >= 140000 ? "(SELECT mt.typname::text FROM pg_type mt WHERE mt.oid = rg.rngmultitypid)" : "NULL::text")}
        FROM pg_type t
        JOIN pg_namespace n ON n.oid = t.typnamespace
        LEFT JOIN pg_type bt ON bt.oid = t.typbasetype
        LEFT JOIN pg_collation co ON co.oid = t.typcollation
        LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
        LEFT JOIN pg_range rg ON rg.rngtypid = t.oid
        LEFT JOIN pg_type st ON st.oid = rg.rngsubtype
        LEFT JOIN pg_opclass op ON op.oid = rg.rngsubopc
        LEFT JOIN pg_namespace opn ON opn.oid = op.opcnamespace
        LEFT JOIN pg_collation rco ON rco.oid = rg.rngcollation
        LEFT JOIN pg_namespace rcn ON rcn.oid = rco.collnamespace
        WHERE (t.typtype IN ('d', 'e', 'r') OR (t.typtype = 'c' AND (SELECT relkind FROM pg_class WHERE oid = t.typrelid) = 'c'))
          AND {UserSchema("n")} AND {NotExtensionMember("pg_type", "t.oid")};
        """;

    private const string Extensions = """
        SELECT e.extname, e.extversion, n.nspname
        FROM pg_extension e
        JOIN pg_namespace n ON n.oid = e.extnamespace;
        """;

    // polcmd: * all, r select, a insert, w update, d delete; role 0 is PUBLIC
    private static readonly string Policies = $"""
        SELECT pol.polrelid, pol.polname, pol.polpermissive, pol.polcmd::text,
               ARRAY(SELECT CASE WHEN r.oid = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(r.oid)) END
                     FROM unnest(pol.polroles) r(oid) ORDER BY 1),
               pg_get_expr(pol.polqual, pol.polrelid), pg_get_expr(pol.polwithcheck, pol.polrelid)
        FROM pg_policy pol
        JOIN pg_class c ON c.oid = pol.polrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE {UserSchema("n")};
        """;

    /// <summary>
    /// User collations (CREATE COLLATION): provider c libc, i ICU, b builtin (PostgreSQL 17). Up to 14 an ICU
    /// collation's locale is its collcollate; 15 and 16 keep it in colliculocale, 17 in colllocale. ICU rules are 16's.
    /// </summary>
    private static string Collations(int versionNum) => $"""
        SELECT co.oid, n.nspname, co.collname, co.collprovider::text, co.collisdeterministic,
               co.collcollate::text, co.collctype::text,
               {(versionNum >= 170000 ? "co.colllocale" : versionNum >= 150000 ? "co.colliculocale" : "NULL::text")},
               {(versionNum >= 160000 ? "co.collicurules" : "NULL::text")}
        FROM pg_collation co
        JOIN pg_namespace n ON n.oid = co.collnamespace
        WHERE {UserSchema("n")} AND {NotExtensionMember("pg_collation", "co.oid")};
        """;

    /// <summary>
    /// Rules other than a view's own _RETURN (CREATE RULE): a table's, and the extra ones a view may have. ev_enabled
    /// 'D' is a disabled rule.
    /// </summary>
    private static readonly string Rules = $"""
        SELECT rw.ev_class, rw.rulename, pg_get_ruledef(rw.oid, true), rw.ev_enabled::text
        FROM pg_rewrite rw
        JOIN pg_class c ON c.oid = rw.ev_class
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE rw.rulename <> '_RETURN' AND c.relkind IN ('r', 'p', 'v') AND {UserSchema("n")} AND {NotExtensionMember("pg_class", "c.oid")};
        """;

    /// <summary>
    /// User operators (CREATE OPERATOR): l prefix, b infix. An operator is told apart by its argument types, as
    /// PostgreSQL keys it; its function, commutator, negator and estimators by name (CREATE OPERATOR resolves a
    /// function by the operator's argument types, and names another operator as OPERATOR(schema.op)).
    /// </summary>
    private static readonly string Operators = $"""
        SELECT o.oid, n.nspname, o.oprname::text,
               CASE WHEN o.oprleft <> 0 THEN format_type(o.oprleft, NULL) END,
               CASE WHEN o.oprright <> 0 THEN format_type(o.oprright, NULL) END,
               o.oprcode::regproc::text,
               CASE WHEN o.oprcom <> 0 THEN (SELECT 'OPERATOR(' || quote_ident(cn.nspname) || '.' || c.oprname || ')'
                                             FROM pg_operator c JOIN pg_namespace cn ON cn.oid = c.oprnamespace WHERE c.oid = o.oprcom) END,
               CASE WHEN o.oprnegate <> 0 THEN (SELECT 'OPERATOR(' || quote_ident(gn.nspname) || '.' || g.oprname || ')'
                                                FROM pg_operator g JOIN pg_namespace gn ON gn.oid = g.oprnamespace WHERE g.oid = o.oprnegate) END,
               CASE WHEN o.oprrest::oid <> 0 THEN o.oprrest::regproc::text END,
               CASE WHEN o.oprjoin::oid <> 0 THEN o.oprjoin::regproc::text END,
               o.oprcanhash, o.oprcanmerge
        FROM pg_operator o
        JOIN pg_namespace n ON n.oid = o.oprnamespace
        WHERE {UserSchema("n")} AND {NotExtensionMember("pg_operator", "o.oid")};
        """;

    /// <summary>
    /// What each object needs (pg_depend, normal and automatic dependencies only), with both ends resolved to what the
    /// reader lists: a view's rule, a default or generated column, a constraint and a policy stand for their table (a
    /// domain's constraint for its domain), a row type for its table, an array type for its element type, a multirange
    /// for its range. A table's own rule is marked "rule" (07 creates it, not 03). An index stands for its table, named,
    /// and marked "key" when it backs a primary, unique or exclusion key (made with the table) and "index" otherwise (06);
    /// of an index only what it needs beyond its table's columns is kept: an operator class, an operator, a collation,
    /// a function or a type. The needed object's extension is named when an extension made it. Built-in objects (oid
    /// below 16384) are never needed by name, so they are left out. A foreign key is marked as one: scripts add those
    /// once every table exists. A user operator needs its function and types like any object. A default's need of its
    /// own column (it goes when the column goes) is no reading of the column, and is left out.
    /// </summary>
    private static string Dependencies(int versionNum) => $"""
        SELECT x.dep_kind, x.dep_oid, x.ref_kind, x.ref_oid, x.ref_col, x.via, e.extname, x.ix_name
        FROM (
            SELECT CASE WHEN dp.classid = 'pg_type'::regclass OR (dp.classid = 'pg_constraint'::regclass AND co.conrelid = 0) THEN 't'
                        WHEN dp.classid = 'pg_proc'::regclass THEN 'p'
                        WHEN dp.classid = 'pg_trigger'::regclass THEN 'g'
                        WHEN dp.classid = 'pg_operator'::regclass THEN 'r'
                        ELSE 'c' END AS dep_kind,
                   CASE WHEN dp.classid = 'pg_rewrite'::regclass THEN rw.ev_class
                        WHEN dp.classid = 'pg_attrdef'::regclass THEN ad.adrelid
                        WHEN dp.classid = 'pg_constraint'::regclass THEN CASE WHEN co.conrelid <> 0 THEN co.conrelid ELSE co.contypid END
                        WHEN dp.classid = 'pg_policy'::regclass THEN pol.polrelid
                        WHEN ix.indexrelid IS NOT NULL THEN ix.indrelid
                        ELSE dp.objid END AS dep_oid,
                   CASE WHEN dp.refclassid = 'pg_proc'::regclass THEN 'p'
                        WHEN dp.refclassid = 'pg_opclass'::regclass THEN 'o'
                        WHEN dp.refclassid = 'pg_operator'::regclass THEN 'r'
                        WHEN dp.refclassid = 'pg_collation'::regclass THEN 'l'
                        WHEN dp.refclassid = 'pg_class'::regclass OR rt.typrelid <> 0 THEN 'c'
                        ELSE 't' END AS ref_kind,
                   CASE WHEN dp.refclassid = 'pg_type'::regclass THEN CASE WHEN rt.typrelid <> 0 THEN rt.typrelid ELSE rt.oid END
                        ELSE dp.refobjid END AS ref_oid,
                   CASE WHEN dp.refclassid = 'pg_class'::regclass AND dp.refobjsubid > 0 THEN
                        (SELECT a.attname::text FROM pg_attribute a WHERE a.attrelid = dp.refobjid AND a.attnum = dp.refobjsubid) END AS ref_col,
                   CASE WHEN dp.classid = 'pg_rewrite'::regclass THEN CASE WHEN rwc.relkind IN ('r', 'p') THEN 'rule' ELSE 'view' END
                        WHEN dp.classid = 'pg_attrdef'::regclass THEN 'default'
                        WHEN dp.classid = 'pg_constraint'::regclass THEN CASE WHEN co.contype = 'f' THEN 'foreignkey' ELSE 'constraint' END
                        WHEN dp.classid = 'pg_trigger'::regclass THEN 'trigger'
                        WHEN dp.classid = 'pg_policy'::regclass THEN 'policy'
                        WHEN dp.classid = 'pg_proc'::regclass THEN 'routine'
                        WHEN dp.classid = 'pg_type'::regclass THEN 'type'
                        WHEN dp.classid = 'pg_operator'::regclass THEN 'operator'
                        WHEN ix.indexrelid IS NOT NULL THEN
                             CASE WHEN EXISTS (SELECT 1 FROM pg_constraint kc WHERE kc.conindid = ix.indexrelid AND kc.contype IN ('p', 'u', 'x'))
                                  THEN 'key' ELSE 'index' END
                        ELSE 'relation' END AS via,
                   CASE WHEN ix.indexrelid IS NOT NULL THEN ixc.relname::text END AS ix_name
            FROM pg_depend dp
            LEFT JOIN pg_rewrite rw ON dp.classid = 'pg_rewrite'::regclass AND rw.oid = dp.objid
            LEFT JOIN pg_class rwc ON rwc.oid = rw.ev_class
            LEFT JOIN pg_attrdef ad ON dp.classid = 'pg_attrdef'::regclass AND ad.oid = dp.objid
            LEFT JOIN pg_constraint co ON dp.classid = 'pg_constraint'::regclass AND co.oid = dp.objid
            LEFT JOIN pg_policy pol ON dp.classid = 'pg_policy'::regclass AND pol.oid = dp.objid
            LEFT JOIN pg_index ix ON dp.classid = 'pg_class'::regclass AND ix.indexrelid = dp.objid
            LEFT JOIN pg_class ixc ON ixc.oid = ix.indexrelid
            LEFT JOIN pg_type rt0 ON dp.refclassid = 'pg_type'::regclass AND rt0.oid = dp.refobjid
            {(versionNum >= 140000 ? "LEFT JOIN pg_range mr ON mr.rngmultitypid = rt0.oid" : "LEFT JOIN (SELECT NULL::oid AS rngtypid) mr ON false")}
            LEFT JOIN pg_type rt ON rt.oid = CASE WHEN mr.rngtypid IS NOT NULL THEN mr.rngtypid
                                                  WHEN rt0.typcategory = 'A' AND rt0.typelem <> 0 THEN rt0.typelem ELSE rt0.oid END
            WHERE dp.deptype IN ('n', 'a') AND dp.objid >= 16384 AND dp.refobjid >= 16384
              AND dp.classid IN ('pg_class'::regclass, 'pg_type'::regclass, 'pg_proc'::regclass, 'pg_rewrite'::regclass,
                                 'pg_attrdef'::regclass, 'pg_constraint'::regclass, 'pg_trigger'::regclass, 'pg_policy'::regclass,
                                 'pg_operator'::regclass)
              AND dp.refclassid IN ('pg_class'::regclass, 'pg_type'::regclass, 'pg_proc'::regclass, 'pg_opclass'::regclass,
                                    'pg_operator'::regclass, 'pg_collation'::regclass)
              AND NOT (ix.indexrelid IS NOT NULL AND dp.refclassid = 'pg_class'::regclass)
              -- a default needs its own column only to go when it goes; that is no reader of the column
              AND NOT (ad.oid IS NOT NULL AND dp.refclassid = 'pg_class'::regclass AND dp.refobjid = ad.adrelid AND dp.refobjsubid = ad.adnum)
        ) x
        LEFT JOIN pg_depend xd ON xd.classid = CASE x.ref_kind WHEN 'c' THEN 'pg_class'::regclass WHEN 't' THEN 'pg_type'::regclass
                                                               WHEN 'o' THEN 'pg_opclass'::regclass WHEN 'r' THEN 'pg_operator'::regclass
                                                               WHEN 'l' THEN 'pg_collation'::regclass ELSE 'pg_proc'::regclass END
             AND xd.objid = x.ref_oid AND xd.refclassid = 'pg_extension'::regclass AND xd.deptype = 'e'
        LEFT JOIN pg_extension e ON e.oid = xd.refobjid
        WHERE x.dep_oid IS NOT NULL AND x.ref_oid IS NOT NULL;
        """;
}
