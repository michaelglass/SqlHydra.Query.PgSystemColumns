// sync:usage-opens:start
open SqlHydra.Query
open SqlHydra.Query.PgSystemColumns.SystemColumns
// sync:usage-opens:end

open System
open SqlHydra

// In a real app `dotnet sqlhydra` generates this record. `xmin` is a PostgreSQL system
// column: the database owns it, `SELECT u.*` does not return it, and it changes on every
// write to the row — which is what makes it a version.
[<CLIMutable>]
type users =
    { [<ProviderDbType("Uuid")>]
      id: Guid
      [<ProviderDbType("Text")>]
      email: string
      [<ProviderDbType("Xid")>]
      xmin: uint32 }

let usersTable = table<users>

let emitter = PostgresEmitter() :> ISqlEmitter
let sqlOf (query: SelectQuery) = (query.CompileWith emitter).Sql

let userId = Guid.NewGuid()

// The region below is sourced verbatim into README.md via syncdocs `src=`; edits here
// (comments included) change the README.

// sync:usage-queries:start
// Read a row together with its version. Without `withSystemColumns` the emitted SQL is
// `SELECT "u".*`, which omits `xmin`, and hydrating a record that declares the field
// fails.
let userWithVersion =
    select {
        for u in usersTable do
            where (u.id = userId)
            select u
            withSystemColumns u.xmin
    }

// Compare-and-swap: the version you read goes into the predicate. If someone else wrote
// first, `xmin` no longer matches, the UPDATE affects zero rows, and you can refuse the
// edit instead of silently overwriting theirs. No extension is needed for this — it is an
// ordinary column comparison, and `[<ProviderDbType("Xid")>]` binds the parameter as
// `xid`.
let guardedUpdate (expectedVersion: uint32) =
    update {
        for u in usersTable do
            set u.email "new@example.com"
            where (u.id = userId && u.xmin = expectedVersion)
    }
// sync:usage-queries:end

// The projection rewrite is public and pure, so you can drive it directly — useful if
// you are writing your own operation over a select's IR.
let expandedByHand = expandProjection ("u", "xmin") [ SelectColumn.AllColumns "u" ]

[<EntryPoint>]
let main _ =
    printfn "read with version:\n  %s\n" (sqlOf userWithVersion)
    printfn "expandProjection: %A\n" expandedByHand
    printfn "compare-and-swap predicate is a plain column comparison; no extension needed."
    printfn "write-shaped placeholder: %i" unwrittenSystemColumn
    0
