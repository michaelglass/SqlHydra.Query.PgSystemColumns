# SqlHydra.Query.PgSystemColumns

<!-- sync:intro:start -->
PostgreSQL system columns — `xmin` — in the generated record and in the
[SqlHydra](https://github.com/JordanMarr/SqlHydra) query computation expression.
<!-- sync:intro:end -->

A system column is not in `information_schema`, so `SqlHydra.Cli` never discovers it and it
cannot appear in the generated record at all. And `SELECT u.*` does not return one, so a record
that does carry the field fails to hydrate on every whole-entity read.

This package covers both halves.

**Codegen.** Register it in your TOML and the generator emits the field itself, with the
attributes it needs:

```
[<ReadOnlyColumn>]
[<ProviderDbType("Xid")>]
xmin: uint
```

**Query.** One operation names the column explicitly, so a whole-entity read returns it:

```
SELECT "u".*      becomes      SELECT "u".*, "u"."xmin"
```

## The columns

All six of PostgreSQL's system columns work. The operation names whichever column you
give it, so nothing is specific to `xmin`, and you can chain it to project more than one.

| Column | Type | What it is |
| --- | --- | --- |
| `tableoid` | `oid` | Which table the row came from. Useful with partitioned tables and inheritance. |
| `xmin` | `xid` | The inserting transaction — the row version. |
| `cmin` | `cid` | Command id within the inserting transaction. |
| `xmax` | `xid` | The deleting transaction, or `0` for a live row. |
| `cmax` | `cid` | Command id within the deleting transaction. |
| `ctid` | `tid` | Physical location of this row version. |

**`ctid` is not a row identifier** — it is a physical address, and it changes when the row
is updated or moved by `VACUUM FULL`. Use `xmin` for concurrency and a primary key for
identity. See [system columns](https://www.postgresql.org/docs/current/ddl-system-columns.html).

## Why you would want `xmin`

It is PostgreSQL's row version, and it changes on every write to the row. That
makes optimistic concurrency a plain column comparison: read the version, then
include it in the `WHERE` of your update. If someone else wrote first the
version no longer matches, the `UPDATE` affects zero rows, and you can refuse
the edit instead of silently overwriting theirs. No locks, no re-read.

## Install

```bash
dotnet add package SqlHydra.Query.PgSystemColumns
```

Then name the columns you want, per table, in the project the generator runs over:

```fsharp
open SqlHydra.Query.PgSystemColumns

type MySystemColumns() =
    inherit Codegen.PgSystemColumns([ "public/users.xmin"; "sales/*.xmin" ])
```

and register **that project** in your `sqlhydra-npgsql.toml`:

```toml
[extensions]
type_mappings = [ "YourProject" ]
```

An entry is `{schema}/{table}.{column}`, the table part a glob — the grammar SqlHydra's
`[filters]` already uses. `dotnet sqlhydra npgsql` then emits `xmin` on the tables you named,
with both attributes:

```fsharp
/// The id of the transaction that inserted this row version — PostgreSQL's row
/// version. It changes on every write to the row, which is what makes it usable as
/// an optimistic-concurrency check: read it, then include it in the WHERE of the
/// UPDATE. If someone else wrote first the UPDATE matches no rows.
[<ReadOnlyColumn>]
[<ProviderDbType("Xid")>]
xmin: uint
```

Both attributes are load-bearing. `[<ProviderDbType("Xid")>]` is what makes the comparison
bind: Npgsql has no default mapping for `uint32`, so without it a compare-and-swap parameter
throws client-side (*"Writing values of 'System.UInt32' is not supported for parameters having
no NpgsqlDbType"*). `[<ReadOnlyColumn>]` keeps the column out of every `INSERT` column list and
`UPDATE SET` clause, which PostgreSQL refuses anyway.

Views get nothing: `SELECT xmin FROM a_view` is an error unless the view projects one, so a
view record carrying the field could not be read.

### Why there is no zero-configuration default

There is nothing to register but your own project: this package ships no ready-made extension,
and `Codegen.PgSystemColumns` is abstract on purpose.

A blanket default would have to contribute to every base table, and that is not a neutral
default — it **breaks** every table it touches. `SELECT t.*` does not return a system column, so
a record that declares the field fails to hydrate on every whole-entity read unless that read is
changed to project it. Against a schema of 97 tables that wants row versioning on 3, the
"convenient" default adds a required field to 97 record types and breaks 94 of them.

So the tables have to be named, and there is nowhere in the TOML to name them: SqlHydra's
`[extensions]` section is a bare list of assembly names with no per-extension settings. The
choice therefore has to be a type in your own project. That is a limitation of the extension
model, not a preference — the equivalent in-library setting is one line of TOML:

```toml
[sqlhydra_query_integration]
system_columns = [ "public/users.xmin" ]
```

A malformed entry, a bare `"xmin"`, or a column that is not one of the six fails when the
extension is constructed — before the generator opens a connection — rather than generating
nothing and failing later at the first read that hydrates the record.

### Requirements

Needs a [SqlHydra](https://github.com/JordanMarr/SqlHydra) with the `IContributeColumns`
codegen seam, plus `Column.IsReadOnly` and `Column.Doc`. The query half alone works against
`SqlHydra.Query` 4.1.1.

## Usage

<!-- sync:usage-opens:start src=examples/ExampleApp/Program.fs -->
```fsharp
open SqlHydra.Query
open SqlHydra.Query.PgSystemColumns.SystemColumns
```
<!-- sync:usage-opens:end -->

<!-- sync:usage-queries:start src=examples/ExampleApp/Program.fs -->
```fsharp
// Read a row together with its version. Without `withSystemColumns` the emitted SQL is
// `SELECT "u".*`, which omits `xmin`, and hydrating a record that declares the field
// fails.
let userWithVersion =
    select {
        for u in usersTable do
            where (u.id = userId)
            select u
            withSystemColumns (fun u -> u.xmin)
    }

// Compare-and-swap: the version you read goes into the predicate. If someone else wrote
// first, `xmin` no longer matches, the UPDATE affects zero rows, and you can refuse the
// edit instead of silently overwriting theirs. An ordinary column comparison —
// `[<ProviderDbType("Xid")>]` is what binds the parameter as `xid`.
let guardedUpdate (row: users) =
    update {
        for u in usersTable do
            set u.email "new@example.com"
            where (u.id = row.id && u.xmin = row.xmin)
    }

// Writing a row you read back needs no `excludeColumn`: `[<ReadOnlyColumn>]` keeps `xmin`
// out of the SET clause, so the version you are comparing against cannot also be assigned.
let insertNew () =
    let row =
        { id = Guid.NewGuid()
          email = "new@example.com"
          xmin = notAVersion }

    insert {
        into usersTable
        entity row
    }
```
<!-- sync:usage-queries:end -->

`withSystemColumns` must follow `select u`, because it expands the
`SELECT u.*` that `select` emits. Placing it earlier raises at query
construction rather than returning a row without the column. Placing it after a
scalar select does not compile at all — after `select u.email` the row type is
`string`, so naming `u.xmin` is a type error.

The column is named with a **lambda over the selected row**, not as a bare
`u.xmin`. That is what makes the operation usable in a **join**. `select` is the
one operation that changes the builder's row type while keeping the computation
expression's variable space, so after `select u` in a joined query the row is
`users` while the variable space is still the tuple `(u, s)`. An
`[<ProjectionParameter>]` is elaborated against the variable space, so the bare
form would be handed the tuple and every joined read would fail to compile
("expected `users` but is a tuple of type `'a * 'b`"). A plain lambda argument is
elaborated against its own parameter type — the selected row — in both shapes:

```fsharp
select {
    for u in usersTable do
        join s in userSettingsTable on (u.id = s.user_id)
        select u
        withSystemColumns (fun u -> u.xmin)   // SELECT "u".*, "u"."xmin", "s".* untouched
}
```

## The write side

Nothing to do. `[<ReadOnlyColumn>]`, which the generator emits, keeps `xmin` out of every
`INSERT` column list and `UPDATE SET` clause — so a row you read back can be written back
unchanged, and `excludeColumn` is not needed.

A record still has no optional fields, so a *fresh* record you are about to insert needs some
value in the field. `notAVersion` is that value, named so that misuse reads wrong:
`where (u.xmin = notAVersion)` compiles, because the field is a plain `uint32` and no codegen
seam can change that — only a value read back from the database is a version.

**The guard itself** needs nothing from this package either. The comparison in the example
above is an ordinary column comparison.

## License

MIT
