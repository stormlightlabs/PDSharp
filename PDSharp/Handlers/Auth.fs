namespace PDSharp.Handlers

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Giraffe
open PDSharp.Core.Config
open PDSharp.Core.Auth
open PDSharp.Handlers

module Auth =
  [<CLIMutable>]
  type CreateAccountRequest = {
    handle : string
    email : string option
    password : string
    inviteCode : string option
  }

  [<CLIMutable>]
  type CreateSessionRequest = { identifier : string; password : string }

  type SessionResponse = {
    accessJwt : string
    refreshJwt : string
    handle : string
    did : string
    email : string option
  }

  let private extractBearerToken (ctx : HttpContext) : string option =
    match ctx.Request.Headers.TryGetValue("Authorization") with
    | true, values ->
      let header = values.ToString()

      if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
        Some(header.Substring(7))
      else
        None
    | _ -> None

  let createAccountHandler : HttpHandler =
    fun next ctx -> task {
      let config = ctx.GetService<AppConfig>()
      let accountStore = ctx.GetService<IAccountStore>()
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
        match! PDSharp.Core.Auth.createAccount accountStore request.handle request.password request.email with
        | Result.Error msg ->
          ctx.SetStatusCode 400
          return! json { error = "AccountExists"; message = msg } next ctx
        | Result.Ok(account : Account) ->
          let accessJwt = PDSharp.Core.Auth.createAccessToken config.JwtSecret account.Did
          let refreshJwt = PDSharp.Core.Auth.createRefreshToken config.JwtSecret account.Did
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

  let createSessionHandler : HttpHandler =
    fun next ctx -> task {
      let config = ctx.GetService<AppConfig>()
      let accountStore = ctx.GetService<IAccountStore>()
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
        let! accountOpt =
          if request.identifier.StartsWith("did:") then
            accountStore.GetAccountByDid request.identifier
          else
            accountStore.GetAccountByHandle request.identifier

        match accountOpt with
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
        | Some(account : Account) ->
          if not (PDSharp.Core.Auth.verifyPassword request.password account.PasswordHash) then
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
            let accessJwt = PDSharp.Core.Auth.createAccessToken config.JwtSecret account.Did
            let refreshJwt = PDSharp.Core.Auth.createRefreshToken config.JwtSecret account.Did
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

  let refreshSessionHandler : HttpHandler =
    fun next ctx -> task {
      let config = ctx.GetService<AppConfig>()
      let accountStore = ctx.GetService<IAccountStore>()

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
        match PDSharp.Core.Auth.validateToken config.JwtSecret token with
        | PDSharp.Core.Auth.Invalid reason ->
          ctx.SetStatusCode 401
          return! json { error = "ExpiredToken"; message = reason } next ctx
        | PDSharp.Core.Auth.Valid(did, tokenType, _) ->
          if tokenType <> PDSharp.Core.Auth.Refresh then
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
            match! accountStore.GetAccountByDid did with
            | None ->
              ctx.SetStatusCode 401
              return! json { error = "AccountNotFound"; message = "Account not found" } next ctx
            | Some account ->
              let accessJwt = PDSharp.Core.Auth.createAccessToken config.JwtSecret account.Did
              let refreshJwt = PDSharp.Core.Auth.createRefreshToken config.JwtSecret account.Did
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
