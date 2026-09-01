/// PostgreSQL system columns inside a `SqlHydra.Query` select.
///
/// `SELECT u.*` does not return a system column, so a generated record carrying an
/// `xmin` field fails to hydrate on every whole-entity read. `withSystemColumns` names
/// the column explicitly, as a lambda over the selected row —
/// `withSystemColumns (fun u -> u.xmin)`:
///
///     SELECT "u".*      becomes      SELECT "u".*, "u"."xmin"
///
/// All six of PostgreSQL's system columns work — the operation names whichever column
/// you give it, so nothing here is specific to `xmin`:
///
///     tableoid  oid    which table the row came from
///     xmin      xid    inserting transaction: the row version
///     cmin      cid    command id within the inserting transaction
///     xmax      xid    deleting transaction, or 0 for a live row
///     cmax      cid    command id within the deleting transaction
///     ctid      tid    physical location of this row version
///
/// Chain the operation to project more than one.
///
/// `ctid` is the one to be careful with: it is a physical address, and it changes when
/// the row is updated or moved by VACUUM FULL. Use `xmin` for concurrency.
///
/// The write side needs nothing from this package. `excludeColumn u.xmin` ships with
/// `SqlHydra.Query` and drops the column from an INSERT column list or an UPDATE SET
/// clause. Comparing the column is ordinary too: `where (u.id = id && u.xmin = expected)`
/// binds natively when the generated field carries `[<ProviderDbType("Xid")>]`.
namespace SqlHydra.Query.PgSystemColumns

open System
open System.Linq.Expressions
open SqlHydra.Query

/// `open SqlHydra.Query.PgSystemColumns.SystemColumns` at a query site to bring
/// `withSystemColumns` into the `select` / `selectTask` computation expression.
module SystemColumns =

    /// The value to give a system-column field in a record you are about to WRITE, paired
    /// with the built-in `excludeColumn` so the column never reaches the statement:
    ///
    ///     let row = { existing with xmin = notAVersion }
    ///     updateTask ctx { for u in ``public``.users do
    ///                      setColumns row
    ///                      excludeColumn u.xmin
    ///                      where (u.id = id) }
    ///
    /// Named so that misuse reads wrong: `where (u.xmin = notAVersion)` compiles — the
    /// field is a plain `uint32`, so nothing can stop it — but says what it is. Only a
    /// value read back from the database is a version.
    let notAVersion: uint32 = 0u

    /// Appends the system column to the whole-entity projection of its OWN table.
    ///
    ///     expandProjection ("u", "xmin") [ AllColumns "u"; AllColumns "o" ]
    ///       = [ AllColumns "u"; SpecificColumn "u.xmin"; AllColumns "o" ]
    ///
    /// Matching on the alias is what keeps a join correct: `o.*` must not gain a column
    /// that belongs to `u`. An explicitly named column already says what it wants and a
    /// raw fragment is the caller's own SQL; neither is a `SELECT *` missing the column.
    let expandProjection
        (tableAlias: string, systemColumn: string)
        (selectColumns: SelectColumn list)
        : SelectColumn list =
        selectColumns
        |> List.collect (fun column ->
            match column with
            | SelectColumn.AllColumns alias when alias = tableAlias ->
                [ column; SelectColumn.SpecificColumn $"{alias}.{systemColumn}" ]
            | SelectColumn.AllColumns _
            | SelectColumn.SpecificColumn _
            | SelectColumn.RawColumn _ -> [ column ])

    [<AutoOpen>]
    module Operations =

        type SqlHydra.Query.SelectBuilders.SelectBuilder<'Selected, 'Mapped> with

            /// Projects a system column alongside the whole entity. Name the column
            /// with a LAMBDA over the selected row; the compiler checks it exists.
            ///
            ///     selectTask ctx {
            ///         for u in ``public``.users do
            ///             where (u.id = userId)
            ///             select u
            ///             withSystemColumns (fun u -> u.xmin)
            ///     }
            ///
            /// WHY A LAMBDA, and not the bare `withSystemColumns u.xmin` an
            /// `[<ProjectionParameter>]` would give. This operation must follow
            /// `select`, and `select` is the one operation that CHANGES the builder's
            /// row type while KEEPING the computation expression's variable space
            /// (`MaintainsVariableSpace = true`). In a JOIN the two therefore disagree:
            /// after `select u` the state's row is `users`, but the variable space is
            /// still the join tuple `(u, s)`. An `[<ProjectionParameter>]` is elaborated
            /// against the VARIABLE SPACE, so it would be handed the tuple while its
            /// signature demanded the row — and every joined read failed to compile with
            /// "expected 'users' but is a tuple of type ''a * 'b'". A plain lambda
            /// argument is elaborated against the parameter's own type instead, so it
            /// sees the SELECTED ROW in both shapes. Overloading on the tuple was tried
            /// and does not work: F# resolves same-named custom operations to the last
            /// declaration rather than per call site, so one shape always broke.
            ///
            /// Must follow `select u`, whose `SELECT u.*` it expands. Placing it before
            /// `select` raises at query construction. Placing it after a scalar select
            /// does not compile: after `select u.email` the row is a `string`, so naming
            /// `u.xmin` is a type error.
            [<CustomOperation("withSystemColumns", MaintainsVariableSpace = true)>]
            member _.WithSystemColumns
                (state: QuerySource<'T, SelectQueryIR>, columnSelector: Expression<Func<'T, 'Prop>>)
                : QuerySource<'T, SelectQueryIR> =
                let ir = state.Query

                // A `select u` emits exactly one whole-entity projection, even in a join,
                // so the alias is never ambiguous. Taking it from the projection rather
                // than from the selector leaves the lambda's parameter name free.
                let wholeEntityAliases =
                    ir.Select
                    |> List.choose (function
                        | SelectColumn.AllColumns alias -> Some alias
                        | _ -> None)

                let systemColumn =
                    match SelectBuilders.ExtensionHelpers.tryGetOrderByColumn columnSelector with
                    | Some(_, column) -> column
                    | None ->
                        failwith
                            "withSystemColumns expects a single column, as in `withSystemColumns (fun u -> u.xmin)`."

                match wholeEntityAliases with
                | [ tableAlias ] ->
                    QuerySource<'T, SelectQueryIR>(
                        { ir with
                            Select = expandProjection (tableAlias, systemColumn) ir.Select },
                        state.TableMappings
                    )
                | [] ->
                    failwith (
                        "withSystemColumns: this query has no whole-entity projection to expand yet. Move it AFTER "
                        + "`select <row>`, which is what emits the `SELECT alias.*` this expands."
                    )
                | many ->
                    let aliases = String.concat ", " many

                    failwith
                        $"withSystemColumns: this query projects %d{many.Length} whole entities (%s{aliases}); which one to expand is ambiguous."
