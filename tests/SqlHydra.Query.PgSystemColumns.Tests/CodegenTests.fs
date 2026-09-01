/// The codegen half: what the generator is handed for a system column, and when.
///
/// `Codegen.contributeTo` is the whole of what this package does to a schema, and it is a pure
/// function of the context the seam supplies, so these drive it directly rather than through a
/// generator and a database.
module SqlHydra.Query.PgSystemColumns.Tests.CodegenTests

open Xunit
open SqlHydra.Domain
open SqlHydra.Query.PgSystemColumns

let private table tableType =
    { Table.Catalog = ""
      Table.Schema = "public"
      Table.Name = "widgets"
      Table.Type = tableType
      Table.Columns = []
      Table.TotalColumns = 0 }

let private ctx provider tableType =
    { ColumnContributionContext.Table = table tableType
      Provider = provider }

let private npgsqlTable = ctx ProviderType.Npgsql TableType.Table

// ---------------------------------------------------------------------------------------
// The columns themselves
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``the six system columns are the six PostgreSQL documents`` () =
    Assert.Equal<string list>([ "tableoid"; "xmin"; "cmin"; "xmax"; "cmax"; "ctid" ], Codegen.names)

[<Fact>]
let ``xmin is a uint bound as xid`` () =
    let xmin = Codegen.column "xmin"

    Assert.Equal("uint", xmin.TypeMapping.ClrType)
    // Mandatory, not decoration. Npgsql has no default mapping for uint32, so a
    // compare-and-swap parameter without this throws client-side before any SQL is sent.
    Assert.Equal(Some "Xid", xmin.TypeMapping.ProviderDbType)

[<Fact>]
let ``every system column names a provider db type`` () =
    // The invariant behind the previous test, for all six: a uint32 with no NpgsqlDbType is
    // unbindable, so a column that reached the generated file without one would be a field
    // you could read and never compare.
    let missing =
        Codegen.all |> List.filter (fun col -> col.TypeMapping.ProviderDbType.IsNone)

    Assert.Empty(missing)

[<Fact>]
let ``every system column is read-only`` () =
    // PostgreSQL refuses `INSERT INTO t (xmin)` and `SET xmin = ...` outright. `IsReadOnly`
    // emits `[<ReadOnlyColumn>]`, which keeps SqlHydra.Query from ever putting the column in
    // either -- so a record read back can be written back unchanged.
    Assert.All(Codegen.all, fun col -> Assert.True(col.IsReadOnly))

[<Fact>]
let ``every system column documents itself`` () =
    // The doc travels with the column into the generated type. A caution that lives only in
    // this repo's README reaches whoever configured the extension and nobody else.
    Assert.All(Codegen.all, fun col -> Assert.NotEmpty(col.Doc))

[<Fact>]
let ``ctid warns that it is not a row identifier`` () =
    // The one that will be misused. It is a physical address and it moves.
    let doc = Codegen.column "ctid" |> _.Doc |> String.concat " "

    Assert.Contains("not a row identifier", doc)

[<Fact>]
let ``a name is normalised before it is looked up`` () =
    Assert.Equal("xmin", (Codegen.column "  XMIN ").Name)

[<Fact>]
let ``a name that is not a system column fails, naming the six`` () =
    // Contributing nothing would generate a file that compiles, omits the field, and fails at
    // the first read that hydrates the record -- a long way from the typo that caused it.
    let ex = Assert.Throws<System.Exception>(fun () -> Codegen.column "xmim" |> ignore)

    Assert.Contains("xmim", ex.Message)
    Assert.Contains("xmin", ex.Message)

// ---------------------------------------------------------------------------------------
// When they are contributed
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``a PostgreSQL base table gets the named columns`` () =
    let contributed = Codegen.contributeTo [ "xmin"; "tableoid" ] npgsqlTable

    Assert.Equal<string list>([ "xmin"; "tableoid" ], contributed |> List.map _.Name)

[<Fact>]
let ``a view gets nothing`` () =
    // `SELECT xmin FROM a_view` is an error unless the view projects one, so contributing
    // here would generate a record that cannot be read.
    Assert.Empty(Codegen.contributeTo [ "xmin" ] (ctx ProviderType.Npgsql TableType.View))

[<Fact>]
let ``another provider gets nothing`` () =
    // What keeps the extension harmless in a project that also generates for SQLite.
    Assert.Empty(Codegen.contributeTo [ "xmin" ] (ctx ProviderType.Sqlite TableType.Table))

// ---------------------------------------------------------------------------------------
// The extension the generator loads
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``XminColumn contributes xmin and nothing else`` () =
    // What registering this package in [extensions] gets you, with no code in your project.
    let ext = Codegen.XminColumn() :> IContributeColumns
    let contributed = ext.Contribute (fun _ -> []) npgsqlTable

    Assert.Equal<string list>([ "xmin" ], contributed |> List.map _.Name)

[<Fact>]
let ``an extension preserves what earlier extensions contributed`` () =
    // The seam composes in registration order; dropping the base call would silently discard
    // a co-registered extension's columns.
    let earlier = Codegen.column "ctid"
    let ext = Codegen.XminColumn() :> IContributeColumns
    let contributed = ext.Contribute (fun _ -> [ earlier ]) npgsqlTable

    Assert.Equal<string list>([ "ctid"; "xmin" ], contributed |> List.map _.Name)

/// How a consumer asks for something other than `xmin`: subclass, in their own project, with a
/// parameterless constructor for the generator's `Activator.CreateInstance` to find.
type CustomSystemColumns() =
    inherit Codegen.PgSystemColumns([ "xmin"; "tableoid" ])

/// The shape a consumer needs when only some tables are versioned.
type GuardedTablesOnly() =
    inherit Codegen.PgSystemColumns([ "xmin" ], Codegen.onlyTables [ "users"; "orders" ])

[<Fact>]
let ``a custom set is a two-line subclass`` () =
    let ext = CustomSystemColumns() :> IContributeColumns
    let contributed = ext.Contribute (fun _ -> []) npgsqlTable

    Assert.Equal<string list>([ "xmin"; "tableoid" ], contributed |> List.map _.Name)

[<Fact>]
let ``a table the predicate rejects gets nothing`` () =
    // A system column costs something at every use site: `SELECT t.*` does not return it, so a
    // table that carries the field and is read whole must project it or fail to hydrate.
    // Contributing to every table would break every whole-entity read in a codebase that
    // versions three of them.
    let ext = GuardedTablesOnly() :> IContributeColumns

    Assert.Empty(ext.Contribute (fun _ -> []) npgsqlTable)

[<Fact>]
let ``a table the predicate accepts gets the column`` () =
    let ext = GuardedTablesOnly() :> IContributeColumns

    // The fixture table is `widgets`, which the predicate rejects; `users` it accepts.
    let users =
        { npgsqlTable with
            Table =
                { table TableType.Table with
                    Name = "users" } }

    Assert.Equal<string list>([ "xmin" ], ext.Contribute (fun _ -> []) users |> List.map _.Name)

[<Fact>]
let ``onlyTables matches regardless of case`` () =
    let predicate = Codegen.onlyTables [ "Widgets" ]

    Assert.True(
        predicate
            { table TableType.Table with
                Name = "widgets" }
    )

    Assert.False(
        predicate
            { table TableType.Table with
                Name = "gadgets" }
    )
