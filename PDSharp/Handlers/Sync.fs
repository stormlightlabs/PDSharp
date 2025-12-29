namespace PDSharp.Handlers

open System
open System.Threading.Tasks
open System.Net.WebSockets
open Microsoft.AspNetCore.Http
open Giraffe
open PDSharp.Core
open PDSharp.Core.Models
open PDSharp.Core.BlockStore
open PDSharp.Core.Repository
open PDSharp.Core.Car
open PDSharp.Core.BlobStore
open PDSharp.Core.SqliteStore
open PDSharp.Handlers

module Sync =
  let getRepoHandler : HttpHandler =
    fun next ctx -> task {
      let repoStore = ctx.GetService<IRepoStore>()
      let blockStore = ctx.GetService<IBlockStore>()
      let did = ctx.Request.Query.["did"].ToString()

      if String.IsNullOrWhiteSpace(did) then
        ctx.SetStatusCode 400
        return! json { error = "InvalidRequest"; message = "Missing did" } next ctx
      else
        let! repoOpt = Persistence.loadRepo repoStore blockStore did

        match repoOpt with
        | None ->
          ctx.SetStatusCode 404
          return! json { error = "RepoNotFound"; message = "Repository not found" } next ctx
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
            let! allBlocks = blockStore.GetAllCidsAndData()
            let carBytes = Car.createCar [ headCid ] allBlocks
            ctx.SetContentType "application/vnd.ipld.car"
            ctx.SetStatusCode 200
            return! ctx.WriteBytesAsync carBytes
    }

  let getBlocksHandler : HttpHandler =
    fun next ctx -> task {
      let repoStore = ctx.GetService<IRepoStore>()
      let blockStore = ctx.GetService<IBlockStore>()
      let did = ctx.Request.Query.["did"].ToString()
      let cidsParam = ctx.Request.Query.["cids"].ToString()

      if String.IsNullOrWhiteSpace did || String.IsNullOrWhiteSpace cidsParam then
        ctx.SetStatusCode 400
        return! json { error = "InvalidRequest"; message = "Missing parameters" } next ctx
      else
        let! repoOpt = Persistence.loadRepo repoStore blockStore did

        match repoOpt with
        | None ->
          ctx.SetStatusCode 404
          return! json { error = "RepoNotFound"; message = "Repository not found" } next ctx
        | Some _ ->
          let cidStrs = cidsParam.Split(',') |> Array.map (fun s -> s.Trim())
          let parsedCids = cidStrs |> Array.choose Cid.TryParse |> Array.toList

          let! blocks =
            parsedCids
            |> List.map (fun cid -> async {
              let! dataOpt = blockStore.Get cid
              return dataOpt |> Option.map (fun d -> (cid, d))
            })
            |> Async.Sequential

          let foundBlocks = blocks |> Array.choose id |> Array.toList
          let roots = if foundBlocks.IsEmpty then [] else [ fst foundBlocks.Head ]
          let carBytes = Car.createCar roots foundBlocks
          ctx.SetContentType "application/vnd.ipld.car"
          ctx.SetStatusCode 200
          return! ctx.WriteBytesAsync carBytes
    }

  let getBlobHandler : HttpHandler =
    fun next ctx -> task {
      let blobStore = ctx.GetService<IBlobStore>()
      let did = ctx.Request.Query.["did"].ToString()
      let cidStr = ctx.Request.Query.["cid"].ToString()

      if String.IsNullOrWhiteSpace(did) || String.IsNullOrWhiteSpace(cidStr) then
        ctx.SetStatusCode 400
        return! json { error = "InvalidRequest"; message = "Missing parameters" } next ctx
      else
        match Cid.TryParse cidStr with
        | None ->
          ctx.SetStatusCode 400
          return! json { error = "InvalidCid"; message = "Invalid CID" } next ctx
        | Some cid ->
          let! blobOpt = blobStore.Get cid

          match blobOpt with
          | Some blob ->
            ctx.SetContentType "application/octet-stream"
            ctx.SetStatusCode 200
            return! ctx.WriteBytesAsync blob
          | None ->
            ctx.SetStatusCode 404
            return! json { error = "NotFound"; message = "Blob not found" } next ctx
    }

  let subscribeReposHandler : HttpHandler =
    fun next ctx -> task {
      if ctx.WebSockets.IsWebSocketRequest then
        let firehose = ctx.GetService<FirehoseState>()
        let! ws = ctx.WebSockets.AcceptWebSocketAsync()
        let id = Guid.NewGuid().ToString()
        firehose.Subscribers.TryAdd(id, ws) |> ignore

        try
          while ws.State = WebSocketState.Open do
            do! Task.Delay 1000
        finally
          firehose.Subscribers.TryRemove(id) |> ignore

        return Some ctx
      else
        ctx.SetStatusCode 400
        return! text "WebSocket upgrade required" next ctx
    }
