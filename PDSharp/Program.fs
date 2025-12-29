open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open Giraffe
open PDSharp.Core.Models
open PDSharp.Core.Config

module App =

  let describeServerHandler : HttpHandler =
    fun next ctx ->
      let config = ctx.GetService<AppConfig>()

      // TODO: add to config
      let response = {
        availableUserDomains = []
        did = config.DidHost
        inviteCodeRequired = true
      }

      json response next ctx

  let webApp =
    choose [
      route "/xrpc/com.atproto.server.describeServer" >=> describeServerHandler
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
