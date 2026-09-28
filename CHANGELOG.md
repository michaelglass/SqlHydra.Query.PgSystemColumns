# Changelog

Notable changes to SqlHydra.Query.PgSystemColumns. `fssemantictagger` reads this file: the `## Unreleased`
section must be non-empty before `mise run release` will cut a tag, and it is
promoted to the new version heading on release.

## Unreleased

- feat: **the generator emits the system column; nothing patches the generated file.** Name the
  columns in the project `sqlhydra` generates into (`<PgSystemColumns>public/users.xmin;sales/*.xmin</PgSystemColumns>`),
  register `SqlHydra.Query.PgSystemColumns` in the TOML `[extensions]`, and `dotnet sqlhydra npgsql`
  adds the field to each named base table with `[<ProviderDbType("Xid")>]` and a doc comment. It
  implements SqlHydra 5.1's `IContributeColumns`. Until now the field had to be written by hand
  or patched into the generated file, because a system column is not in `information_schema`.

- feat: **a generated system column is read-only.** PostgreSQL rejects an assignment to any system
  column, so the extension contributes each one as `ContributedColumn.ReadOnly`. SqlHydra then
  leaves it out of `entity`'s column list and emits a `{table}_write` record without it, and no
  `excludeColumn` is needed.

- feat: **the configuration is checked before the generator reads the schema.** A registered
  package with no entries, an entry without a schema and table (a bare `xmin`), or a column that
  is not one of the six stops `sqlhydra` with a message saying what to write. Views and
  providers other than PostgreSQL get nothing.

- feat: **`Codegen.all`, `Codegen.column`, `Codegen.parseEntry`, `Codegen.contributeTo` and
  `Codegen.readEntries`**: the six columns and the contribution rules as values and pure
  functions, for writing your own `IContributeColumns`. `SystemColumnsCodegen` is the extension
  the generator loads.

- feat: **the package's MSBuild targets copy its assembly into `bin/`**, where `sqlhydra` loads an
  extension from, so a library project needs no `CopyLocalLockFileAssemblies` or copy target for
  it. They run before compiling, so a build that fails on a field the generated file does not
  have yet still updates the entries.

- feat!: **require SqlHydra.Query 5.1.0.** Consumers on 5.0.x must move to 5.1.0. The query half
  builds and passes its tests against 5.1.0 unmodified. The README names the new floor; the
  published 0.1.0-alpha.2 page said 4.1.1, which the package has not accepted since that release.

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
