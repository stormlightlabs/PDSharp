module Handlers.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Collections.Generic
open Xunit
open Microsoft.AspNetCore.Http
open Giraffe
open PDSharp.Core.Config
open PDSharp.Core.BlockStore
open PDSharp.Core
open PDSharp.Core.SqliteStore
open PDSharp.Core.Auth

type MockAccountStore() =
  let mutable accounts = Map.empty<string, Account>

  interface IAccountStore with
    member _.CreateAccount(account) = async {
      if accounts.ContainsKey account.Did then
        return Error "Exists"
      else
        accounts <- accounts.Add(account.Did, account)
        return Ok()
    }

    member _.GetAccountByHandle(handle) = async {
      return accounts |> Map.tryPick (fun _ v -> if v.Handle = handle then Some v else None)
    }

    member _.GetAccountByDid did = async { return accounts.TryFind did }

type MockBlockStore() =
  let mutable blocks = Map.empty<string, byte[]>

  interface IBlockStore with
    member _.Put(data) = async {
      let hash = Crypto.sha256 data
      let cid = Cid.FromHash hash
      blocks <- blocks.Add(cid.ToString(), data)
      return cid
    }

    member _.Get cid = async { return blocks.TryFind(cid.ToString()) }
    member _.Has cid = async { return blocks.ContainsKey(cid.ToString()) }

    member _.GetAllCidsAndData() = async {
      return
        blocks
        |> Map.toList
        |> List.choose (fun (k, v) -> Cid.TryParse k |> Option.map (fun c -> (c, v)))
    }

type MockRepoStore() =
  let mutable repos = Map.empty<string, RepoRow>

  interface IRepoStore with
    member _.GetRepo(did) = async { return repos.TryFind did }
    member _.SaveRepo(repo) = async { repos <- repos.Add(repo.did, repo) }

type MockJsonSerializer() =
  interface Giraffe.Json.ISerializer with
    member _.SerializeToString x = JsonSerializer.Serialize x
    member _.SerializeToBytes x = JsonSerializer.SerializeToUtf8Bytes x
    member _.Deserialize<'T>(json : string) = JsonSerializer.Deserialize<'T> json

    member _.Deserialize<'T>(bytes : byte[]) =
      JsonSerializer.Deserialize<'T>(ReadOnlySpan bytes)

    member _.DeserializeAsync<'T>(stream : Stream) = task { return! JsonSerializer.DeserializeAsync<'T>(stream) }

    member _.SerializeToStreamAsync<'T> (x : 'T) (stream : Stream) = task {
      do! JsonSerializer.SerializeAsync<'T>(stream, x)
    }

let mockContext (services : (Type * obj) list) (body : string) (query : Map<string, string>) =
  let ctx = new DefaultHttpContext()
  let serializer = MockJsonSerializer()
  let allServices = (typeof<Giraffe.Json.ISerializer>, box serializer) :: services

  let sp =
    { new IServiceProvider with
        member _.GetService(serviceType) =
          allServices
          |> List.tryPick (fun (t, s) -> if t = serviceType then Some s else None)
          |> Option.toObj
    }

  ctx.RequestServices <- sp

  if not (String.IsNullOrEmpty body) then
    let stream = new MemoryStream(Encoding.UTF8.GetBytes(body))
    ctx.Request.Body <- stream
    ctx.Request.ContentLength <- stream.Length

  if not query.IsEmpty then
    let dict = Dictionary<string, Microsoft.Extensions.Primitives.StringValues>()

    for kvp in query do
      dict.Add(kvp.Key, Microsoft.Extensions.Primitives.StringValues(kvp.Value))

    ctx.Request.Query <- QueryCollection dict

  ctx

[<Fact>]
let ``Auth.createAccountHandler creates account successfully`` () = task {
  let accountStore = MockAccountStore()

  let config = {
    PublicUrl = "https://pds.example.com"
    DidHost = "did:web:pds.example.com"
    JwtSecret = "secret"
    SqliteConnectionString = ""
    DisableWalAutoCheckpoint = false
    BlobStore = Disk "blobs"
  }

  let services = [ typeof<AppConfig>, box config; typeof<IAccountStore>, box accountStore ]

  let req : PDSharp.Handlers.Auth.CreateAccountRequest = {
    handle = "alice.test"
    email = Some "alice@test.com"
    password = "password123"
    inviteCode = None
  }

  let body = JsonSerializer.Serialize req
  let ctx = mockContext services body Map.empty
  let next : HttpFunc = fun _ -> Task.FromResult(None)
  let! result = PDSharp.Handlers.Auth.createAccountHandler next ctx
  Assert.Equal(200, ctx.Response.StatusCode)

  let store = accountStore :> IAccountStore
  let! accountOpt = store.GetAccountByHandle "alice.test"
  Assert.True accountOpt.IsSome
}

[<Fact>]
let ``Server.indexHandler returns HTML`` () = task {
  let ctx = new DefaultHttpContext()
  let next : HttpFunc = fun _ -> Task.FromResult(None)
  let! result = PDSharp.Handlers.Server.indexHandler next ctx
  Assert.Equal(200, ctx.Response.StatusCode)
  Assert.Equal("text/html", ctx.Response.ContentType)
}

[<Fact>]
let ``Repo.createRecordHandler invalid collection returns error`` () = task {
  let blockStore = MockBlockStore()
  let repoStore = MockRepoStore()
  let keyStore = PDSharp.Handlers.SigningKeyStore()
  let firehose = PDSharp.Handlers.FirehoseState()

  let services = [
    typeof<IBlockStore>, box blockStore
    typeof<IRepoStore>, box repoStore
    typeof<PDSharp.Handlers.SigningKeyStore>, box keyStore
    typeof<PDSharp.Handlers.FirehoseState>, box firehose
  ]

  let record = JsonSerializer.Deserialize<JsonElement> "{\"text\":\"hello\"}"

  let req : PDSharp.Handlers.Repo.CreateRecordRequest = {
    repo = "did:web:alice.test"
    collection = "app.bsky.feed.post"
    record = record
    rkey = None
  }

  let body = JsonSerializer.Serialize(req)
  let ctx = mockContext services body Map.empty
  let next : HttpFunc = fun _ -> Task.FromResult(None)
  let! result = PDSharp.Handlers.Repo.createRecordHandler next ctx
  Assert.Equal(400, ctx.Response.StatusCode)
}
