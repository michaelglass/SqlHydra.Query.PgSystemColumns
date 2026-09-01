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
// The grammar: {schema}/{table}.{column}
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``an entry names a table and a column`` () =
    let pattern, col = Codegen.parseEntry "sales/currency.xmin"

    Assert.Equal("sales/currency", pattern)
    Assert.Equal("xmin", col.Name)

[<Fact>]
let ``an entry is split at the last dot`` () =
    // A schema or table may contain a dot; a system-column name never does.
    let pattern, col = Codegen.parseEntry "odd.schema/odd.table.xmin"

    Assert.Equal("odd.schema/odd.table", pattern)
    Assert.Equal("xmin", col.Name)

[<Fact>]
let ``a bare column name is refused, and the message shows the grammar`` () =
    // The old global form. It has to fail rather than mean "every table": contributing a
    // system column to a table nobody asked about breaks every whole-entity read of it.
    let ex =
        Assert.Throws<System.Exception>(fun () -> Codegen.parseEntry "xmin" |> ignore)

    Assert.Contains("{schema}/{table}.{column}", ex.Message)

// ---------------------------------------------------------------------------------------
// When they are contributed
// ---------------------------------------------------------------------------------------

let private entries = List.map Codegen.parseEntry

[<Fact>]
let ``a named table gets the column`` () =
    let contributed =
        Codegen.contributeTo (entries [ "public/widgets.xmin" ]) npgsqlTable

    Assert.Equal<string list>([ "xmin" ], contributed |> List.map _.Name)

[<Fact>]
let ``a table nobody named gets nothing`` () =
    Assert.Empty(Codegen.contributeTo (entries [ "public/users.xmin" ]) npgsqlTable)

[<Fact>]
let ``the table part is a glob`` () =
    let contributed = Codegen.contributeTo (entries [ "public/*.xmin" ]) npgsqlTable

    Assert.Equal<string list>([ "xmin" ], contributed |> List.map _.Name)

[<Fact>]
let ``a glob does not cross the schema separator`` () =
    // `public/*` must not match `other/widgets`; the schema is part of the path.
    let other =
        { npgsqlTable with
            Table =
                { table TableType.Table with
                    Schema = "other" } }

    Assert.Empty(Codegen.contributeTo (entries [ "public/*.xmin" ]) other)

[<Fact>]
let ``two entries matching one table contribute the column once`` () =
    // Overlapping globs are ordinary; a duplicated field would not compile.
    let contributed =
        Codegen.contributeTo (entries [ "public/*.xmin"; "public/widgets.xmin" ]) npgsqlTable

    Assert.Equal<string list>([ "xmin" ], contributed |> List.map _.Name)

[<Fact>]
let ``several columns on one table all arrive`` () =
    let contributed =
        Codegen.contributeTo (entries [ "public/widgets.xmin"; "public/widgets.tableoid" ]) npgsqlTable

    Assert.Equal<string list>([ "xmin"; "tableoid" ], contributed |> List.map _.Name)

[<Fact>]
let ``a view gets nothing`` () =
    // `SELECT xmin FROM a_view` is an error unless the view projects one, so contributing
    // here would generate a record that cannot be read.
    Assert.Empty(Codegen.contributeTo (entries [ "public/*.xmin" ]) (ctx ProviderType.Npgsql TableType.View))

[<Fact>]
let ``another provider gets nothing`` () =
    // What keeps the extension harmless in a project that also generates for SQLite.
    Assert.Empty(Codegen.contributeTo (entries [ "public/*.xmin" ]) (ctx ProviderType.Sqlite TableType.Table))

// ---------------------------------------------------------------------------------------
// The extension the generator loads
// ---------------------------------------------------------------------------------------

/// The only shape available: there is no safe blanket default, and `[extensions]` has nowhere
/// to put a setting, so the choice is a type in the consumer's own project.
type GuardedSystemColumns() =
    inherit Codegen.PgSystemColumns([ "public/widgets.xmin" ])

[<Fact>]
let ``a subclass contributes to the tables it names`` () =
    let ext = GuardedSystemColumns() :> IContributeColumns

    Assert.Equal<string list>([ "xmin" ], ext.Contribute (fun _ -> []) npgsqlTable |> List.map _.Name)

[<Fact>]
let ``a subclass leaves other tables alone`` () =
    let ext = GuardedSystemColumns() :> IContributeColumns

    let users =
        { npgsqlTable with
            Table =
                { table TableType.Table with
                    Name = "users" } }

    Assert.Empty(ext.Contribute (fun _ -> []) users)

[<Fact>]
let ``an extension preserves what earlier extensions contributed`` () =
    // The seam composes in registration order; dropping the base call would silently discard
    // a co-registered extension's columns.
    let ext = GuardedSystemColumns() :> IContributeColumns
    let earlier = Codegen.column "ctid"

    Assert.Equal<string list>([ "ctid"; "xmin" ], ext.Contribute (fun _ -> [ earlier ]) npgsqlTable |> List.map _.Name)

[<Fact>]
let ``a misspelled column fails when the extension is constructed`` () =
    // Before the generator opens a connection, rather than at the first read that hydrates
    // the record -- a long way from the typo that caused it.
    let ex =
        Assert.Throws<System.Exception>(fun () -> Codegen.parseEntry "public/widgets.xmim" |> ignore)

    Assert.Contains("xmin", ex.Message)
