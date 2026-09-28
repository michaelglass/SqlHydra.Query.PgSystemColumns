/// The codegen half: puts a system column on the record `SqlHydra.Cli` generates.
///
/// `SqlHydra.Cli` learns its columns from `information_schema`, which never lists a system
/// column, so no type mapping is ever consulted for one. SqlHydra 5.1's `IContributeColumns`
/// is the stage that adds a column the catalog does not list, and `SystemColumnsCodegen`
/// implements it.
///
/// Name the columns in the project the generator runs over, and register this package:
///
///     <PropertyGroup>
///       <PgSystemColumns>public/users.xmin;sales/*.xmin</PgSystemColumns>
///     </PropertyGroup>
///
///     [extensions]
///     type_mappings = [ "SqlHydra.Query.PgSystemColumns" ]
///
/// The package's MSBuild targets write the entries next to this assembly in the build output,
/// which is where the generator loads it from, and the extension reads them from there.
namespace SqlHydra.Query.PgSystemColumns

open System
open System.Data
open System.IO
open System.Text.RegularExpressions
open SqlHydra.Domain

module Codegen =

    /// `NpgsqlDbType` case names, as strings: `SqlHydra.Query` resolves the name against the
    /// provider's enum when it binds a parameter, so this package needs no Npgsql reference.
    let private mapping (columnTypeAlias: string) (clrType: string) (dbType: DbType) (providerDbType: string) =
        { TypeMapping.ColumnTypeAlias = columnTypeAlias
          TypeMapping.ClrType = clrType
          TypeMapping.DbType = dbType
          TypeMapping.ProviderDbType = Some providerDbType }

    let private oid = mapping "oid" "uint" DbType.UInt32 "Oid"
    let private xid = mapping "xid" "uint" DbType.UInt32 "Xid"
    let private cid = mapping "cid" "uint" DbType.UInt32 "Cid"
    let private tid = mapping "tid" "NpgsqlTypes.NpgsqlTid" DbType.Object "Tid"

    let private systemColumn name typeMapping doc =
        { Column.Name = name
          Column.TypeMapping = typeMapping
          Column.IsNullable = false
          Column.IsPK = false
          // Overwritten from the `ContributedColumn` case; true is what every one of these is.
          Column.IsReadOnly = true
          Column.Doc = doc }

    /// All six [system columns](https://www.postgresql.org/docs/current/ddl-system-columns.html),
    /// in the order the PostgreSQL manual lists them.
    ///
    /// Each carries a `ProviderDbType`, which the generator emits as `[<ProviderDbType(...)>]`.
    /// It is required, not decorative: Npgsql has no default mapping for `uint32`, so without it
    /// a compare-and-swap parameter throws before any SQL is sent ("Writing values of
    /// 'System.UInt32' is not supported for parameters having no NpgsqlDbType").
    let all: Column list =
        [ systemColumn
              "tableoid"
              oid
              [ "The OID of the table this row came from. Constant for a query against one table;"
                "useful when the query spans a partition or inheritance hierarchy." ]

          systemColumn
              "xmin"
              xid
              [ "PostgreSQL's row version: the id of the transaction that inserted this row version."
                "It changes on every write to the row, so it works as an optimistic-concurrency check:"
                "read it, then include it in the WHERE of the UPDATE. If someone else wrote first, the"
                "UPDATE matches no rows. A whole-entity read returns it only with `withSystemColumns`." ]

          systemColumn
              "cmin"
              cid
              [ "The command id within the inserting transaction. Only meaningful inside that transaction." ]

          systemColumn "xmax" xid [ "The id of the transaction that deleted this row version, or 0 for a live row." ]

          systemColumn
              "cmax"
              cid
              [ "The command id within the deleting transaction. Only meaningful inside that transaction." ]

          systemColumn
              "ctid"
              tid
              [ "Not a row identifier. `ctid` is the physical location of this row version, and it"
                "changes when the row is updated and when VACUUM FULL or CLUSTER moves it. Use the"
                "primary key for identity and `xmin` for versioning." ] ]

    /// The six names, in `all`'s order.
    let names = all |> List.map _.Name

    /// The `Column` for a system-column name, ignoring case and surrounding space.
    ///
    /// An unknown name raises rather than contributing nothing: nothing would generate a file
    /// that compiles and fails at the first read that needs the field.
    let column (name: string) : Column =
        let normalized = name.Trim().ToLowerInvariant()

        match all |> List.tryFind (fun col -> col.Name = normalized) with
        | Some col -> col
        | None -> failwith $"""'{name}' is not a PostgreSQL system column. The six are: {String.concat ", " names}."""

    /// One entry: which tables get which column.
    type Entry =
        {
            /// A glob over the schema name: `*` is any run of characters, `?` any one.
            Schema: string
            /// A glob over the table name, as `Schema` is.
            Table: string
            Column: Column
        }

    let private entryPattern =
        Regex(@"^(?<schema>[^/]+)/(?<table>[^/]+)\.(?<column>[^./]+)$")

    /// Parses `{schema}/{table}.{column}`, the path shape SqlHydra's `[filters]` uses. Schema and
    /// table are globs, but simpler ones than `[filters]`: only `*` (any run of characters) and
    /// `?` (any one) are special, and `[`, `{` are literal. The column is one of the six.
    ///
    /// The table ends at the LAST dot, because a table name may contain one and a system-column
    /// name never does. A bare column name raises: a system column on a table nobody named
    /// breaks that table's whole-entity reads, so there is no "every table" form.
    let parseEntry (entry: string) : Entry =
        let m = entryPattern.Match(entry.Trim())

        if not m.Success then
            failwith
                $"'{entry}' is not a system-column entry. Write \"{{schema}}/{{table}}.{{column}}\", e.g. \"public/users.xmin\"."

        { Schema = m.Groups["schema"].Value
          Table = m.Groups["table"].Value
          Column = column m.Groups["column"].Value }

    let private globMatches (pattern: string) (name: string) =
        let regex =
            "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$"

        Regex.IsMatch(name, regex)

    /// What the entries contribute to one table: the columns whose entry matches it, each once,
    /// every one `ReadOnly`.
    ///
    /// Base tables on PostgreSQL only. A view has no system columns of its own (`SELECT xmin
    /// FROM a_view` fails unless the view projects one), so a view record carrying the field
    /// could not be read. A materialized view does have them, but SqlHydra reports it as a
    /// view, so it gets nothing either. `ReadOnly` because PostgreSQL rejects any statement that assigns to a
    /// system column; SqlHydra leaves a read-only column off the `{table}_write` record and out
    /// of `entity`'s column list.
    let contributeTo (entries: Entry list) (ctx: ColumnContributionContext) : ContributedColumn list =
        if ctx.Provider = ProviderType.Npgsql && ctx.Table.Type = TableType.Table then
            entries
            |> List.filter (fun entry ->
                globMatches entry.Schema ctx.Table.Schema
                && globMatches entry.Table ctx.Table.Name)
            |> List.map _.Column
            // Two globs can match one table; a repeated field would not compile.
            |> List.distinctBy _.Name
            |> List.map ContributedColumn.ReadOnly
        else
            []

    /// The file the package's MSBuild targets write next to this assembly: the project's
    /// `PgSystemColumns` property, one entry per line.
    [<Literal>]
    let EntriesFileName = "SqlHydra.Query.PgSystemColumns.codegen.txt"

    /// Reads and parses an entries file.
    ///
    /// A missing or empty file raises. The extension only runs when `[extensions]` names this
    /// package, so an entry list that is not there is a configuration mistake, and generating
    /// without the columns would exit 0 with every record missing its field.
    let readEntries (path: string) : Entry list =
        let lines =
            if File.Exists path then
                File.ReadAllLines path
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.toList
            else
                []

        if lines.IsEmpty then
            failwith (
                $"SqlHydra.Query.PgSystemColumns is registered in [extensions], but '{path}' names no system "
                + "columns. Name them in the project the generator runs over, then build it; the build writes "
                + "that file:\n\n"
                + "  <PropertyGroup>\n"
                + "    <PgSystemColumns>public/users.xmin</PgSystemColumns>\n"
                + "  </PropertyGroup>"
            )

        lines |> List.map parseEntry

/// The `IContributeColumns` extension `SqlHydra.Cli` loads when `[extensions]` names this
/// package. It contributes the system columns named by the project's `PgSystemColumns` property,
/// which the package's MSBuild targets write to `Codegen.EntriesFileName` next to this assembly.
///
/// The entries are read and checked on construction, so a malformed entry or a misspelled
/// column fails before the generator reads the schema.
type SystemColumnsCodegen(entriesFile: string) =
    let entries = Codegen.readEntries entriesFile

    /// Reads the entries file next to this assembly: the constructor `SqlHydra.Cli` calls.
    new() =
        let here = Path.GetDirectoryName(typeof<SystemColumnsCodegen>.Assembly.Location)

        SystemColumnsCodegen(Path.Combine(here, Codegen.EntriesFileName))

    interface IContributeColumns with
        member _.Contribute(baseFn) =
            fun ctx -> baseFn ctx @ Codegen.contributeTo entries ctx
