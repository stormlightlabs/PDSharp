open System
open System.IO
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open Giraffe
open PDSharp.Core
open PDSharp.Core.Models
open PDSharp.Core.Config
open PDSharp.Core.BlockStore
open PDSharp.Core.Repository
open PDSharp.Core.Mst
open PDSharp.Core.Crypto

module App =
  /// Repo state per DID: MST root, collections, current rev, head commit CID
  type RepoData = {
    MstRoot : MstNode
    Collections : Map<string, Map<string, Cid>>
    Rev : string
    Head : Cid option
    Prev : Cid option
  }

  let emptyRepo = {
    MstRoot = { Left = None; Entries = [] }
    Collections = Map.empty
    Rev = ""
    Head = None
    Prev = None
  }

  let mutable repos : Map<string, RepoData> = Map.empty
  let blockStore = MemoryBlockStore()
  let mutable signingKeys : Map<string, EcKeyPair> = Map.empty

  let getOrCreateKey (did : string) =
    match Map.tryFind did signingKeys with
    | Some k -> k
    | None ->
      let k = generateKey P256
      signingKeys <- Map.add did k signingKeys
      k

  let loader (c : Cid) = async {
    let! bytesOpt = (blockStore :> IBlockStore).Get(c)

    match bytesOpt with
    | Some bytes -> return Some(Mst.deserialize bytes)
    | None -> return None
  }

  let persister (n : MstNode) = async {
    let bytes = Mst.serialize n
    return! (blockStore :> IBlockStore).Put(bytes)
  }

  let signAndStoreCommit (did : string) (mstRootCid : Cid) (rev : string) (prev : Cid option) = async {
    let key = getOrCreateKey did

    let unsigned : UnsignedCommit = {
      Did = did
      Version = 3
      Data = mstRootCid
      Rev = rev
      Prev = prev
    }

    let signed = signCommit key unsigned
    let commitBytes = serializeCommit signed
    let! commitCid = (blockStore :> IBlockStore).Put(commitBytes)
    return (signed, commitCid)
  }

  [<CLIMutable>]
  type CreateRecordRequest = {
    repo : string
    collection : string
    record : JsonElement
    rkey : string option
  }

  [<CLIMutable>]
  type CreateRecordResponse = {
    uri : string
    cid : string
    commit : {| rev : string; cid : string |}
  }

  [<CLIMutable>]
  type GetRecordResponse = { uri : string; cid : string; value : JsonElement }

  [<CLIMutable>]
  type ErrorResponse = { error : string; message : string }

  let describeServerHandler : HttpHandler =
    fun next ctx ->
      let config = ctx.GetService<AppConfig>()

      let response = {
        availableUserDomains = []
        did = config.DidHost
        inviteCodeRequired = true
      }

      json response next ctx

  let createRecordHandler : HttpHandler =
    fun next ctx -> task {
      let! body = ctx.ReadBodyFromRequestAsync()

      let request =
        JsonSerializer.Deserialize<CreateRecordRequest>(body, JsonSerializerOptions(PropertyNameCaseInsensitive = true))

      let did = request.repo

      let rkey =
        match request.rkey with
        | Some r when not (String.IsNullOrWhiteSpace(r)) -> r
        | _ -> Tid.generate ()

      let recordJson = request.record.GetRawText()
      let recordBytes = Encoding.UTF8.GetBytes(recordJson)
      let! recordCid = (blockStore :> IBlockStore).Put(recordBytes)

      let repoData = Map.tryFind did repos |> Option.defaultValue emptyRepo
      let mstKey = $"{request.collection}/{rkey}"

      let! newMstRoot = Mst.put loader persister repoData.MstRoot mstKey recordCid ""
      let! mstRootCid = persister newMstRoot

      let newRev = Tid.generate ()
      let! (_, commitCid) = signAndStoreCommit did mstRootCid newRev repoData.Head

      let collectionMap =
        Map.tryFind request.collection repoData.Collections
        |> Option.defaultValue Map.empty

      let newCollectionMap = Map.add rkey recordCid collectionMap

      let newCollections =
        Map.add request.collection newCollectionMap repoData.Collections

      let updatedRepo = {
        MstRoot = newMstRoot
        Collections = newCollections
        Rev = newRev
        Head = Some commitCid
        Prev = repoData.Head
      }

      repos <- Map.add did updatedRepo repos

      let uri = $"at://{did}/{request.collection}/{rkey}"
      ctx.SetStatusCode 200

      return!
        json
          {|
            uri = uri
            cid = recordCid.ToString()
            commit = {| rev = newRev; cid = commitCid.ToString() |}
          |}
          next
          ctx
    }

  let getRecordHandler : HttpHandler =
    fun next ctx -> task {
      let repo = ctx.Request.Query.["repo"].ToString()
      let collection = ctx.Request.Query.["collection"].ToString()
      let rkey = ctx.Request.Query.["rkey"].ToString()

      if
        String.IsNullOrWhiteSpace(repo)
        || String.IsNullOrWhiteSpace(collection)
        || String.IsNullOrWhiteSpace(rkey)
      then
        ctx.SetStatusCode 400

        return!
          json
            {
              error = "InvalidRequest"
              message = "Missing required query parameters: repo, collection, rkey"
            }
            next
            ctx
      else
        match Map.tryFind repo repos with
        | None ->
          ctx.SetStatusCode 404

          return!
            json
              {
                error = "RepoNotFound"
                message = $"Repository not found: {repo}"
              }
              next
              ctx
        | Some repoData ->
          match Map.tryFind collection repoData.Collections with
          | None ->
            ctx.SetStatusCode 404

            return!
              json
                {
                  error = "RecordNotFound"
                  message = $"Collection not found: {collection}"
                }
                next
                ctx
          | Some collectionMap ->
            match Map.tryFind rkey collectionMap with
            | None ->
              ctx.SetStatusCode 404

              return!
                json
                  {
                    error = "RecordNotFound"
                    message = $"Record not found: {rkey}"
                  }
                  next
                  ctx
            | Some recordCid ->
              let! recordBytesOpt = (blockStore :> IBlockStore).Get(recordCid)

              match recordBytesOpt with
              | None ->
                ctx.SetStatusCode 500

                return!
                  json
                    {
                      error = "InternalError"
                      message = "Block not found in store"
                    }
                    next
                    ctx
              | Some recordBytes ->
                let recordJson = Encoding.UTF8.GetString(recordBytes)
                let uri = $"at://{repo}/{collection}/{rkey}"
                let valueElement = JsonSerializer.Deserialize<JsonElement>(recordJson)
                ctx.SetStatusCode 200

                return!
                  json
                    {|
                      uri = uri
                      cid = recordCid.ToString()
                      value = valueElement
                    |}
                    next
                    ctx
    }

  let putRecordHandler : HttpHandler =
    fun next ctx -> task {
      let! body = ctx.ReadBodyFromRequestAsync()

      let request =
        JsonSerializer.Deserialize<CreateRecordRequest>(body, JsonSerializerOptions(PropertyNameCaseInsensitive = true))

      match request.rkey with
      | Some r when not (String.IsNullOrWhiteSpace r) ->
        let did = request.repo
        let recordJson = request.record.GetRawText()
        let recordBytes = Encoding.UTF8.GetBytes(recordJson)
        let! recordCid = (blockStore :> IBlockStore).Put(recordBytes)

        let repoData = Map.tryFind did repos |> Option.defaultValue emptyRepo
        let mstKey = $"{request.collection}/{r}"

        let! newMstRoot = Mst.put loader persister repoData.MstRoot mstKey recordCid ""
        let! mstRootCid = persister newMstRoot

        let newRev = Tid.generate ()
        let! (_, commitCid) = signAndStoreCommit did mstRootCid newRev repoData.Head

        let collectionMap =
          Map.tryFind request.collection repoData.Collections
          |> Option.defaultValue Map.empty

        let newCollectionMap = Map.add r recordCid collectionMap

        let newCollections =
          Map.add request.collection newCollectionMap repoData.Collections

        let updatedRepo = {
          MstRoot = newMstRoot
          Collections = newCollections
          Rev = newRev
          Head = Some commitCid
          Prev = repoData.Head
        }

        repos <- Map.add did updatedRepo repos

        ctx.SetStatusCode 200

        return!
          json
            {|
              uri = $"at://{did}/{request.collection}/{r}"
              cid = recordCid.ToString()
              commit = {| rev = newRev; cid = commitCid.ToString() |}
            |}
            next
            ctx
      | _ ->
        ctx.SetStatusCode 400

        return!
          json
            {
              error = "InvalidRequest"
              message = "rkey is required for putRecord"
            }
            next
            ctx
    }

  let webApp =
    choose [
      GET
      >=> route "/xrpc/com.atproto.server.describeServer"
      >=> describeServerHandler
      POST >=> route "/xrpc/com.atproto.repo.createRecord" >=> createRecordHandler
      GET >=> route "/xrpc/com.atproto.repo.getRecord" >=> getRecordHandler
      POST >=> route "/xrpc/com.atproto.repo.putRecord" >=> putRecordHandler
      route "/" >=> text "PDSharp PDS is running."
      RequestErrors.NOT_FOUND "Not Found"
    ]

  let configureApp (app : IApplicationBuilder) = app.UseGiraffe webApp

  let configureServices (config : AppConfig) (services : IServiceCollection) =
    services.AddGiraffe() |> ignore
    services.AddSingleton<AppConfig>(config) |> ignore

  [<EntryPoint>]
  let main args =
    let configBuilder =
      ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional = false, reloadOnChange = true)
        .AddEnvironmentVariables(prefix = "PDSHARP_")
        .Build()

    let appConfig = configBuilder.Get<AppConfig>()

    Host
      .CreateDefaultBuilder(args)
      .ConfigureWebHostDefaults(fun webHostBuilder ->
        webHostBuilder.Configure(configureApp).ConfigureServices(configureServices appConfig)
        |> ignore)
      .Build()
      .Run()

    0
