# Changelog

Notable changes to SqlHydra.Query.PgSystemColumns. `fssemantictagger` reads this file: the `## Unreleased`
section must be non-empty before `mise run release` will cut a tag, and it is
promoted to the new version heading on release.

## Unreleased

- feat: **the generator emits the column — no text overlay, no hand-written attribute.**
  `Codegen.XminColumn` implements SqlHydra's `IContributeColumns`, so registering this package in
  the TOML `[extensions]` section is all it takes for `dotnet sqlhydra npgsql` to put `xmin` on
  every base table with `[<ProviderDbType("Xid")>]`, `[<ReadOnlyColumn>]` and a doc comment. A
  system column is not in `information_schema`, so `IExtendTypeMapping` could never reach it: a
  type mapping only fires for a column the provider already discovered. Until now the only way
  in was to rewrite the generated file after the generator had written it.

- feat: **`Codegen.PgSystemColumns`** — subclass it with a parameterless constructor, in the
  project the generator runs over, to ask for a set other than `xmin`. Abstract on purpose:
  SqlHydra instantiates every non-abstract extension it finds in a registered assembly, so a
  concrete base would contribute its own set alongside the subclass's. A name that is not one of
  the six fails the build rather than silently generating nothing — the failure mode that
  otherwise surfaces as a hydration error a long way from the typo.

- feat: **`Codegen.all`, `Codegen.column` and `Codegen.contributeTo`** — the six columns and the
  contribution decision as plain values and a pure function, so you can drive them directly.
  `contributeTo` returns nothing for a view (`SELECT xmin FROM a_view` is an error unless the
  view projects one) and nothing for a non-PostgreSQL provider.

- feat: **`Codegen.onlyTables`** — contribute to named tables rather than every base table.
  `SELECT t.*` does not return a system column, so a record that carries the field and is read
  whole fails to hydrate unless the read projects it; putting `xmin` on every table in a codebase
  that versions three of them breaks every whole-entity read of the rest. Found by consuming the
  package for real. Pass it as the second constructor argument to `Codegen.PgSystemColumns`;
  `Codegen.contributeToTables` is the same decision as a pure function.

- change: **the write side needs no `excludeColumn`.** `[<ReadOnlyColumn>]` keeps the column out
  of every `INSERT` column list and `UPDATE SET` clause, so a row read back can be written back
  unchanged. `notAVersion` remains, now only for the value a *fresh* record must carry in the
  field — a record has no optional fields.

- change: requires a SqlHydra with the `IContributeColumns` seam (`Column.IsReadOnly` and
  `Column.Doc` with it). The query half alone still works against `SqlHydra.Query` 4.1.1.

- feat: **`withSystemColumns` — project a PostgreSQL system column alongside the whole entity.**
  `SELECT u.*` does not return a system column, so a generated record carrying an `xmin` field
  fails to hydrate on every whole-entity read. `select u` followed by
  `withSystemColumns (fun u -> u.xmin)` emits `SELECT "u".*, "u"."xmin"` instead. Chain it to
  project more than one. The column is named with a LAMBDA over the selected row rather than as a
  bare `u.xmin`, which is what makes the operation usable after a JOIN: `select` changes the
  builder's row type while keeping the CE's variable space, so an `[<ProjectionParameter>]` would
  be handed the join tuple while its signature demanded the row, and every joined read failed to
  compile. A plain lambda argument is elaborated against the selected row in both shapes.

- feat: **all six system columns are supported** — `tableoid`, `xmin`, `cmin`, `xmax`, `cmax` and
  `ctid`. You name the column, so nothing in the operation is specific to any of them, and the
  compiler checks the field exists on your row. Note that `ctid` is a physical address, not a row
  identifier: it changes when the row is updated or moved by `VACUUM FULL`.

- feat: **`notAVersion`** — the value to give a system-column field in a record you are about
  to write, paired with the built-in `excludeColumn`. Named so that misuse reads wrong:
  `where (u.xmin = notAVersion)` compiles, because the generated field is a plain `uint32`
  and nothing outside the generator can change that. Only a value read back from the
  database is a version.

- feat: **`expandProjection`** — the projection rewrite as a pure function over a select's columns,
  public so you can drive it directly or reuse it in your own operation. It matches on the table
  alias, so a joined query expands only the table the column belongs to.

- docs: the write side and the concurrency comparison itself need nothing from this package.
  `excludeColumn` already ships with `SqlHydra.Query`, and `where (u.id = id && u.xmin = expected)`
  is an ordinary column comparison.
