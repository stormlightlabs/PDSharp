namespace PDSharp.Handlers

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Giraffe
open PDSharp.Core
open PDSharp.Core.Models
open PDSharp.Core.BlockStore
open PDSharp.Core.Repository
open PDSharp.Core.Mst
open PDSharp.Core.Lexicon
open PDSharp.Core.Car
open PDSharp.Core.Firehose
open PDSharp.Core.SqliteStore
open PDSharp.Handlers

module Repo =
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

  let createRecordHandler : HttpHandler =
    fun next ctx -> task {
      let blockStore = ctx.GetService<IBlockStore>()
      let repoStore = ctx.GetService<IRepoStore>()
      let keyStore = ctx.GetService<SigningKeyStore>()
      let firehose = ctx.GetService<FirehoseState>()

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
          | Some r when not (String.IsNullOrWhiteSpace r) -> r
          | _ -> Tid.generate ()

        let recordJson = request.record.GetRawText()
        let recordBytes = Encoding.UTF8.GetBytes(recordJson)
        let! recordCid = blockStore.Put(recordBytes)

        let! existingRepoOpt = Persistence.loadRepo repoStore blockStore did

        let repoData =
          match existingRepoOpt with
          | Some r -> r
          | None -> {
              Did = did
              Head = None
              Mst = { Left = None; Entries = [] }
              Collections = Map.empty
            }

        let mstKey = $"{request.collection}/{rkey}"
        let loader = Persistence.nodeLoader blockStore
        let persister = Persistence.nodePersister blockStore

        let! newMstRoot = Mst.put loader persister repoData.Mst mstKey recordCid ""

        let newRev = Tid.generate ()
        let! mstRootCid = persister newMstRoot

        let! (_, commitCid) = Persistence.signAndStoreCommit blockStore keyStore did mstRootCid newRev repoData.Head

        let updatedRepo = {
          Did = did
          Mst = newMstRoot
          Collections = Map.empty
          Head = Some commitCid
        }

        do! Persistence.saveRepo repoStore blockStore updatedRepo newRev

        let carBytes = Car.createCar [ commitCid ] [ (recordCid, recordBytes) ]
        let event = Firehose.createCommitEvent did newRev commitCid carBytes
        Persistence.broadcastEvent firehose event

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
      let repoStore = ctx.GetService<IRepoStore>()
      let blockStore = ctx.GetService<IBlockStore>()

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
              message = "Missing required params"
            }
            next
            ctx
      else
        let! repoOpt = Persistence.loadRepo repoStore blockStore repo

        match repoOpt with
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
          let mstKey = $"{collection}/{rkey}"
          let loader = Persistence.nodeLoader blockStore
          let! cidOpt = Mst.get loader repoData.Mst mstKey ""

          match cidOpt with
          | None ->
            ctx.SetStatusCode 404
            return! json { error = "RecordNotFound"; message = "Record not found" } next ctx
          | Some recordCid ->
            let! recordBytesOpt = blockStore.Get(recordCid)

            match recordBytesOpt with
            | None ->
              ctx.SetStatusCode 500
              return! json { error = "InternalError"; message = "Block missing" } next ctx
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
      let blockStore = ctx.GetService<IBlockStore>()
      let repoStore = ctx.GetService<IRepoStore>()
      let keyStore = ctx.GetService<SigningKeyStore>()
      let firehose = ctx.GetService<FirehoseState>()

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
          let! recordCid = blockStore.Put(recordBytes)

          let! existingRepoOpt = Persistence.loadRepo repoStore blockStore did

          let repoData =
            match existingRepoOpt with
            | Some r -> r
            | None -> {
                Did = did
                Head = None
                Mst = { Left = None; Entries = [] }
                Collections = Map.empty
              }

          let mstKey = $"{request.collection}/{r}"
          let loader = Persistence.nodeLoader blockStore
          let persister = Persistence.nodePersister blockStore

          let! newMstRoot = Mst.put loader persister repoData.Mst mstKey recordCid ""
          let! mstRootCid = persister newMstRoot

          let newRev = Tid.generate ()
          let! (_, commitCid) = Persistence.signAndStoreCommit blockStore keyStore did mstRootCid newRev repoData.Head

          let updatedRepo = {
            Did = did
            Mst = newMstRoot
            Collections = Map.empty
            Head = Some commitCid
          }

          do! Persistence.saveRepo repoStore blockStore updatedRepo newRev

          let carBytes = Car.createCar [ commitCid ] [ (recordCid, recordBytes) ]
          let event = Firehose.createCommitEvent did newRev commitCid carBytes
          Persistence.broadcastEvent firehose event

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
          return! json { error = "InvalidRequest"; message = "rkey is required" } next ctx
    }
