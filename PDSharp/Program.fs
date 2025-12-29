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

  let disableWalAutoCheckpoint =
    env "PDSHARP_SQLITE_DISABLE_WAL_AUTO_CHECKPOINT" "false" |> bool.Parse

  let blobStoreConfig =
    match env "PDSHARP_BLOBSTORE_TYPE" "disk" with
    | "s3" ->
      S3 {
        Bucket = env "PDSHARP_S3_BUCKET" "pdsharp-blobs"
        Region = env "PDSHARP_S3_REGION" "us-east-1"
        AccessKey = Option.ofObj (Environment.GetEnvironmentVariable "PDSHARP_S3_ACCESS_KEY")
        SecretKey = Option.ofObj (Environment.GetEnvironmentVariable "PDSHARP_S3_SECRET_KEY")
        ServiceUrl = Option.ofObj (Environment.GetEnvironmentVariable "PDSHARP_S3_SERVICE_URL")
        ForcePathStyle = env "PDSHARP_S3_FORCE_PATH_STYLE" "false" |> bool.Parse
      }
    | _ -> Disk "blobs"

  {
    PublicUrl = publicUrl
    DidHost = env "PDSHARP_DidHost" "did:web:localhost"
    JwtSecret = env "PDSHARP_JwtSecret" "development-secret-do-not-use-in-prod"
    SqliteConnectionString = $"Data Source={dbPath}"
    DisableWalAutoCheckpoint = disableWalAutoCheckpoint
    BlobStore = blobStoreConfig
  }

let config = getConfig ()

SqliteStore.initialize config

module App =
  let appRouter =
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

  let webApp (app : IApplicationBuilder) =
    app.UseWebSockets() |> ignore
    app.UseGiraffe appRouter

  let configureServices (config : AppConfig) (services : IServiceCollection) =
    services.AddGiraffe() |> ignore
    services.AddSingleton<AppConfig>(config) |> ignore

    let blockStore = new SqliteBlockStore(config.SqliteConnectionString)
    let accountStore = new SqliteAccountStore(config.SqliteConnectionString)
    let repoStore = new SqliteRepoStore(config.SqliteConnectionString)

    services.AddSingleton<IBlockStore> blockStore |> ignore
    services.AddSingleton<IAccountStore> accountStore |> ignore
    services.AddSingleton<IRepoStore> repoStore |> ignore

    let blobStore : IBlobStore =
      match config.BlobStore with
      | Disk path -> new DiskBlobStore(path) :> IBlobStore
      | S3 s3Config -> new S3BlobStore(s3Config) :> IBlobStore

    services.AddSingleton<IBlobStore> blobStore |> ignore
    services.AddSingleton<FirehoseState>(new FirehoseState()) |> ignore
    services.AddSingleton<SigningKeyStore>(new SigningKeyStore()) |> ignore

  [<EntryPoint>]
  let main args =
    Host
      .CreateDefaultBuilder(args)
      .ConfigureWebHostDefaults(fun webHostBuilder ->
        webHostBuilder.Configure(webApp).ConfigureServices(configureServices config)
        |> ignore)
      .Build()
      .Run()

    0
