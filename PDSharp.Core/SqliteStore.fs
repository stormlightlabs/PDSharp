namespace PDSharp.Core

open System
open System.IO
open Microsoft.Data.Sqlite
open Dapper
open PDSharp.Core.BlockStore
open PDSharp.Core.Auth
open System.Threading.Tasks
open PDSharp.Core.Config

/// SQLite persistence layer
module SqliteStore =

  /// Initialize the database schema
  let initialize (connectionString : string) =
    use conn = new SqliteConnection(connectionString)
    conn.Open()

    conn.Execute("PRAGMA journal_mode=WAL;") |> ignore
    // TODO: fast, slightly less safe. Keep default (FULL) for now.
    // conn.Execute("PRAGMA synchronous=NORMAL;") |> ignore

    conn.Execute(
      """
      CREATE TABLE IF NOT EXISTS blocks (
        cid TEXT PRIMARY KEY,
        data BLOB NOT NULL
      );
    """
    )
    |> ignore


    conn.Execute(
      """
      CREATE TABLE IF NOT EXISTS accounts (
        did TEXT PRIMARY KEY,
        handle TEXT NOT NULL UNIQUE,
        password_hash TEXT NOT NULL,
        email TEXT,
        created_at TEXT NOT NULL
      );
    """
    )
    |> ignore

    conn.Execute(
      """
      CREATE TABLE IF NOT EXISTS repos (
        did TEXT PRIMARY KEY,
        rev TEXT NOT NULL,
        mst_root_cid TEXT NOT NULL,
        head_cid TEXT,
        collections_json TEXT -- Just store serialized collection map for now
      );
    """
    )
    |> ignore

    conn.Execute(
      """
        CREATE TABLE IF NOT EXISTS signing_keys (
            did TEXT PRIMARY KEY,
            k TEXT NOT NULL -- Hex encoded private key D
        );
    """
    )
    |> ignore

  /// DTOs for Sqlite Mapping
  type RepoRow = {
    did : string
    rev : string
    mst_root_cid : string
    head_cid : string
    collections_json : string
  }

  type BlockRow = { cid : string; data : byte[] }

  type IRepoStore =
    abstract member GetRepo : string -> Async<RepoRow option>
    abstract member SaveRepo : RepoRow -> Async<unit>

  type SqliteBlockStore(connectionString : string) =
    interface IBlockStore with
      member _.Put(data : byte[]) = async {
        let hash = Crypto.sha256 data
        let cid = Cid.FromHash hash
        let cidStr = cid.ToString()

        use conn = new SqliteConnection(connectionString)

        let! _ =
          conn.ExecuteAsync(
            "INSERT OR IGNORE INTO blocks (cid, data) VALUES (@cid, @data)",
            {| cid = cidStr; data = data |}
          )
          |> Async.AwaitTask

        return cid
      }

      member _.Get(cid : Cid) = async {
        use conn = new SqliteConnection(connectionString)

        let! result =
          conn.QuerySingleOrDefaultAsync<byte[]>("SELECT data FROM blocks WHERE cid = @cid", {| cid = cid.ToString() |})
          |> Async.AwaitTask

        if isNull result then return None else return Some result
      }

      member _.Has(cid : Cid) = async {
        use conn = new SqliteConnection(connectionString)

        let! count =
          conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM blocks WHERE cid = @cid", {| cid = cid.ToString() |})
          |> Async.AwaitTask

        return count > 0
      }

      member _.GetAllCidsAndData() = async {
        use conn = new SqliteConnection(connectionString)
        let! rows = conn.QueryAsync<BlockRow>("SELECT cid, data FROM blocks") |> Async.AwaitTask

        return
          rows
          |> Seq.map (fun r -> (r.cid, r.data))
          |> Seq.choose (fun (cidStr, data) ->
            match Cid.TryParse cidStr with
            | Some c -> Some(c, data)
            | None -> None)
          |> Seq.toList
      }

  type SqliteAccountStore(connectionString : string) =
    interface IAccountStore with
      member _.CreateAccount(account : Account) = async {
        use conn = new SqliteConnection(connectionString)

        try
          let! _ =
            conn.ExecuteAsync(
              """
                INSERT INTO accounts (did, handle, password_hash, email, created_at)
                VALUES (@Did, @Handle, @PasswordHash, @Email, @CreatedAt)
            """,
              account
            )
            |> Async.AwaitTask

          return Ok()
        with
        | :? SqliteException as ex when ex.SqliteErrorCode = 19 -> // Constraint violation
          return Error "Account already exists (handle or DID taken)"
        | ex -> return Error ex.Message
      }

      member _.GetAccountByHandle(handle : string) = async {
        use conn = new SqliteConnection(connectionString)

        let! result =
          conn.QuerySingleOrDefaultAsync<Account>(
            "SELECT * FROM accounts WHERE handle = @handle",
            {| handle = handle |}
          )
          |> Async.AwaitTask

        if isNull (box result) then
          return None
        else
          return Some result
      }

      member _.GetAccountByDid(did : string) = async {
        use conn = new SqliteConnection(connectionString)

        let! result =
          conn.QuerySingleOrDefaultAsync<Account>("SELECT * FROM accounts WHERE did = @did", {| did = did |})
          |> Async.AwaitTask

        if isNull (box result) then
          return None
        else
          return Some result
      }

  type SqliteRepoStore(connectionString : string) =
    interface IRepoStore with
      member _.GetRepo(did : string) : Async<RepoRow option> = async {
        use conn = new SqliteConnection(connectionString)

        let! result =
          conn.QuerySingleOrDefaultAsync<RepoRow>("SELECT * FROM repos WHERE did = @did", {| did = did |})
          |> Async.AwaitTask

        if isNull (box result) then
          return None
        else
          return Some result
      }

      member _.SaveRepo(repo : RepoRow) : Async<unit> = async {
        use conn = new SqliteConnection(connectionString)

        let! _ =
          conn.ExecuteAsync(
            """
                INSERT INTO repos (did, rev, mst_root_cid, head_cid, collections_json)
                VALUES (@did, @rev, @mst_root_cid, @head_cid, @collections_json)
                ON CONFLICT(did) DO UPDATE SET
                    rev = @rev,
                    mst_root_cid = @mst_root_cid,
                    head_cid = @head_cid,
                    collections_json = @collections_json
            """,
            repo
          )
          |> Async.AwaitTask

        ()
      }
