open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Giraffe
open PDSharp.Core
open PDSharp.Core.Auth
open PDSharp.Core.BlockStore
open PDSharp.Core.SqliteStore
open PDSharp.Core.BlobStore
open PDSharp.Core.Config
open PDSharp.Handlers

let getConfig () =
  let env (k : string) (def : string) =
    match Environment.GetEnvironmentVariable k with
    | null -> def
    | v -> v

  let publicUrl = env "PDSHARP_PublicUrl" "http://localhost:5000"
  let dbPath = env "PDSHARP_DbPath" "pdsharp.db"

  {
    PublicUrl = publicUrl
    DidHost = env "PDSHARP_DidHost" "did:web:localhost"
    JwtSecret = env "PDSHARP_JwtSecret" "development-secret-do-not-use-in-prod"
    SqliteConnectionString = $"Data Source={dbPath}"
    BlobStore = Disk "blobs" // Default to disk for now
  }

let config = getConfig ()

SqliteStore.initialize config.SqliteConnectionString

module App =
  let webApp =
    choose [
      GET
      >=> choose [
        route "/" >=> Server.indexHandler
        route "/xrpc/com.atproto.server.describeServer" >=> Server.describeServerHandler
      ]
      POST
      >=> route "/xrpc/com.atproto.server.createAccount"
      >=> Auth.createAccountHandler
      POST
      >=> route "/xrpc/com.atproto.server.createSession"
      >=> Auth.createSessionHandler
      POST
      >=> route "/xrpc/com.atproto.server.refreshSession"
      >=> Auth.refreshSessionHandler
      POST
      >=> route "/xrpc/com.atproto.repo.createRecord"
      >=> Repo.createRecordHandler
      GET >=> route "/xrpc/com.atproto.repo.getRecord" >=> Repo.getRecordHandler
      POST >=> route "/xrpc/com.atproto.repo.putRecord" >=> Repo.putRecordHandler
      GET >=> route "/xrpc/com.atproto.sync.getRepo" >=> Sync.getRepoHandler
      GET >=> route "/xrpc/com.atproto.sync.getBlocks" >=> Sync.getBlocksHandler
      GET >=> route "/xrpc/com.atproto.sync.getBlob" >=> Sync.getBlobHandler
      GET
      >=> route "/xrpc/com.atproto.sync.subscribeRepos"
      >=> Sync.subscribeReposHandler
      route "/" >=> text "PDSharp PDS is running."
      RequestErrors.NOT_FOUND "Not Found"
    ]

  let configureApp (app : IApplicationBuilder) =
    app.UseWebSockets() |> ignore
    app.UseGiraffe webApp

  let configureServices (config : AppConfig) (services : IServiceCollection) =
    services.AddGiraffe() |> ignore
    services.AddSingleton<AppConfig>(config) |> ignore

    let blockStore = new SqliteBlockStore(config.SqliteConnectionString)
    let accountStore = new SqliteAccountStore(config.SqliteConnectionString)
    let repoStore = new SqliteRepoStore(config.SqliteConnectionString)

    services.AddSingleton<IBlockStore>(blockStore) |> ignore
    services.AddSingleton<IAccountStore>(accountStore) |> ignore
    services.AddSingleton<IRepoStore>(repoStore) |> ignore

    let blobStore : IBlobStore =
      match config.BlobStore with
      | Disk path -> new DiskBlobStore(path) :> IBlobStore
      | S3 s3Config -> new S3BlobStore(s3Config) :> IBlobStore

    services.AddSingleton<IBlobStore>(blobStore) |> ignore
    services.AddSingleton<FirehoseState>(new FirehoseState()) |> ignore
    services.AddSingleton<SigningKeyStore>(new SigningKeyStore()) |> ignore

  [<EntryPoint>]
  let main args =
    Host
      .CreateDefaultBuilder(args)
      .ConfigureWebHostDefaults(fun webHostBuilder ->
        webHostBuilder.Configure(configureApp).ConfigureServices(configureServices config)
        |> ignore)
      .Build()
      .Run()

    0
