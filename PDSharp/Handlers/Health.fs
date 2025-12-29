namespace PDSharp.Handlers

open System.Text.Json
open Microsoft.AspNetCore.Http
open Giraffe
open PDSharp.Core.Health
open PDSharp.Core.Config

module HealthHandler =
  /// PDS version (could be read from assembly info)
  let private version = "0.1.0"

  /// JSON serialization options with camelCase naming
  let private jsonOptions =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

  /// Health check handler for /xrpc/_health endpoint
  let healthHandler : HttpHandler =
    fun next ctx -> task {
      let config = ctx.GetService<AppConfig>()
      let healthState = ctx.GetService<HealthState>()
      let status = buildHealthStatus version healthState config.SqliteConnectionString "." // Check disk of current working directory

      if status.DatabaseStatus.IsHealthy then
        ctx.SetStatusCode 200
      else
        ctx.SetStatusCode 503

      let json = JsonSerializer.Serialize(status, jsonOptions)
      ctx.SetContentType "application/json"
      return! text json next ctx
    }
