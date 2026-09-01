module SqlHydra.Query.PgSystemColumns.Tests.SystemColumnsTests

open Xunit
open SqlHydra.Query
open SqlHydra.Query.PgSystemColumns
open SqlHydra.Query.PgSystemColumns.SystemColumns

// `expandProjection` is the whole of what this package does to a query, and it is pure,
// so the tests drive it directly rather than through a database.

[<Fact>]
let ``a whole-entity projection gains the system column`` () =
    let result =
        SystemColumns.expandProjection ("u", "xmin") [ SelectColumn.AllColumns "u" ]

    Assert.Equal<SelectColumn list>([ SelectColumn.AllColumns "u"; SelectColumn.SpecificColumn "u.xmin" ], result)

[<Fact>]
let ``a join expands only the table the column belongs to`` () =
    // The bug this pins: appending to every `AllColumns` would put `u.xmin` under `o.*`
    // as well, so a joined query would select a column the other table does not have.
    let result =
        SystemColumns.expandProjection ("u", "xmin") [ SelectColumn.AllColumns "u"; SelectColumn.AllColumns "o" ]

    Assert.Equal<SelectColumn list>(
        [ SelectColumn.AllColumns "u"
          SelectColumn.SpecificColumn "u.xmin"
          SelectColumn.AllColumns "o" ],
        result
    )

[<Fact>]
let ``a named column is left alone`` () =
    // `select u.email` already says what it wants; it is not a `SELECT *` missing a column.
    let columns = [ SelectColumn.SpecificColumn "u.email" ]

    Assert.Equal<SelectColumn list>(columns, SystemColumns.expandProjection ("u", "xmin") columns)

[<Fact>]
let ``a raw fragment is left alone`` () =
    let columns = [ SelectColumn.RawColumn("count(*) over ()", [||]) ]

    Assert.Equal<SelectColumn list>(columns, SystemColumns.expandProjection ("u", "xmin") columns)

[<Fact>]
let ``a column from a table this query does not project changes nothing`` () =
    // Documents a silent no-op: naming `o.xmin` when only `u.*` is projected adds nothing
    // rather than producing `o.xmin` against a table that is not in the select.
    let columns = [ SelectColumn.AllColumns "u" ]

    Assert.Equal<SelectColumn list>(columns, SystemColumns.expandProjection ("o", "xmin") columns)

[<Fact>]
let ``the write placeholder is not a version anyone could have read`` () =
    // Pinned so nobody "tidies" it into a value the database could plausibly return.
    Assert.Equal(0u, SystemColumns.unwrittenSystemColumn)

// The tests above cover `expandProjection` in isolation. These drive the custom
// operation itself — the thing callers actually write — by compiling a real query to
// SQL. `PostgresEmitter` needs no database.

open System
open SqlHydra

[<CLIMutable>]
type users =
    { [<ProviderDbType("Uuid")>]
      id: Guid
      [<ProviderDbType("Text")>]
      email: string
      [<ProviderDbType("Xid")>]
      xmin: uint32
      [<ProviderDbType("Xid")>]
      xmax: uint32
      [<ProviderDbType("Cid")>]
      cmin: uint32
      [<ProviderDbType("Cid")>]
      cmax: uint32
      [<ProviderDbType("Tid")>]
      ctid: string
      [<ProviderDbType("Oid")>]
      tableoid: uint32 }

[<CLIMutable>]
type user_settings =
    { [<ProviderDbType("Uuid")>]
      id: Guid
      [<ProviderDbType("Uuid")>]
      user_id: Guid
      [<ProviderDbType("Boolean")>]
      auto_brief_enabled: bool }

let private usersTable = table<users>
let private userSettingsTable = table<user_settings>
let private emitter = PostgresEmitter() :> ISqlEmitter
let private sqlOf (query: SelectQuery) = (query.CompileWith emitter).Sql

[<Fact>]
let ``the emitted SQL names the system column alongside the entity`` () =
    let sql =
        sqlOf (
            select {
                for u in usersTable do
                    where (u.id = Guid.Empty)
                    select u
                    withSystemColumns (fun u -> u.xmin)
            }
        )

    Assert.Contains("\"u\".*, \"u\".\"xmin\"", sql)

[<Fact>]
let ``without the operation the system column is absent`` () =
    // The control for the test above: `SELECT u.*` really does omit xmin, which is the
    // whole reason this package exists.
    let sql =
        sqlOf (
            select {
                for u in usersTable do
                    select u
            }
        )

    Assert.DoesNotContain("xmin", sql)

[<Fact>]
let ``placed before select, it is refused at query-construction time`` () =
    // The only misuse that reaches this guard. Following a SCALAR select cannot reach it
    // at all: after `select u.email` the row type is String, so naming `u.xmin` is a
    // compile error — the column selector rules that case out for free.
    let build () =
        select {
            for u in usersTable do
                where (u.id = Guid.Empty)
                withSystemColumns (fun u -> u.xmin)
                select u
        }
        |> ignore

    let ex = Assert.Throws<Exception>(build)
    Assert.Contains("no whole-entity projection", ex.Message)

[<Fact>]
let ``every PostgreSQL system column can be projected`` () =
    // Nothing in the operation is specific to xmin: it names whichever column it is
    // given. Pinned for all six so a future change cannot quietly narrow it.
    Assert.Contains(
        "\"u\".\"xmin\"",
        sqlOf (
            select {
                for u in usersTable do
                    select u
                    withSystemColumns (fun u -> u.xmin)
            }
        )
    )

    Assert.Contains(
        "\"u\".\"xmax\"",
        sqlOf (
            select {
                for u in usersTable do
                    select u
                    withSystemColumns (fun u -> u.xmax)
            }
        )
    )

    Assert.Contains(
        "\"u\".\"cmin\"",
        sqlOf (
            select {
                for u in usersTable do
                    select u
                    withSystemColumns (fun u -> u.cmin)
            }
        )
    )

    Assert.Contains(
        "\"u\".\"cmax\"",
        sqlOf (
            select {
                for u in usersTable do
                    select u
                    withSystemColumns (fun u -> u.cmax)
            }
        )
    )

    Assert.Contains(
        "\"u\".\"ctid\"",
        sqlOf (
            select {
                for u in usersTable do
                    select u
                    withSystemColumns (fun u -> u.ctid)
            }
        )
    )

    Assert.Contains(
        "\"u\".\"tableoid\"",
        sqlOf (
            select {
                for u in usersTable do
                    select u
                    withSystemColumns (fun u -> u.tableoid)
            }
        )
    )

[<Fact>]
let ``chaining projects every column named, and only those`` () =
    let sql =
        sqlOf (
            select {
                for u in usersTable do
                    select u
                    withSystemColumns (fun u -> u.xmin)
                    withSystemColumns (fun u -> u.ctid)
            }
        )

    Assert.Contains("\"u\".\"xmin\"", sql)
    Assert.Contains("\"u\".\"ctid\"", sql)
    // The control: a column that was NOT named stays absent, so chaining adds rather
    // than opening the floodgates.
    Assert.DoesNotContain("tableoid", sql)

[<Fact>]
let ``a joined read projects the system column of the table it was selected from`` () =
    // THE REGRESSION THIS SHAPE EXISTS FOR. `select` is the one operation that changes
    // the builder's row type while keeping the CE's variable space, so in a join the two
    // disagree: after `select u` the row is `users`, but the variable space is still the
    // tuple `(u, s)`. While the column selector was an `[<ProjectionParameter>]` it was
    // elaborated against the variable space and every joined read failed to COMPILE —
    // "expected 'users' but is a tuple of type ''a * 'b'". A plain lambda argument is
    // elaborated against its own parameter type, which is the selected row in both
    // shapes. A compile error cannot be asserted, so this test existing and compiling
    // IS the assertion; the SQL below pins that the right table grew.
    let sql =
        sqlOf (
            select {
                for u in usersTable do
                    join s in userSettingsTable on (u.id = s.user_id)
                    where (s.auto_brief_enabled = true)
                    select u
                    withSystemColumns (fun u -> u.xmin)
            }
        )

    Assert.Contains("\"u\".*, \"u\".\"xmin\"", sql)
    // The joined table is not handed a column that belongs to the other one.
    Assert.DoesNotContain("\"s\".\"xmin\"", sql)

[<Fact>]
let ``an expression that is not a column is refused`` () =
    // The selector's type permits any expression over the row, so this compiles. It is
    // refused at query construction rather than emitting nonsense SQL. The refusal comes
    // from SqlHydra's own selector reader, which raises rather than returning None, so
    // this pins "loud failure", not a specific exception type.
    let build () =
        select {
            for u in usersTable do
                select u
                withSystemColumns (fun u -> u.xmin + 1u)
        }
        |> ignore

    Assert.ThrowsAny<Exception>(build) |> ignore
