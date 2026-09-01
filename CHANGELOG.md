# Changelog

Notable changes to SqlHydra.Query.PgSystemColumns. `fssemantictagger` reads this file: the `## Unreleased`
section must be non-empty before `mise run release` will cut a tag, and it is
promoted to the new version heading on release.

## Unreleased

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

- feat: **`unwrittenSystemColumn`** — the value to give a system-column field in a record you are
  about to write, to be paired with the built-in `excludeColumn`. The database owns the column, so
  the value is never sent and never read back.

- feat: **`expandProjection`** — the projection rewrite as a pure function over a select's columns,
  public so you can drive it directly or reuse it in your own operation. It matches on the table
  alias, so a joined query expands only the table the column belongs to.

- docs: the write side and the concurrency comparison itself need nothing from this package.
  `excludeColumn` already ships with `SqlHydra.Query`, and `where (u.id = id && u.xmin = expected)`
  is an ordinary column comparison.
