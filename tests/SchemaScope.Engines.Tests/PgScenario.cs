namespace SchemaScope.Engines.Tests;

/// <summary>
/// The LocalDB demo's six-status scenario, in PostgreSQL. The source and the target share most of a schema; the
/// target drifts in exactly these ways, so every status shows up once per kind:
///
///   Same            app.customers, the "Users" / users case twins, app.add(integer, integer), the view, the matview...
///   FormattingOnly  app.fmt: the body's layout and comment differ, nothing else
///   Different       app.orders (an extra column), app.changed ('A' became 'a': a literal's case is a real change)
///   MissingInTarget app.v_missing, and the overload app.add(text, text)
///   OnlyInTarget    app.extra
///   Unavailable     a second target this login may not connect to
///
/// pgcrypto is installed on both sides: its functions must not appear as objects, only the extension itself.
/// </summary>
public static class PgScenario
{
    public const string Common = """
        CREATE EXTENSION IF NOT EXISTS pgcrypto;
        CREATE SCHEMA app;
        CREATE TYPE app.mood AS ENUM ('sad', 'ok', 'happy');
        CREATE DOMAIN app.positive AS integer CHECK (VALUE > 0);
        CREATE TYPE app.pair AS (a integer, b text);
        CREATE TABLE app.customers (
            id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name text NOT NULL,
            mood app.mood DEFAULT 'ok',
            email text UNIQUE
        );
        CREATE TABLE app."Users" (id integer PRIMARY KEY);
        CREATE TABLE app.users (id integer PRIMARY KEY, "Name" text);
        CREATE SEQUENCE app.ticket_seq START 100 INCREMENT 5;
        CREATE FUNCTION app.add(a integer, b integer) RETURNS integer LANGUAGE sql AS $$ SELECT a + b $$;
        CREATE FUNCTION app.touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.total := coalesce(NEW.total, 0); RETURN NEW; END $$;
        CREATE PROCEDURE app.archive(days integer) LANGUAGE sql AS $$ SELECT days $$;
        CREATE AGGREGATE app.total_sum(numeric) (SFUNC = numeric_add, STYPE = numeric, INITCOND = '0');
        """;

    private const string Orders = """
        CREATE TABLE app.orders (
            id bigserial PRIMARY KEY,
            customer_id integer NOT NULL REFERENCES app.customers (id) ON DELETE CASCADE,
            total numeric(10,2) NOT NULL DEFAULT 0 CHECK (total >= 0),
            qty app.positive,
            doubled integer GENERATED ALWAYS AS (qty * 2) STORED{0}
        );
        CREATE INDEX orders_customer_ix ON app.orders (customer_id) INCLUDE (total) WHERE total > 0;
        CREATE TRIGGER orders_touch BEFORE INSERT ON app.orders FOR EACH ROW EXECUTE FUNCTION app.touch();
        CREATE VIEW app.v_orders AS SELECT o.id, c.name FROM app.orders o JOIN app.customers c ON c.id = o.customer_id;
        CREATE MATERIALIZED VIEW app.mv_totals AS SELECT customer_id, sum(total) AS total FROM app.orders GROUP BY customer_id;
        CREATE INDEX mv_totals_ix ON app.mv_totals (customer_id);
        """;

    public static readonly string Source = Common + string.Format(Orders, "") + """
        CREATE VIEW app.v_missing AS SELECT 1 AS one;
        CREATE FUNCTION app.add(a text, b text) RETURNS text LANGUAGE sql AS $$ SELECT a || b $$;
        CREATE FUNCTION app.changed() RETURNS text LANGUAGE sql AS $$ SELECT 'A' $$;
        CREATE FUNCTION app.fmt(x integer) RETURNS integer LANGUAGE plpgsql AS $$
        BEGIN
            RETURN x * 2; -- double it
        END;
        $$;
        """;

    public static readonly string Target = Common + string.Format(Orders, ",\n    note text") + """
        CREATE TABLE app.extra (id integer);
        CREATE FUNCTION app.changed() RETURNS text LANGUAGE sql AS $$ SELECT 'a' $$;
        CREATE FUNCTION app.fmt(x integer) RETURNS integer LANGUAGE plpgsql AS $$
        BEGIN
          /* the same code, laid out differently */
          RETURN   x*2;
        END;
        $$;
        """;
}
