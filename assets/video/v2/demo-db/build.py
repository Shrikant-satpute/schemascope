"""Builds the SchemaScope demo databases on LocalDB only.

Drops and recreates SSDemo_Dev (source), SSDemo_QA and SSDemo_Client01..08 on (localdb)\\MSSQLLocalDB.
Every target starts as an exact copy of the source schema, then gets its own deterministic drift.

    python build.py            # all 10 databases
    python build.py SSDemo_Client05   # just one

SSDemo_Client05 is the hero: its drift is only what a DEFAULT apply fixes
(04_columntypes, 01_columns, 03_missing, 05_constraints, 06_indexes).
"""
import random
import re
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

SERVER = r"(localdb)\MSSQLLocalDB"   # never anything else
assert SERVER.lower().startswith("(localdb)"), "demo builder only ever touches LocalDB"
SQLCMD = r"C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE"
OUT = Path(__file__).parent / "generated"

SCHEMAS = {
    "core": "Tenant TenantSetting AppUser Role UserRole Currency Country Region FeatureFlag ApiKey PricingPlan Subscription Webhook AuditPolicy",
    "sales": "Customer CustomerContact Quote QuoteLine SalesOrder SalesOrderLine PriceList PriceListItem Discount Promotion SalesRep Territory Commission OrderNote",
    "billing": "Invoice InvoiceLine Payment PaymentMethod Refund CreditNote TaxRate TaxRegion BillingCycle Dunning LedgerEntry Statement PaymentGateway Chargeback",
    "inventory": "Product ProductCategory ProductVariant Warehouse Bin StockLevel StockMovement Supplier PurchaseOrder PurchaseOrderLine Uom Lot SerialNumber ReorderRule",
    "shipping": "Shipment ShipmentLine Carrier CarrierService Package TrackingEvent ShippingZone ShippingRate ReturnRequest ReturnLine Manifest Pickup Address DeliveryWindow",
    "crm": "Lead Opportunity Activity Campaign CampaignMember Segment SegmentMember Note FollowUp Pipeline Stage LeadSource EmailTemplate Survey",
    "hr": "Employee Department Position Shift TimeEntry LeaveRequest PayGrade Payroll PayrollLine Benefit Review Skill EmployeeSkill Holiday",
    "support": "Ticket TicketComment TicketTag Sla SupportQueue Agent Macro KnowledgeArticle ArticleFeedback Escalation Csat Channel BusinessHour Attachment",
    "reporting": "Snapshot KpiDefinition KpiValue Dashboard Widget ScheduledReport ReportRun ExportJob DataSource Metric Dimension SavedFilter ReportSubscription Alert",
    "audit": "ChangeLog LoginEvent AccessLog DataExport ConfigChange ErrorLog JobRun JobStep RetentionRule ArchiveBatch Consent PolicyAck Incident Evidence",
}

# optional columns: name, type, narrowed type (for drift) - all nullable, no default, no constraint
EXTRA_POOL = [
    ("Email", "core.Email", None), ("Phone", "core.Phone", None), ("DueDate", "date", None),
    ("StartDate", "date", None), ("EndDate", "date", None), ("Notes", "nvarchar(max)", None),
    ("ExternalRef", "varchar(64)", "varchar(40)"), ("Priority", "tinyint", None),
    ("Amount", "decimal(18,2)", None), ("Quantity", "int", None),
    ("CurrencyCode", "char(3)", None), ("Score", "int", None), ("Url", "nvarchar(400)", "nvarchar(255)"),
    ("Weight", "decimal(10,3)", None),
]

# ---------------------------------------------------------------- source model

class Table:
    def __init__(self, schema, name, i, names):
        self.schema, self.name, self.i = schema, name, i
        self.fq = f"{schema}.{name}"
        self.tenant_fk = not (schema == "core" and name == "Tenant")
        # every table at i%3==1 hangs off the one before it in its schema
        self.parent = names[i - 1] if i % 3 == 1 else None
        self.pcol = ("Owner" if self.parent == "Tenant" else "") + f"{self.parent}Id"
        self.has_children = i % 3 == 0 and i + 1 < len(names)
        h = sum(map(ord, schema + name))
        self.extras = [EXTRA_POOL[(h + k * 5) % len(EXTRA_POOL)] for k in range(3)]
        self.extras = list({e[0]: e for e in self.extras}.values())
        self.has_trigger = i in (2, 6, 10)
        self.has_count_fn = i % 2 == 0
        self.has_tvf = i < 4
        self.has_tabletype = i in (0, 5)
        self.created_index = i % 2 == 0

    @property
    def p(self):
        return f"{self.schema}_{self.name}"

    def columns(self):
        cols = ["    Id int IDENTITY(1,1) NOT NULL", "    TenantId int NOT NULL"]
        if self.parent:
            cols.append(f"    {self.pcol} int NULL")
        cols += ["    Code nvarchar(32) NOT NULL", "    Name nvarchar(200) NOT NULL", "    Description nvarchar(1000) NULL"]
        cols += [
            f"    Status tinyint NOT NULL CONSTRAINT DF_{self.p}_Status DEFAULT (0)",
            f"    IsActive bit NOT NULL CONSTRAINT DF_{self.p}_IsActive DEFAULT (1)",
            f"    CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_{self.p}_CreatedAt DEFAULT (sysutcdatetime())",
            f"    CreatedBy nvarchar(128) NOT NULL CONSTRAINT DF_{self.p}_CreatedBy DEFAULT (suser_sname())",
            "    ModifiedAt datetime2(3) NULL",
            "    RowVer rowversion NOT NULL",
        ]
        # optional columns last, so a column the target lacks comes back in the same position
        cols += [f"    {n} {t} NULL" for n, t, _ in self.extras]
        cols += [
            f"    CONSTRAINT PK_{self.p} PRIMARY KEY CLUSTERED (Id)",
            f"    CONSTRAINT UQ_{self.p}_Code UNIQUE (TenantId, Code)",
            f"    CONSTRAINT CK_{self.p}_Status CHECK (Status BETWEEN 0 AND 9)",
        ]
        return cols

    def create(self):
        return f"CREATE TABLE {self.fq}\n(\n" + ",\n".join(self.columns()) + "\n);"

    def fks(self):
        out = []
        if self.tenant_fk:
            out.append((f"FK_{self.p}_Tenant",
                        f"ALTER TABLE {self.fq} ADD CONSTRAINT FK_{self.p}_Tenant FOREIGN KEY (TenantId) REFERENCES core.Tenant (Id);"))
        if self.parent:
            out.append((f"FK_{self.p}_{self.pcol}",
                        f"ALTER TABLE {self.fq} ADD CONSTRAINT FK_{self.p}_{self.pcol} FOREIGN KEY ({self.pcol}) REFERENCES {self.schema}.{self.parent} (Id);"))
        return out

    def indexes(self):
        out = [(f"IX_{self.p}_TenantId_Status",
                f"CREATE NONCLUSTERED INDEX IX_{self.p}_TenantId_Status ON {self.fq} (TenantId, Status) INCLUDE (Name);")]
        if self.created_index:
            out.append((f"IX_{self.p}_CreatedAt", f"CREATE NONCLUSTERED INDEX IX_{self.p}_CreatedAt ON {self.fq} (CreatedAt DESC);"))
        if self.parent:
            out.append((f"IX_{self.p}_{self.pcol}", f"CREATE NONCLUSTERED INDEX IX_{self.p}_{self.pcol} ON {self.fq} ({self.pcol});"))
        return out

    def select_list(self, indent="           "):
        cols = ["t.Id", "t.TenantId", "t.Code", "t.Name", "t.Description"] + [f"t.{n}" for n, _, _ in self.extras] + ["t.Status", "t.CreatedAt"]
        return (",\n" + indent).join(cols)


def modules(t: Table):
    """(kind, fq name, text) for every module of a table, in dependency order."""
    s, n, fq = t.schema, t.name, t.fq
    out = []
    out.append(("PROCEDURE", f"{s}.usp_{n}_Get", f"""CREATE PROCEDURE {s}.usp_{n}_Get
    @TenantId int,
    @Id int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT {t.select_list()}
    FROM {fq} AS t
    WHERE t.TenantId = @TenantId
      AND t.Id = @Id;
END"""))
    out.append(("PROCEDURE", f"{s}.usp_{n}_List", f"""CREATE PROCEDURE {s}.usp_{n}_List
    @TenantId int,
    @Page int = 1,
    @PageSize int = 50
AS
BEGIN
    SET NOCOUNT ON;

    SELECT {t.select_list()}
    FROM {fq} AS t
    WHERE t.TenantId = @TenantId
      AND t.IsActive = 1
    ORDER BY t.CreatedAt DESC
    OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END"""))
    out.append(("PROCEDURE", f"{s}.usp_{n}_Insert", f"""CREATE PROCEDURE {s}.usp_{n}_Insert
    @TenantId int,
    @Code nvarchar(32),
    @Name nvarchar(200),
    @Description nvarchar(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO {fq} (TenantId, Code, Name, Description)
    VALUES (@TenantId, @Code, @Name, @Description);

    SELECT CAST(SCOPE_IDENTITY() AS int) AS Id;
END"""))
    out.append(("PROCEDURE", f"{s}.usp_{n}_Update", f"""CREATE PROCEDURE {s}.usp_{n}_Update
    @TenantId int,
    @Id int,
    @Name nvarchar(200),
    @Description nvarchar(1000) = NULL,
    @Status tinyint
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE t
       SET t.Name = @Name,
           t.Description = @Description,
           t.Status = @Status,
           t.ModifiedAt = SYSUTCDATETIME()
    FROM {fq} AS t
    WHERE t.TenantId = @TenantId
      AND t.Id = @Id;

    SELECT @@ROWCOUNT AS RowsAffected;
END"""))
    out.append(("PROCEDURE", f"{s}.usp_{n}_Delete", f"""CREATE PROCEDURE {s}.usp_{n}_Delete
    @TenantId int,
    @Id int
AS
BEGIN
    SET NOCOUNT ON;

    -- soft delete: rows are kept for the audit trail
    UPDATE t
       SET t.IsActive = 0,
           t.ModifiedAt = SYSUTCDATETIME()
    FROM {fq} AS t
    WHERE t.TenantId = @TenantId
      AND t.Id = @Id;
END"""))
    out.append(("VIEW", f"{s}.vw_{n}Active", f"""CREATE VIEW {s}.vw_{n}Active
AS
    SELECT {t.select_list()}
    FROM {fq} AS t
    WHERE t.IsActive = 1;"""))
    if t.has_count_fn:
        out.append(("FUNCTION", f"{s}.fn_{n}Count", f"""CREATE FUNCTION {s}.fn_{n}Count (@TenantId int)
RETURNS int
AS
BEGIN
    DECLARE @n int;

    SELECT @n = COUNT(*)
    FROM {fq} AS t
    WHERE t.TenantId = @TenantId
      AND t.IsActive = 1;

    RETURN @n;
END"""))
    if t.has_tvf:
        out.append(("FUNCTION", f"{s}.tvf_{n}Search", f"""CREATE FUNCTION {s}.tvf_{n}Search (@TenantId int, @Term nvarchar(100))
RETURNS TABLE
AS
RETURN
    SELECT t.Id, t.Code, t.Name, t.Status
    FROM {fq} AS t
    WHERE t.TenantId = @TenantId
      AND (t.Code LIKE @Term + N'%' OR t.Name LIKE N'%' + @Term + N'%');"""))
    if t.has_tabletype:
        out.append(("PROCEDURE", f"{s}.usp_{n}_BulkInsert", f"""CREATE PROCEDURE {s}.usp_{n}_BulkInsert
    @TenantId int,
    @Rows {s}.{n}Rows READONLY
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO {fq} (TenantId, Code, Name)
    SELECT @TenantId, r.Code, r.Name
    FROM @Rows AS r
    WHERE NOT EXISTS (SELECT 1 FROM {fq} AS t WHERE t.TenantId = @TenantId AND t.Code = r.Code);
END"""))
    return out


def trigger(t: Table):
    return (f"{t.schema}.tr_{t.name}_Touch", f"""CREATE TRIGGER {t.schema}.tr_{t.name}_Touch ON {t.fq}
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    UPDATE t
       SET t.ModifiedAt = SYSUTCDATETIME()
    FROM {t.fq} AS t
    JOIN inserted AS i ON i.Id = t.Id;
END""")


TABLES = [Table(s, n, i, names.split()) for s, names in SCHEMAS.items() for i, n in enumerate(names.split())]
BY_FQ = {t.fq: t for t in TABLES}
SYNONYMS = [(f"reporting.syn_{t.schema}{t.name}", t.fq) for t in TABLES if t.i == 0 and t.schema != "reporting"]


def source_sql():
    b = []
    b += [f"CREATE SCHEMA {s} AUTHORIZATION dbo;" for s in SCHEMAS]
    b += ["CREATE TYPE core.Email FROM nvarchar(256) NULL;", "CREATE TYPE core.Phone FROM varchar(32) NULL;",
          "CREATE TYPE core.MoneyAmount FROM decimal(19,4) NOT NULL;", "CREATE TYPE core.CountryCode FROM char(2) NOT NULL;",
          "CREATE TYPE core.Percentage FROM decimal(5,2) NULL;"]
    b += [f"CREATE SEQUENCE {s}.seq_DocumentNumber AS bigint START WITH 100000 INCREMENT BY 1;" for s in SCHEMAS]
    for t in TABLES:
        if t.has_tabletype:
            b.append(f"CREATE TYPE {t.schema}.{t.name}Rows AS TABLE\n(\n    Code nvarchar(32) NOT NULL PRIMARY KEY,\n    Name nvarchar(200) NOT NULL\n);")
    b += [t.create() for t in TABLES]
    b += [sql for t in TABLES for _, sql in t.fks()]
    b += [sql for t in TABLES for _, sql in t.indexes()]
    b += [f"CREATE SYNONYM {name} FOR {target};" for name, target in SYNONYMS]
    b += [text for t in TABLES for _, _, text in modules(t)]
    b += [trigger(t)[1] for t in TABLES if t.has_trigger]
    return b

# ---------------------------------------------------------------- drift helpers

ALL_MODULES = [(k, fq, text, t) for t in TABLES for k, fq, text in modules(t)]
MOD_BY_FQ = {fq: (k, text) for k, fq, text, _ in ALL_MODULES}


def reformat(text):
    """Same tokens, different layout: 2-space indents, keywords re-wrapped. Shows as Formatting only."""
    lines = []
    for line in text.split("\n"):
        stripped = line.lstrip(" ")
        indent = (len(line) - len(stripped)) // 2
        lines.append(" " * indent + stripped)
    out = "\n".join(lines)
    out = out.replace("SET NOCOUNT ON;\n\n", "SET NOCOUNT ON;\n")
    out = re.sub(r",\n\s+t\.", ", t.", out)          # select list on one line
    return out


def change(text):
    """A real behaviour change: one more filter on the first WHERE."""
    return re.sub(r"(\n(\s*)WHERE [^\n;]*)(;?)", r"\1\n\2  AND t.Status <> 9\3", text, count=1)


def recreate(fq, text):
    kind = MOD_BY_FQ[fq][0] if fq in MOD_BY_FQ else "TRIGGER"
    return [f"DROP {kind} {fq};", text]


def drift(db, spec):
    """spec: counts per kind of drift. Deterministic per database name."""
    rnd = random.Random(db)
    b = []
    used = set()

    def pick(pool, k, key=lambda x: x):
        pool = [x for x in pool if key(x) not in used]
        chosen = rnd.sample(pool, min(k, len(pool)))
        used.update(key(x) for x in chosen)
        return chosen

    tables = [t for t in TABLES if t.schema not in spec.get("skip_schemas", ())]

    # whole schema never deployed
    for s in spec.get("missing_schemas", ()):
        st = [t for t in TABLES if t.schema == s]
        b += [f"DROP {k} {fq};" for k, fq, _, t in ALL_MODULES if t.schema == s]
        b += [f"DROP TYPE {s}.{t.name}Rows;" for t in st if t.has_tabletype]
        b += [f"DROP SYNONYM {n};" for n, tgt in SYNONYMS if tgt.startswith(s + ".")]
        b += [f"DROP TABLE {t.fq};" for t in reversed(st)]
        b += [f"DROP SEQUENCE {s}.seq_DocumentNumber;", f"DROP SCHEMA {s};"]
        used.update(t.fq for t in st)
        used.update(fq for _, fq, _, t in ALL_MODULES if t.schema == s)

    # missing tables: leaf tables without trigger or table type
    for t in pick([t for t in tables if not t.has_children and not t.has_trigger and not t.has_tabletype and t.tenant_fk],
                  spec.get("missing_tables", 0), key=lambda t: t.fq):
        b.append(f"DROP TABLE {t.fq};")

    mods = [m for m in ALL_MODULES if m[3].schema not in spec.get("missing_schemas", ()) and "BulkInsert" not in m[1]]
    for k, fq, _, _ in pick(mods, spec.get("missing_modules", 0), key=lambda m: m[1]):
        b.append(f"DROP {k} {fq};")
    for k, fq, text, _ in pick([m for m in mods if "\n    WHERE" in m[2] or "\nAS\n    SELECT" in m[2]],
                               spec.get("different", 0), key=lambda m: m[1]):
        b += recreate(fq, change(text))
    for k, fq, text, _ in pick(mods, spec.get("formatting", 0), key=lambda m: m[1]):
        b += recreate(fq, reformat(text))

    live = [t for t in tables if t.fq not in used]
    # missing columns (01): drop an optional nullable column
    for t in pick([t for t in live if t.extras], spec.get("missing_columns", 0), key=lambda t: "col:" + t.fq):
        b.append(f"ALTER TABLE {t.fq} DROP COLUMN {t.extras[-1][0]};")
    # narrower columns in target (04 widens)
    for t in pick([t for t in live if any(e[2] for e in t.extras[:-1])], spec.get("narrow_columns", 0), key=lambda t: "type:" + t.fq):
        n, _, narrow = next(e for e in t.extras[:-1] if e[2])
        b.append(f"ALTER TABLE {t.fq} ALTER COLUMN {n} {narrow} NULL;")
    for t in pick(live, spec.get("notnull_columns", 0), key=lambda t: "nn:" + t.fq):
        b.append(f"ALTER TABLE {t.fq} ALTER COLUMN Description nvarchar(1000) NOT NULL;")
    # missing constraints (05)
    for t in pick(live, spec.get("missing_defaults", 0), key=lambda t: "df:" + t.fq):
        b.append(f"ALTER TABLE {t.fq} DROP CONSTRAINT DF_{t.p}_IsActive;")
    for t in pick(live, spec.get("missing_checks", 0), key=lambda t: "ck:" + t.fq):
        b.append(f"ALTER TABLE {t.fq} DROP CONSTRAINT CK_{t.p}_Status;")
    for t in pick([t for t in live if t.parent], spec.get("missing_fks", 0), key=lambda t: "fk:" + t.fq):
        b.append(f"ALTER TABLE {t.fq} DROP CONSTRAINT FK_{t.p}_{t.pcol};")
    for t in pick([t for t in live if t.parent], spec.get("disabled_fks", 0), key=lambda t: "fk:" + t.fq):
        b.append(f"ALTER TABLE {t.fq} NOCHECK CONSTRAINT FK_{t.p}_{t.pcol};")
    # missing indexes (06)
    for t in pick([t for t in live if t.created_index], spec.get("missing_indexes", 0), key=lambda t: "ix:" + t.fq):
        b.append(f"DROP INDEX IX_{t.p}_CreatedAt ON {t.fq};")
    # missing triggers (07, opt-in)
    for t in pick([t for t in live if t.has_trigger], spec.get("missing_triggers", 0), key=lambda t: "tr:" + t.fq):
        b.append(f"DROP TRIGGER {t.schema}.tr_{t.name}_Touch;")
    # extra column only the target has
    for t in pick(live, spec.get("extra_columns", 0), key=lambda t: "xc:" + t.fq):
        b.append(f"ALTER TABLE {t.fq} ADD LegacyRef varchar(50) NULL;")
    # case-only rename (09, opt-in)
    for t in pick(live, spec.get("case_renames", 0), key=lambda t: "cr:" + t.fq):
        b.append(f"EXEC sp_rename '{t.fq}.CreatedBy', 'createdBy', 'COLUMN';")
    # hotfix objects only the target has
    for k in range(spec.get("extra_objects", 0)):
        t = rnd.choice([t for t in live])
        tag = db.replace("SSDemo_", "")
        if k % 3 == 2:
            b.append(f"CREATE TABLE {t.schema}.{t.name}Import_{tag}_{k:02d}\n(\n    Id int NOT NULL CONSTRAINT PK_{t.p}Import_{tag}_{k:02d} PRIMARY KEY,\n    Payload nvarchar(max) NULL,\n    LoadedAt datetime2(3) NOT NULL CONSTRAINT DF_{t.p}Import_{tag}_{k:02d}_LoadedAt DEFAULT (sysutcdatetime())\n);")
        else:
            b.append(f"CREATE PROCEDURE {t.schema}.usp_{t.name}_Hotfix_{tag}_{k:02d}\n    @TenantId int\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    -- hotfix applied directly on {db}\n    UPDATE t SET t.Status = 0 FROM {t.fq} AS t WHERE t.TenantId = @TenantId AND t.Status > 9;\nEND")
    return b


# Each target's drift. Client05 is the hero: default-apply sections only.
DRIFT = {
    "SSDemo_QA": dict(formatting=2, different=1),
    "SSDemo_Client01": dict(missing_modules=5, formatting=3, different=2, extra_objects=1),
    "SSDemo_Client02": dict(missing_modules=18, different=10, formatting=12, extra_objects=4, missing_columns=3,
                            missing_indexes=2, disabled_fks=1),
    "SSDemo_Client03": dict(missing_tables=3, missing_modules=55, different=38, formatting=25, extra_objects=10,
                            missing_columns=6, narrow_columns=4, missing_indexes=6, missing_fks=2, extra_columns=3),
    "SSDemo_Client04": dict(missing_schemas=("support",), missing_modules=8, different=6, formatting=4, extra_objects=2),
    "SSDemo_Client05": dict(missing_tables=1, missing_modules=12, missing_columns=4, narrow_columns=3, notnull_columns=1,
                            missing_defaults=2, missing_checks=1, missing_fks=1, missing_indexes=5),
    "SSDemo_Client06": dict(extra_objects=24, different=8, formatting=3, extra_columns=2),
    "SSDemo_Client07": dict(formatting=80, different=2),
    "SSDemo_Client08": dict(missing_modules=10, different=14, formatting=9, extra_objects=5, missing_triggers=2,
                            disabled_fks=2, case_renames=2, missing_indexes=3),
}
DATABASES = ["SSDemo_Dev"] + list(DRIFT)


def script_for(db):
    head = [f"""IF DB_ID(N'{db}') IS NOT NULL
BEGIN
    ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{db}];
END""", f"CREATE DATABASE [{db}];", f"ALTER DATABASE [{db}] SET RECOVERY SIMPLE;", f"USE [{db}];"]
    body = source_sql() + (drift(db, DRIFT[db]) if db in DRIFT else [])
    return "\n\nGO\n\n".join(head + body) + "\n\nGO\n"


def build(db):
    OUT.mkdir(exist_ok=True)
    path = OUT / f"{db}.sql"
    path.write_text(script_for(db), encoding="utf-8")
    r = subprocess.run([SQLCMD, "-S", SERVER, "-E", "-b", "-I", "-x", "-i", str(path)], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f"{db} failed:\n{r.stdout[-3000:]}\n{r.stderr[-2000:]}")
    return db


if __name__ == "__main__":
    names = sys.argv[1:] or DATABASES
    for n in names:
        assert n in DATABASES, n
    with ThreadPoolExecutor(max_workers=5) as pool:
        for db in pool.map(build, names):
            print("built", db, flush=True)
