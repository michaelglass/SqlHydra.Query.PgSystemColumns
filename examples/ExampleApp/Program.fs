// sync:usage-opens:start
open SqlHydra.Query
open SqlHydra.Query.PgSystemColumns.SystemColumns
// sync:usage-opens:end

open System
open SqlHydra
open SqlHydra.Domain
open SqlHydra.Query.PgSystemColumns

// `dotnet sqlhydra` generates this record, `xmin` included, once the codegen half of this
// package is registered in the TOML `[extensions]` section. Both attributes are emitted by
// the generator: `[<ProviderDbType("Xid")>]` because Npgsql has no default mapping for
// uint32, and `[<ReadOnlyColumn>]` because the database owns the value.
[<CLIMutable>]
type users =
    { [<ProviderDbType("Uuid")>]
      id: Guid
      [<ProviderDbType("Text")>]
      email: string
      [<ReadOnlyColumn>]
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
// sync:usage-queries:end

// The codegen half, driven directly. `contributeTo` is what the generator calls, once per
// table; `Codegen.all` is the six columns with the type mapping each one needs.
let contributedToABaseTable =
    Codegen.contributeTo
        [ "xmin" ]
        { Table =
            { Catalog = ""
              Schema = "public"
              Name = "users"
              Type = TableType.Table
              Columns = []
              TotalColumns = 0 }
          Provider = ProviderType.Npgsql }

// The projection rewrite is public and pure, so you can drive it directly — useful if
// you are writing your own operation over a select's IR.
let expandedByHand = expandProjection ("u", "xmin") [ SelectColumn.AllColumns "u" ]

[<EntryPoint>]
let main _ =
    printfn "read with version:\n  %s\n" (sqlOf userWithVersion)
    printfn "expandProjection: %A\n" expandedByHand

    printfn "contributed to a base table: %A" (contributedToABaseTable |> List.map _.Name)

    for col in contributedToABaseTable do
        printfn
            "  %s: %s, providerDbType=%A, readOnly=%b"
            col.Name
            col.TypeMapping.ClrType
            col.TypeMapping.ProviderDbType
            col.IsReadOnly

    printfn ""
    printfn "compare-and-swap predicate is a plain column comparison; no extension needed."
    printfn "the write side needs no excludeColumn; [<ReadOnlyColumn>] does it."
    printfn "record-construction placeholder: %i" notAVersion
    ignore (guardedUpdate { id = userId; email = ""; xmin = 1u })
    ignore (insertNew ())
    0
