/// Real PostgreSQL, via Testcontainers. The unit tests assert the SQL this package
/// emits; these assert PostgreSQL accepts it and returns what the SQL claims — the
/// difference between "the string looks right" and "the round trip works".
module SqlHydra.Query.PgSystemColumns.Tests.IntegrationTests

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open Npgsql
open Xunit
open SqlHydra
open SqlHydra.Query
open SqlHydra.Query.PgSystemColumns.SystemColumns
open Testcontainers.PostgreSql

/// Stands in for a SqlHydra-generated table type. The compiled SQL runs through raw
/// Npgsql, so no generated reader is needed. The module name is the schema.
module ``public`` =
    [<CLIMutable>]
    type widgets =
        { [<ProviderDbType("Uuid")>]
          id: Guid
          [<ProviderDbType("Text")>]
          name: string
          [<ProviderDbType("Xid")>]
          xmin: uint32 }

    let widgets = table<widgets>

type PostgresFixture() =
    let container = PostgreSqlBuilder("postgres:17").Build()
    member val ConnectionString = "" with get, set

    interface IAsyncLifetime with
        member this.InitializeAsync() : ValueTask =
            ValueTask(
                task {
                    do! container.StartAsync()
                    this.ConnectionString <- container.GetConnectionString()
                    use conn = new NpgsqlConnection(this.ConnectionString)
                    do! conn.OpenAsync()

                    use cmd =
                        new NpgsqlCommand("create table public.widgets (id uuid primary key, name text not null)", conn)

                    let! _ = cmd.ExecuteNonQueryAsync()
                    return ()
                }
            )

        member _.DisposeAsync() : ValueTask =
            ValueTask(container.DisposeAsync().AsTask())

[<Trait("Category", "Integration")>]
type IntegrationTests(fixture: PostgresFixture) =
    let emitter = PostgresEmitter() :> ISqlEmitter

    /// SqlHydra emits positional placeholders; Npgsql needs named ones, so they are
    /// rewritten to @pN and bound in order, by value.
    let command (conn: NpgsqlConnection) (compiled: {| Sql: string; Parameters: obj seq |}) =
        let mutable i = -1

        let sql =
            Regex.Replace(
                compiled.Sql,
                @"\?",
                (fun _ ->
                    i <- i + 1
                    $"@p{i}")
            )

        let cmd = new NpgsqlCommand(sql, conn)

        compiled.Parameters
        |> Seq.iteri (fun n p ->
            let value = FSharpValue.GetTupleFields(p).[1]
            let v = value.GetType().GetProperty("Value").GetValue value
            cmd.Parameters.AddWithValue($"p{n}", v) |> ignore)

        cmd

    let compiled (q: SelectQuery) =
        let c = q.CompileWith emitter

        {| Sql = c.Sql
           Parameters = c.Parameters |> Seq.map box |}

    let seedRow (conn: NpgsqlConnection) (name: string) =
        let id = Guid.NewGuid()

        use cmd =
            new NpgsqlCommand($"insert into public.widgets (id, name) values (@i, @n)", conn)

        cmd.Parameters.AddWithValue("i", id) |> ignore
        cmd.Parameters.AddWithValue("n", name) |> ignore
        cmd.ExecuteNonQuery() |> ignore
        id

    interface IClassFixture<PostgresFixture>

    [<Fact>]
    member _.``a whole-entity read hydrates the system column``() =
        use conn = new NpgsqlConnection(fixture.ConnectionString)
        conn.Open()
        let id = seedRow conn "hydrates"

        let q =
            select {
                for w in ``public``.widgets do
                    where (w.id = id)
                    select w
                    withSystemColumns (fun w -> w.xmin)
            }

        use cmd = command conn (compiled q)
        use reader = cmd.ExecuteReader()
        Assert.True(reader.Read())
        let xmin = reader.GetFieldValue<uint32>(reader.GetOrdinal "xmin")
        // A live row's inserting transaction id is never zero, so this distinguishes
        // "the column came back" from "the field defaulted".
        Assert.NotEqual(0u, xmin)

    [<Fact>]
    member _.``without the operation the system column is not returned``() =
        // The control for the test above: the whole point is that `SELECT u.*` omits it.
        use conn = new NpgsqlConnection(fixture.ConnectionString)
        conn.Open()
        let id = seedRow conn "no-operation"

        let q =
            select {
                for w in ``public``.widgets do
                    where (w.id = id)
                    select w
            }

        use cmd = command conn (compiled q)
        use reader = cmd.ExecuteReader()
        Assert.True(reader.Read())

        Assert.Throws<IndexOutOfRangeException>(fun () -> reader.GetOrdinal "xmin" |> ignore)
        |> ignore

    [<Fact>]
    member _.``a stale version loses the write and a current one wins``() =
        use conn = new NpgsqlConnection(fixture.ConnectionString)
        conn.Open()
        let id = seedRow conn "original"

        let currentVersion () =
            use cmd = new NpgsqlCommand("select xmin from public.widgets where id = @i", conn)
            cmd.Parameters.AddWithValue("i", id) |> ignore
            cmd.ExecuteScalar() :?> uint32

        let compareAndSwap (expected: uint32) (newName: string) =
            use cmd =
                new NpgsqlCommand("update public.widgets set name = @n where id = @i and xmin = @x", conn)

            cmd.Parameters.AddWithValue("n", newName) |> ignore
            cmd.Parameters.AddWithValue("i", id) |> ignore

            cmd.Parameters.Add(NpgsqlParameter("x", NpgsqlTypes.NpgsqlDbType.Xid, Value = expected))
            |> ignore

            cmd.ExecuteNonQuery()

        let version = currentVersion ()
        // A version nobody holds: the write must match no rows rather than clobbering.
        Assert.Equal(0, compareAndSwap 1u "should-not-win")
        Assert.Equal(1, compareAndSwap version "won")

        use check = new NpgsqlCommand("select name from public.widgets where id = @i", conn)
        check.Parameters.AddWithValue("i", id) |> ignore
        Assert.Equal("won", check.ExecuteScalar() |> string)

    [<Fact>]
    member _.``PostgreSQL accepts every system column this package can project``() =
        // The unit tests assert the emitted string. This asserts the server parses and
        // runs it — a column name we got subtly wrong would pass there and fail here.
        use conn = new NpgsqlConnection(fixture.ConnectionString)
        conn.Open()
        seedRow conn "all-columns" |> ignore

        for column in [ "tableoid"; "xmin"; "cmin"; "xmax"; "cmax"; "ctid" ] do
            let sql =
                expandProjection ("w", column) [ SelectColumn.AllColumns "w" ]
                |> List.map (function
                    | SelectColumn.AllColumns a -> $"\"{a}\".*"
                    | SelectColumn.SpecificColumn c ->
                        let parts = c.Split('.')
                        $"\"{parts.[0]}\".\"{parts.[1]}\""
                    | SelectColumn.RawColumn(raw, _) -> raw)
                |> String.concat ", "
                |> fun cols -> $"select {cols} from public.widgets as \"w\" limit 1"

            use cmd = new NpgsqlCommand(sql, conn)
            use reader = cmd.ExecuteReader()
            Assert.True(reader.Read(), $"PostgreSQL rejected the projection for {column}")
            Assert.True(reader.GetOrdinal column >= 0)
