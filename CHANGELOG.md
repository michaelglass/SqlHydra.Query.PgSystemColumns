# Changelog

Notable changes to SqlHydra.Query.PgSystemColumns. `fssemantictagger` reads this file: the `## Unreleased`
section must be non-empty before `mise run release` will cut a tag, and it is
promoted to the new version heading on release.

## Unreleased

- feat: **the generator emits the column — no hand-written field, no text overlay.**
  `Codegen.PgSystemColumns` implements SqlHydra's `IContributeColumns`, so the tables you name
  get their system columns from `dotnet sqlhydra npgsql` itself, with `[<ProviderDbType("Xid")>]`
  and a doc comment on the field. A system column is not in `information_schema`, so
  `IExtendTypeMapping` could never reach it: a type mapping only fires for a column the provider
  already discovered. Until now the only way in was to write the field by hand, or to rewrite the
  generated file after the generator had written it.

- feat: **system columns are named per table, in the `{schema}/{table}.{column}` grammar**
  (`[ "person/address.xmin"; "sales/*.xmin" ]`), the table part a glob — the same grammar
  SqlHydra's `[filters]` uses. Subclass `Codegen.PgSystemColumns` with a parameterless
  constructor in the project the generator runs over, and register **that project** in the TOML
  `[extensions]` section. A bare `"xmin"`, a malformed entry, or a column that is not one of the
  six fails when the extension is constructed, before the generator opens a connection.

- feat: **`Codegen.all`, `Codegen.column`, `Codegen.parseEntry` and `Codegen.contributeTo`** —
  the six columns and the contribution decision as plain values and pure functions, so you can
  drive them directly. `contributeTo` returns nothing for a view (`SELECT xmin FROM a_view` is an
  error unless the view projects one) and nothing for a non-PostgreSQL provider.

- change: **there is no zero-configuration default, and `Codegen.PgSystemColumns` is abstract.**
  Registering this package alone in `[extensions]` does nothing. A blanket default would have to
  contribute to every base table, and that is not neutral — it breaks every table it touches,
  because `SELECT t.*` does not return a system column and a record that declares the field fails
  to hydrate on every whole-entity read. Since SqlHydra's `[extensions]` section is a bare list of
  assembly names with nowhere to put a setting, the choice has to be a type in the consumer's own
  project.

- change: **the write side needs no `excludeColumn`.** A contributed system column is marked
  read-only, and SqlHydra 5.0 splits a table with read-only columns into a read record and a
  companion `{table}_write` record, so the column cannot reach an `INSERT` column list or an
  `UPDATE SET` clause at all.

- change: **BRANCH ONLY — requires a SqlHydra carrying the `IContributeColumns` seam**
  (`SqlHydra.Query`/`SqlHydra.Cli` `5.1.0-seam.1`, packed locally from `ext/contribute-columns`;
  see `NuGet.config`). `tests/verify-package-metadata.fsx` carries a matching, single-constant
  exception. Both must go back to a stable release before this ships. The query half alone still
  works against the released `SqlHydra.Query` 5.0.0.

## 0.1.0-alpha.2 - 2026-09-10

- chore: **the SqlHydra.Query dependency floor is asserted, not just declared.**
  `tests/verify-package-metadata.fsx` fails when the floor is a prerelease or is not the stable
  host release (5.0.0). It runs as its own CI job on every push and pull request and in the
  local `check`/`ci` tasks, so a routine bump cannot quietly move consumers onto a prerelease.
- feat!: **require SqlHydra.Query 5.0.0.** SqlHydra 5.0 is a major release, so a consumer of
  this package moves to it too rather than staying on 4.1.x. Nothing in this package's own
  surface changes: it builds and its tests pass against 5.0.0 unmodified, so the bump is the
  whole change.
- chore: bump Microsoft.SourceLink.GitHub to 10.0.401, which carries a Microsoft.Build.Tasks.Git
  without the GHSA-23fw-v26w-5fgq advisory. Unrelated to the SqlHydra bump; the audit failed the
  build on the old pin either way.
## 0.1.0-alpha.1 - 2026-09-01

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
