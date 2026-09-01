/// The codegen half of this package: it makes the system column EXIST in the generated record.
///
/// `SqlHydra.Cli` learns its columns from `information_schema`, which never lists a system
/// column. No type mapping is ever consulted for one, so `IExtendTypeMapping` cannot help: a
/// mapping only fires for a column that was already discovered. Before `IContributeColumns`
/// the only way to get `xmin` into the generated file was to rewrite the file after the
/// generator had written it.
///
/// Register this in the `[extensions]` section of your `sqlhydra-*.toml` alongside the package
/// reference, and the generator emits the field itself:
///
///     [extensions]
///     type_mapping = [ "SqlHydra.Query.PgSystemColumns" ]
///
/// What comes out, for `xmin`:
///
///     /// The id of the transaction that inserted this row version -- PostgreSQL's row version.
///     /// ...
///     [<ReadOnlyColumn>]
///     [<ProviderDbType("Xid")>]
///     xmin: uint
///
/// The `[<ProviderDbType("Xid")>]` is mandatory rather than decorative. Npgsql has no default
/// mapping for `uint32`, so a compare-and-swap parameter throws client-side without it:
/// "Writing values of 'System.UInt32' is not supported for parameters having no NpgsqlDbType".
/// Reads need nothing.
namespace SqlHydra.Query.PgSystemColumns

open System.Data
open GlobExpressions
open SqlHydra.Domain

module Codegen =

    /// `NpgsqlDbType` case names, as strings. `SqlHydra.Query` resolves the name against the
    /// provider's enum at parameter-binding time, so naming them here costs this package no
    /// dependency on Npgsql -- which matters, because the codegen half is loaded into the
    /// generator's process and the query half into yours.
    let private mapping (columnTypeAlias: string) (clrType: string) (dbType: DbType) (providerDbType: string) =
        { TypeMapping.ColumnTypeAlias = columnTypeAlias
          TypeMapping.ClrType = clrType
          TypeMapping.DbType = dbType
          TypeMapping.ProviderDbType = Some providerDbType }

    let private xid = mapping "xid" "uint" DbType.UInt32 "Xid"
    let private cid = mapping "cid" "uint" DbType.UInt32 "Cid"

    let private systemColumn name typeMapping doc =
        { Column.Name = name
          Column.TypeMapping = typeMapping
          Column.IsNullable = false
          Column.IsPK = false
          // The database owns every one of them. PostgreSQL refuses `INSERT INTO t (xmin)`
          // and `SET xmin = ...` outright, so this is not a stylistic preference: it is the
          // difference between a record that can be round-tripped and one that cannot.
          //
          // The field this sets belongs to the read-only work (upstream PR #149), not to the
          // contribution seam, and its final shape is that PR's to decide. It models
          // read-only-ness as a record rather than a flag, so a GENERATED column (which
          // `SELECT *` does return) stays distinguishable from a system column (which it does
          // not). That distinction is the right one; this line follows it when it lands.
          Column.IsReadOnly = true
          Column.Doc = doc }

    /// All six [system columns](https://www.postgresql.org/docs/current/ddl-system-columns.html),
    /// in the order the PostgreSQL manual lists them.
    let all: Column list =
        [ systemColumn
              "tableoid"
              (mapping "oid" "uint" DbType.UInt32 "Oid")
              [ "The OID of the table this row came from. Constant for a query against one table;"
                "it earns its keep when the query spans a partition or inheritance hierarchy." ]

          systemColumn
              "xmin"
              xid
              [ "The id of the transaction that inserted this row version -- PostgreSQL's row"
                "version. It changes on every write to the row, which is what makes it usable as"
                "an optimistic-concurrency check: read it, then include it in the WHERE of the"
                "UPDATE. If someone else wrote first the UPDATE matches no rows." ]

          systemColumn
              "cmin"
              cid
              [ "The command id within the inserting transaction. Only meaningful inside that"
                "transaction." ]

          systemColumn "xmax" xid [ "The id of the transaction that deleted this row version, or 0 for a live row." ]

          systemColumn
              "cmax"
              cid
              [ "The command id within the deleting transaction. Only meaningful inside that"
                "transaction." ]

          systemColumn
              "ctid"
              (mapping "tid" "NpgsqlTypes.NpgsqlTid" DbType.Object "Tid")
              [ "WARNING: not a row identifier. `ctid` is the physical location of this row"
                "version, and it changes when the row is updated and when VACUUM FULL or CLUSTER"
                "moves it. It is valid only for as long as you hold the row you read it from."
                "Use the primary key for identity and `xmin` for versioning." ] ]

    /// The six names, for error messages.
    let names = all |> List.map _.Name

    /// The `Column` for a system-column name, or a failure naming the six that exist.
    ///
    /// A typo has to stop the build. Contributing nothing for an unrecognised name would
    /// generate a file that compiles, omits the field, and fails at the first read that
    /// hydrates the record -- a long way from the line that caused it.
    let column (name: string) : Column =
        let normalized = name.Trim().ToLowerInvariant()

        match all |> List.tryFind (fun col -> col.Name = normalized) with
        | Some col -> col
        | None ->
            failwithf "'%s' is not a PostgreSQL system column. The six are: %s." name (names |> String.concat ", ")

    /// One `system_columns` entry: `{schema}/{table}.{column}`, the table part a glob. The
    /// same grammar a column filter in SqlHydra's `[filters]` uses, and the same grammar the
    /// in-library `system_columns` setting uses -- the two paths should not spell the same
    /// choice two different ways.
    ///
    /// Split at the LAST dot, because a schema or table may contain one and a system-column
    /// name never does.
    let parseEntry (entry: string) : string * Column =
        match entry.LastIndexOf '.' with
        | -1 ->
            failwithf
                "'%s' does not name a table. Write \"{schema}/{table}.{column}\" -- e.g. \"sales/currency.xmin\"."
                entry
        | i -> entry.Substring(0, i), column (entry.Substring(i + 1))

    /// The columns to contribute to one table: those whose entry matches it, on PostgreSQL,
    /// on base tables.
    ///
    /// A VIEW has no system columns of its own -- `SELECT xmin FROM a_view` is an error unless
    /// the view happens to project one -- so contributing to one would generate a record that
    /// cannot be read. The provider check is what keeps the same extension harmless when it is
    /// registered in a project that also generates for SQL Server or SQLite.
    let contributeTo (entries: (string * Column) list) (ctx: ColumnContributionContext) : Column list =
        if ctx.Provider = ProviderType.Npgsql && ctx.Table.Type = TableType.Table then
            let path = $"{ctx.Table.Schema}/{ctx.Table.Name}"

            entries
            |> List.filter (fun (tablePattern, _) -> Glob(tablePattern).IsMatch path)
            |> List.map snd
            // Two entries can match the same table through different globs.
            |> List.distinctBy _.Name
        else
            []

    /// Contributes system columns to the tables you name. Inherit it with a parameterless
    /// constructor, in the project the generator runs over:
    ///
    ///     type MySystemColumns() =
    ///         inherit PgSystemColumns([ "public/users.xmin"; "sales/*.xmin" ])
    ///
    /// THERE IS NO DEFAULT, and this class is abstract, so registering this package alone in
    /// `[extensions]` does nothing -- deliberately. A system column contributed to a table
    /// nobody asked about does not merely add an unused field: `SELECT t.*` does not return
    /// one, so the record fails to hydrate on every whole-entity read of that table. There is
    /// no safe blanket default to offer, and SqlHydra's `[extensions]` section is a bare list
    /// of assembly names with nowhere to put a setting, so the choice has to be a type in your
    /// own project.
    ///
    /// Abstract also keeps `SqlHydra.Cli` from instantiating this base alongside your subclass:
    /// it constructs every non-abstract `ISqlHydraExtension` it finds in a registered assembly.
    [<AbstractClass>]
    type PgSystemColumns(entries: string list) =
        /// Parsed on construction, so a malformed entry or a misspelled column fails before the
        /// generator opens a connection rather than at the first read that hydrates the record.
        member _.Entries = entries |> List.map parseEntry

        interface IContributeColumns with
            member this.Contribute(baseFn) =
                fun ctx -> baseFn ctx @ contributeTo this.Entries ctx
