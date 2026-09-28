/// The codegen half: which columns the generator is handed, for which tables, and how the
/// extension learns the entries.
module SqlHydra.Query.PgSystemColumns.Tests.CodegenTests

open System
open System.Diagnostics
open System.IO
open Xunit
open SqlHydra.Domain
open SqlHydra.Query.PgSystemColumns

let private table schema name tableType =
    { Table.Catalog = ""
      Table.Schema = schema
      Table.Name = name
      Table.Type = tableType
      Table.Columns = []
      Table.TotalColumns = 0 }

let private ctx provider tableType schema name =
    { ColumnContributionContext.Table = table schema name tableType
      Provider = provider }

let private npgsqlTable = ctx ProviderType.Npgsql TableType.Table

let private widgets = npgsqlTable "public" "widgets"

let private contributed entries context =
    Codegen.contributeTo (entries |> List.map Codegen.parseEntry) context
    |> List.map _.Column.Name

let private contributedBy (ext: IContributeColumns) earlier context =
    ext.Contribute (fun _ -> earlier) context |> List.map _.Column.Name

// ---------------------------------------------------------------------------------------
// The columns
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``the six system columns are the six PostgreSQL documents`` () =
    Assert.Equal<string list>([ "tableoid"; "xmin"; "cmin"; "xmax"; "cmax"; "ctid" ], Codegen.names)

[<Fact>]
let ``xmin is a uint bound as xid`` () =
    let xmin = Codegen.column "xmin"

    Assert.Equal("uint", xmin.TypeMapping.ClrType)
    Assert.Equal(Some "Xid", xmin.TypeMapping.ProviderDbType)

[<Fact>]
let ``every system column names a provider db type`` () =
    // Npgsql has no default mapping for uint32, so a field without one could be read and
    // never compared.
    Assert.All(Codegen.all, fun col -> Assert.True(col.TypeMapping.ProviderDbType.IsSome, col.Name))

[<Fact>]
let ``every system column documents itself`` () =
    Assert.All(Codegen.all, fun col -> Assert.NotEmpty(col.Doc))

[<Fact>]
let ``ctid warns that it is not a row identifier`` () =
    let doc = Codegen.column "ctid" |> _.Doc |> String.concat " "

    Assert.Contains("Not a row identifier", doc)

[<Fact>]
let ``a name is normalised before it is looked up`` () =
    Assert.Equal("xmin", (Codegen.column "  XMIN ").Name)

[<Fact>]
let ``a name that is not a system column fails, naming the six`` () =
    let ex = Assert.Throws<Exception>(fun () -> Codegen.column "xmim" |> ignore)

    Assert.Contains("'xmim'", ex.Message)
    Assert.Contains("tableoid, xmin, cmin, xmax, cmax, ctid", ex.Message)

// ---------------------------------------------------------------------------------------
// The grammar: {schema}/{table}.{column}
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``an entry names a schema, a table and a column`` () =
    let entry = Codegen.parseEntry " sales/currency.xmin "

    Assert.Equal("sales", entry.Schema)
    Assert.Equal("currency", entry.Table)
    Assert.Equal("xmin", entry.Column.Name)

[<Fact>]
let ``an entry is split at the last dot`` () =
    let entry = Codegen.parseEntry "public/odd.table.xmin"

    Assert.Equal("odd.table", entry.Table)
    Assert.Equal("xmin", entry.Column.Name)

[<Theory>]
[<InlineData("xmin")>]
[<InlineData("users.xmin")>]
[<InlineData("/users.xmin")>]
[<InlineData("public/.xmin")>]
let ``an entry without a schema and a table is refused, showing the grammar`` (entry: string) =
    // No "every table" form: a system column on a table nobody named breaks its reads.
    let ex = Assert.Throws<Exception>(fun () -> Codegen.parseEntry entry |> ignore)

    Assert.Contains("{schema}/{table}.{column}", ex.Message)

[<Fact>]
let ``an entry naming an unknown column is refused`` () =
    let ex =
        Assert.Throws<Exception>(fun () -> Codegen.parseEntry "public/widgets.xmim" |> ignore)

    Assert.Contains("not a PostgreSQL system column", ex.Message)

// ---------------------------------------------------------------------------------------
// Which tables get what
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``a named table gets the column`` () =
    Assert.Equal<string list>([ "xmin" ], contributed [ "public/widgets.xmin" ] widgets)

[<Fact>]
let ``a table nobody named gets nothing`` () =
    Assert.Empty(contributed [ "public/users.xmin" ] widgets)

[<Fact>]
let ``the table part is a glob`` () =
    Assert.Equal<string list>([ "xmin" ], contributed [ "public/wid*.xmin" ] widgets)
    Assert.Equal<string list>([ "xmin" ], contributed [ "public/widget?.xmin" ] widgets)

[<Fact>]
let ``the schema part is a glob, and does not reach into the table`` () =
    Assert.Equal<string list>([ "xmin" ], contributed [ "*/widgets.xmin" ] widgets)
    Assert.Empty(contributed [ "public/*.xmin" ] (npgsqlTable "other" "widgets"))

[<Fact>]
let ``a glob matches the whole name, not a part of it`` () =
    Assert.Empty(contributed [ "public/widget.xmin" ] widgets)
    Assert.Empty(contributed [ "pub/widgets.xmin" ] widgets)

[<Fact>]
let ``a regex character in a name is literal`` () =
    Assert.Empty(contributed [ "public/widget[s].xmin" ] widgets)
    Assert.Equal<string list>([ "xmin" ], contributed [ "public/a+b.xmin" ] (npgsqlTable "public" "a+b"))

[<Fact>]
let ``names compare with case`` () =
    // A quoted PostgreSQL identifier keeps its case, so `Widgets` is another table.
    Assert.Empty(contributed [ "public/Widgets.xmin" ] widgets)

[<Fact>]
let ``two entries matching one table contribute the column once`` () =
    Assert.Equal<string list>([ "xmin" ], contributed [ "public/*.xmin"; "public/widgets.xmin" ] widgets)

[<Fact>]
let ``several columns on one table arrive in entry order`` () =
    Assert.Equal<string list>(
        [ "xmin"; "tableoid" ],
        contributed [ "public/widgets.xmin"; "public/widgets.tableoid" ] widgets
    )

[<Fact>]
let ``every contribution is read-only`` () =
    // PostgreSQL rejects an assignment to any system column; SqlHydra leaves a read-only
    // column off the write record and out of `entity`'s column list.
    let all: Codegen.Entry list =
        Codegen.all
        |> List.map (fun col ->
            { Schema = "public"
              Table = "widgets"
              Column = col })

    Assert.All(
        Codegen.contributeTo all widgets,
        fun contribution ->
            match contribution with
            | ContributedColumn.ReadOnly _ -> ()
            | ContributedColumn.Writable col -> failwith $"{col.Name} was contributed writable"
    )

[<Fact>]
let ``a view gets nothing`` () =
    Assert.Empty(contributed [ "*/*.xmin" ] (ctx ProviderType.Npgsql TableType.View "public" "widgets"))

[<Fact>]
let ``another provider gets nothing`` () =
    Assert.Empty(contributed [ "*/*.xmin" ] (ctx ProviderType.Sqlite TableType.Table "public" "widgets"))

// ---------------------------------------------------------------------------------------
// The extension and its entries file
// ---------------------------------------------------------------------------------------

let private withEntriesFile (lines: string list) (use': string -> unit) =
    let path =
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.{Codegen.EntriesFileName}")

    File.WriteAllLines(path, lines)

    try
        use' path
    finally
        File.Delete path

[<Fact>]
let ``the extension contributes what its entries file names`` () =
    withEntriesFile [ "public/widgets.xmin"; ""; "  public/users.ctid  " ] (fun path ->
        let ext = SystemColumnsCodegen(path) :> IContributeColumns

        Assert.Equal<string list>([ "xmin" ], contributedBy ext [] widgets)
        Assert.Equal<string list>([ "ctid" ], contributedBy ext [] (npgsqlTable "public" "users")))

[<Fact>]
let ``the extension keeps what earlier extensions contributed`` () =
    // Extensions compose in registration order; dropping the base call would discard a
    // co-registered extension's columns.
    withEntriesFile [ "public/widgets.xmin" ] (fun path ->
        let ext = SystemColumnsCodegen(path) :> IContributeColumns
        let earlier = ContributedColumn.Writable(Codegen.column "ctid")

        Assert.Equal<string list>([ "ctid"; "xmin" ], contributedBy ext [ earlier ] widgets))

[<Fact>]
let ``the extension parses its entries on construction`` () =
    withEntriesFile [ "public/widgets.xmim" ] (fun path ->
        Assert.Throws<Exception>(fun () -> SystemColumnsCodegen(path) |> ignore)
        |> ignore)

[<Fact>]
let ``a missing entries file stops the extension and says how to name the columns`` () =
    let missing = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt")
    let ex = Assert.Throws<Exception>(fun () -> SystemColumnsCodegen(missing) |> ignore)

    Assert.Contains(missing, ex.Message)
    Assert.Contains("<PgSystemColumns>", ex.Message)

[<Fact>]
let ``an entries file with no entries stops the extension`` () =
    withEntriesFile [ ""; "   " ] (fun path ->
        Assert.Throws<Exception>(fun () -> SystemColumnsCodegen(path) |> ignore)
        |> ignore)

[<Fact>]
let ``the parameterless constructor reads the file next to the assembly`` () =
    // The constructor SqlHydra.Cli calls. This test's own output directory has no entries
    // file, so the refusal names where it looked.
    let expected =
        Path.Combine(Path.GetDirectoryName(typeof<SystemColumnsCodegen>.Assembly.Location), Codegen.EntriesFileName)

    let ex = Assert.Throws<Exception>(fun () -> SystemColumnsCodegen() |> ignore)

    Assert.Contains(expected, ex.Message)

// ---------------------------------------------------------------------------------------
// The MSBuild targets: the PgSystemColumns property in, entries file and assembly out
// ---------------------------------------------------------------------------------------

/// Builds a throwaway project that imports the package's targets and references this build
/// of the assembly, the way a PackageReference would, and returns its output directory.
let private buildConsumer (dir: string) (entries: string list) =
    let repoRoot =
        let rec up (d: DirectoryInfo) =
            if File.Exists(Path.Combine(d.FullName, "SqlHydra.Query.PgSystemColumns.slnx")) then
                d.FullName
            else
                up d.Parent

        up (DirectoryInfo AppContext.BaseDirectory)

    let targets =
        Path.Combine(repoRoot, "src/SqlHydra.Query.PgSystemColumns/build/SqlHydra.Query.PgSystemColumns.targets")

    File.WriteAllText(
        Path.Combine(dir, "Consumer.csproj"),
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutDir>$(MSBuildThisFileDirectory)out/</OutDir>
    <PgSystemColumns>{String.concat ";" entries}</PgSystemColumns>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="SqlHydra.Query.PgSystemColumns" HintPath="{typeof<SystemColumnsCodegen>.Assembly.Location}" Private="false" />
  </ItemGroup>
  <Import Project="{targets}" />
</Project>
"""
    )

    let dotnet =
        match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
        | null
        | "" -> "dotnet"
        | path -> path

    let psi =
        ProcessStartInfo(dotnet, "build -nologo -v:q", WorkingDirectory = dir, RedirectStandardOutput = true)

    use proc = Process.Start psi
    let output = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    Assert.True((proc.ExitCode = 0), output)
    Path.Combine(dir, "out")

[<Fact>]
[<Trait("Category", "Integration")>]
let ``the build puts the entries and the assembly where the generator loads the extension from`` () =
    let dir = Directory.CreateTempSubdirectory("pgsystemcolumns-targets-").FullName

    try
        // `Private="false"` keeps the normal copy-local step out of it: the assembly lands in
        // the output only because the targets put it there, as for a library project.
        // Globs survive: an item's Include would have expanded `*` against the file system
        // and dropped the entry, which is why the input is a property.
        let out = buildConsumer dir [ "public/user*.xmin"; "public/orders.xmin" ]
        let entriesFile = Path.Combine(out, Codegen.EntriesFileName)

        Assert.True(File.Exists(Path.Combine(out, "SqlHydra.Query.PgSystemColumns.dll")))

        Assert.Equal<string list>(
            [ "public/user*.xmin"; "public/orders.xmin" ],
            File.ReadAllLines entriesFile |> List.ofArray
        )

        let ext = SystemColumnsCodegen(entriesFile) :> IContributeColumns
        Assert.Equal<string list>([ "xmin" ], contributedBy ext [] (npgsqlTable "public" "users"))

        // Clearing the property deletes the file, so the extension refuses rather than
        // generating from a list the project no longer declares.
        buildConsumer dir [] |> ignore
        Assert.False(File.Exists entriesFile)
    finally
        Directory.Delete(dir, true)
