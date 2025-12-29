open System
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
open PDSharp.Core.Firehose
open PDSharp.Core.Auth

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

  // Firehose subscriber management
  open System.Net.WebSockets
  open System.Collections.Concurrent

  /// Connected WebSocket subscribers
  let subscribers = ConcurrentDictionary<Guid, WebSocket>()

  /// Broadcast a commit event to all connected subscribers
  let broadcastEvent (event : CommitEvent) =
    let eventBytes = encodeEvent event
    let segment = ArraySegment<byte>(eventBytes)

    for kvp in subscribers do
      let ws = kvp.Value

      if ws.State = WebSocketState.Open then
        try
          ws.SendAsync(segment, WebSocketMessageType.Binary, true, Threading.CancellationToken.None)
          |> Async.AwaitTask
          |> Async.RunSynchronously
        with _ ->
          subscribers.TryRemove(kvp.Key) |> ignore

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
    return signed, commitCid
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

  [<CLIMutable>]
  type CreateAccountRequest = {
    handle : string
    email : string option
    password : string
    inviteCode : string option
  }

  [<CLIMutable>]
  type CreateSessionRequest = {
    /// Handle or email
    identifier : string
    password : string
  }

  type SessionResponse = {
    accessJwt : string
    refreshJwt : string
    handle : string
    did : string
    email : string option
  }

  /// POST /xrpc/com.atproto.server.createAccount
  let createAccountHandler : HttpHandler =
    fun next ctx -> task {
      let config = ctx.GetService<AppConfig>()
      let! body = ctx.ReadBodyFromRequestAsync()

      let request =
        JsonSerializer.Deserialize<CreateAccountRequest>(
          body,
          JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        )

      if
        String.IsNullOrWhiteSpace(request.handle)
        || String.IsNullOrWhiteSpace(request.password)
      then
        ctx.SetStatusCode 400

        return!
          json
            {
              error = "InvalidRequest"
              message = "handle and password are required"
            }
            next
            ctx
      else
        match createAccount request.handle request.password request.email with
        | Error msg ->
          ctx.SetStatusCode 400
          return! json { error = "AccountExists"; message = msg } next ctx
        | Ok account ->
          let accessJwt = createAccessToken config.JwtSecret account.Did
          let refreshJwt = createRefreshToken config.JwtSecret account.Did

          ctx.SetStatusCode 200

          return!
            json
              {
                accessJwt = accessJwt
                refreshJwt = refreshJwt
                handle = account.Handle
                did = account.Did
                email = account.Email
              }
              next
              ctx
    }

  /// POST /xrpc/com.atproto.server.createSession
  let createSessionHandler : HttpHandler =
    fun next ctx -> task {
      let config = ctx.GetService<AppConfig>()
      let! body = ctx.ReadBodyFromRequestAsync()

      let request =
        JsonSerializer.Deserialize<CreateSessionRequest>(
          body,
          JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        )

      if
        String.IsNullOrWhiteSpace(request.identifier)
        || String.IsNullOrWhiteSpace(request.password)
      then
        ctx.SetStatusCode 400

        return!
          json
            {
              error = "InvalidRequest"
              message = "identifier and password are required"
            }
            next
            ctx
      else
        match getAccountByHandle request.identifier with
        | None ->
          ctx.SetStatusCode 401

          return!
            json
              {
                error = "AuthenticationRequired"
                message = "Invalid identifier or password"
              }
              next
              ctx
        | Some account ->
          if not (verifyPassword request.password account.PasswordHash) then
            ctx.SetStatusCode 401

            return!
              json
                {
                  error = "AuthenticationRequired"
                  message = "Invalid identifier or password"
                }
                next
                ctx
          else
            let accessJwt = createAccessToken config.JwtSecret account.Did
            let refreshJwt = createRefreshToken config.JwtSecret account.Did

            ctx.SetStatusCode 200

            return!
              json
                {
                  accessJwt = accessJwt
                  refreshJwt = refreshJwt
                  handle = account.Handle
                  did = account.Did
                  email = account.Email
                }
                next
                ctx
    }

  /// Extract Bearer token from Authorization header
  let private extractBearerToken (ctx : HttpContext) : string option =
    match ctx.Request.Headers.TryGetValue("Authorization") with
    | true, values ->
      let header = values.ToString()

      if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
        Some(header.Substring(7))
      else
        None
    | _ -> None

  /// POST /xrpc/com.atproto.server.refreshSession
  let refreshSessionHandler : HttpHandler =
    fun next ctx -> task {
      let config = ctx.GetService<AppConfig>()

      match extractBearerToken ctx with
      | None ->
        ctx.SetStatusCode 401

        return!
          json
            {
              error = "AuthenticationRequired"
              message = "Missing or invalid Authorization header"
            }
            next
            ctx
      | Some token ->
        match validateToken config.JwtSecret token with
        | Invalid reason ->
          ctx.SetStatusCode 401
          return! json { error = "ExpiredToken"; message = reason } next ctx
        | Valid(did, tokenType, _) ->
          if tokenType <> Refresh then
            ctx.SetStatusCode 400

            return!
              json
                {
                  error = "InvalidRequest"
                  message = "Refresh token required"
                }
                next
                ctx
          else
            match getAccountByDid did with
            | None ->
              ctx.SetStatusCode 401
              return! json { error = "AccountNotFound"; message = "Account not found" } next ctx
            | Some account ->
              let accessJwt = createAccessToken config.JwtSecret account.Did
              let refreshJwt = createRefreshToken config.JwtSecret account.Did

              ctx.SetStatusCode 200

              return!
                json
                  {
                    accessJwt = accessJwt
                    refreshJwt = refreshJwt
                    handle = account.Handle
                    did = account.Did
                    email = account.Email
                  }
                  next
                  ctx
    }

  let createRecordHandler : HttpHandler =
    fun next ctx -> task {
      let! body = ctx.ReadBodyFromRequestAsync()

      let request =
        JsonSerializer.Deserialize<CreateRecordRequest>(body, JsonSerializerOptions(PropertyNameCaseInsensitive = true))

      match Lexicon.validate request.collection request.record with
      | Lexicon.Error msg ->
        ctx.SetStatusCode 400
        return! json { error = "InvalidRequest"; message = msg } next ctx
      | Lexicon.Ok ->
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

        let! allBlocks = (blockStore :> IBlockStore).GetAllCidsAndData()
        let carBytes = Car.createCar [ commitCid ] allBlocks
        let event = createCommitEvent did newRev commitCid carBytes
        broadcastEvent event

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

      match Lexicon.validate request.collection request.record with
      | Lexicon.Error msg ->
        ctx.SetStatusCode 400
        return! json { error = "InvalidRequest"; message = msg } next ctx
      | Lexicon.Ok ->
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

          let! allBlocks = (blockStore :> IBlockStore).GetAllCidsAndData()
          let carBytes = Car.createCar [ commitCid ] allBlocks
          let event = createCommitEvent did newRev commitCid carBytes
          broadcastEvent event

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

  /// sync.getRepo: Export entire repository as CAR file
  let getRepoHandler : HttpHandler =
    fun next ctx -> task {
      let did = ctx.Request.Query.["did"].ToString()

      if String.IsNullOrWhiteSpace(did) then
        ctx.SetStatusCode 400

        return!
          json
            {
              error = "InvalidRequest"
              message = "Missing required query parameter: did"
            }
            next
            ctx
      else
        match Map.tryFind did repos with
        | None ->
          ctx.SetStatusCode 404

          return!
            json
              {
                error = "RepoNotFound"
                message = $"Repository not found: {did}"
              }
              next
              ctx
        | Some repoData ->
          match repoData.Head with
          | None ->
            ctx.SetStatusCode 404

            return!
              json
                {
                  error = "RepoNotFound"
                  message = "Repository has no commits"
                }
                next
                ctx
          | Some headCid ->
            let! allBlocks = (blockStore :> IBlockStore).GetAllCidsAndData()
            let carBytes = Car.createCar [ headCid ] allBlocks
            ctx.SetContentType "application/vnd.ipld.car"
            ctx.SetStatusCode 200
            return! ctx.WriteBytesAsync carBytes
    }

  /// sync.getBlocks: Fetch specific blocks by CID
  let getBlocksHandler : HttpHandler =
    fun next ctx -> task {
      let did = ctx.Request.Query.["did"].ToString()
      let cidsParam = ctx.Request.Query.["cids"].ToString()

      if String.IsNullOrWhiteSpace did || String.IsNullOrWhiteSpace cidsParam then
        ctx.SetStatusCode 400

        return!
          json
            {
              error = "InvalidRequest"
              message = "Missing required query parameters: did, cids"
            }
            next
            ctx
      else
        match Map.tryFind did repos with
        | None ->
          ctx.SetStatusCode 404

          return!
            json
              {
                error = "RepoNotFound"
                message = $"Repository not found: {did}"
              }
              next
              ctx
        | Some _ ->
          let cidStrs = cidsParam.Split(',') |> Array.map (fun s -> s.Trim())
          let parsedCids = cidStrs |> Array.choose Cid.TryParse |> Array.toList
          let! allBlocks = (blockStore :> IBlockStore).GetAllCidsAndData()

          let filteredBlocks =
            if parsedCids.IsEmpty then
              allBlocks
            else
              allBlocks
              |> List.filter (fun (c, _) -> parsedCids |> List.exists (fun pc -> pc.Bytes = c.Bytes))

          let roots =
            if filteredBlocks.Length > 0 then
              [ fst filteredBlocks.[0] ]
            else
              []

          let carBytes = Car.createCar roots filteredBlocks
          ctx.SetContentType "application/vnd.ipld.car"
          ctx.SetStatusCode 200
          return! ctx.WriteBytesAsync carBytes
    }

  /// sync.getBlob: Fetch a blob by CID
  let getBlobHandler : HttpHandler =
    fun next ctx -> task {
      let did = ctx.Request.Query.["did"].ToString()
      let cidStr = ctx.Request.Query.["cid"].ToString()

      if String.IsNullOrWhiteSpace(did) || String.IsNullOrWhiteSpace(cidStr) then
        ctx.SetStatusCode 400

        return!
          json
            {
              error = "InvalidRequest"
              message = "Missing required query parameters: did, cid"
            }
            next
            ctx
      else
        match Map.tryFind did repos with
        | None ->
          ctx.SetStatusCode 404

          return!
            json
              {
                error = "RepoNotFound"
                message = $"Repository not found: {did}"
              }
              next
              ctx
        | Some _ ->
          match Cid.TryParse cidStr with
          | None ->
            ctx.SetStatusCode 400

            return!
              json
                {
                  error = "InvalidRequest"
                  message = $"Invalid CID format: {cidStr}"
                }
                next
                ctx
          | Some cid ->
            let! dataOpt = (blockStore :> IBlockStore).Get(cid)

            match dataOpt with
            | None ->
              ctx.SetStatusCode 404

              return!
                json
                  {
                    error = "BlobNotFound"
                    message = $"Blob not found: {cidStr}"
                  }
                  next
                  ctx
            | Some data ->
              ctx.SetContentType "application/octet-stream"
              ctx.SetStatusCode 200
              return! ctx.WriteBytesAsync data
    }

  /// subscribeRepos: WebSocket firehose endpoint
  let subscribeReposHandler : HttpHandler =
    fun next ctx -> task {
      if ctx.WebSockets.IsWebSocketRequest then
        let cursor =
          match ctx.Request.Query.TryGetValue("cursor") with
          | true, v when not (String.IsNullOrWhiteSpace(v.ToString())) ->
            Int64.TryParse(v.ToString())
            |> function
              | true, n -> Some n
              | _ -> None
          | _ -> None

        let! webSocket = ctx.WebSockets.AcceptWebSocketAsync()
        let id = Guid.NewGuid()
        subscribers.TryAdd(id, webSocket) |> ignore

        let buffer = Array.zeroCreate<byte> 1024

        try
          let mutable loop = true

          while loop && webSocket.State = WebSocketState.Open do
            let! result = webSocket.ReceiveAsync(ArraySegment(buffer), Threading.CancellationToken.None)

            if result.MessageType = WebSocketMessageType.Close then
              loop <- false
        finally
          subscribers.TryRemove(id) |> ignore

          if webSocket.State = WebSocketState.Open then
            webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", Threading.CancellationToken.None)
            |> Async.AwaitTask
            |> Async.RunSynchronously

        return Some ctx
      else
        ctx.SetStatusCode 400

        return!
          json
            {
              error = "InvalidRequest"
              message = "WebSocket upgrade required"
            }
            next
            ctx
    }

  let indexHandler : HttpHandler =
    fun next ctx ->
      let html =
        """<html>
      <head><title>PDSharp</title></head>
      <body>
        <pre>
         888                             888                         8888888888   888  888
         888                             888                         888          888  888
         888                             888                         888        888888888888
 8888b.  888888 88888b.  888d888 .d88b.  888888 .d88b.       88      8888888      888  888
    "88b 888    888 "88b 888P"  d88""88b 888   d88""88b    888888    888          888  888
.d888888 888    888  888 888    888  888 888   888  888      88      888        888888888888
888  888 Y88b.  888 d88P 888    Y88..88P Y88b. Y88..88P              888          888  888
"Y888888  "Y888 88888P"  888     "Y88P"   "Y888 "Y88P"               888          888  888
                888
                888
                888


This is an AT Protocol Personal Data Server (aka, an atproto PDS)

Most API routes are under /xrpc/

      Code: https://github.com/bluesky-social/atproto
            https://github.com/stormlightlabs/PDSharp
            https://tangled.org/desertthunder.dev/PDSharp
 Self-Host: https://github.com/bluesky-social/pds
  Protocol: https://atproto.com
        </pre>
      </body>
    </html>"""

      ctx.SetContentType "text/html"
      ctx.SetStatusCode 200
      ctx.WriteStringAsync html

  let webApp =
    choose [
      GET
      >=> choose [
        route "/" >=> indexHandler
        route "/xrpc/com.atproto.server.describeServer" >=> describeServerHandler
      ]
      POST >=> route "/xrpc/com.atproto.server.createAccount" >=> createAccountHandler
      POST >=> route "/xrpc/com.atproto.server.createSession" >=> createSessionHandler
      POST
      >=> route "/xrpc/com.atproto.server.refreshSession"
      >=> refreshSessionHandler
      POST >=> route "/xrpc/com.atproto.repo.createRecord" >=> createRecordHandler
      GET >=> route "/xrpc/com.atproto.repo.getRecord" >=> getRecordHandler
      POST >=> route "/xrpc/com.atproto.repo.putRecord" >=> putRecordHandler
      GET >=> route "/xrpc/com.atproto.sync.getRepo" >=> getRepoHandler
      GET >=> route "/xrpc/com.atproto.sync.getBlocks" >=> getBlocksHandler
      GET >=> route "/xrpc/com.atproto.sync.getBlob" >=> getBlobHandler
      GET >=> route "/xrpc/com.atproto.sync.subscribeRepos" >=> subscribeReposHandler
      route "/" >=> text "PDSharp PDS is running."
      RequestErrors.NOT_FOUND "Not Found"
    ]

  let configureApp (app : IApplicationBuilder) =
    app.UseWebSockets() |> ignore
    app.UseGiraffe webApp

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
