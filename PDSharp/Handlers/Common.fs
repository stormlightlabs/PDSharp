namespace PDSharp.Handlers

open System
open System.Text.Json
open System.Collections.Concurrent
open System.Net.WebSockets
open PDSharp.Core
open PDSharp.Core.Models
open PDSharp.Core.BlockStore
open PDSharp.Core.Repository
open PDSharp.Core.Mst
open PDSharp.Core.SqliteStore
open PDSharp.Core.Crypto
open PDSharp.Core.Firehose

/// Repo state per DID: MST root, collections, current rev, head commit CID
type RepoData = {
  Did : string
  Head : Cid option
  Mst : Mst.MstNode
  Collections : Map<string, string>
}

/// Manages active Firehose WebSocket subscribers
type FirehoseState() =
  member val Subscribers = ConcurrentDictionary<string, WebSocket>() with get

/// Simple in-memory key store (TODO: Persist)
type SigningKeyStore() =
  let mutable keys : Map<string, EcKeyPair> = Map.empty
  let lockObj = obj ()

  member _.GetOrCreateKey(did : string) =
    lock lockObj (fun () ->
      match Map.tryFind did keys with
      | Some k -> k
      | None ->
        let k = Crypto.generateKey P256
        keys <- Map.add did k keys
        k)

[<CLIMutable>]
type ErrorResponse = { error : string; message : string }

module Persistence =
  let nodeLoader (blockStore : IBlockStore) (cid : Cid) = async {
    let! data = blockStore.Get cid
    return data |> Option.map Mst.deserialize
  }

  let nodePersister (blockStore : IBlockStore) (node : MstNode) = async {
    let bytes = Mst.serialize node
    return! blockStore.Put bytes
  }

  let loadRepo (repoStore : IRepoStore) (blockStore : IBlockStore) (did : string) : Async<RepoData option> = async {
    let! rowOpt = repoStore.GetRepo did

    match rowOpt with
    | None -> return None
    | Some row ->
      let! mstNode = async {
        if String.IsNullOrEmpty row.mst_root_cid then
          return None
        else
          match Cid.TryParse row.mst_root_cid with
          | None -> return None
          | Some rootCid -> return! nodeLoader blockStore rootCid
      }

      let mst = mstNode |> Option.defaultValue { Left = None; Entries = [] }

      let collections =
        try
          JsonSerializer.Deserialize<Map<string, string>>(row.collections_json)
        with _ ->
          Map.empty

      let head =
        if String.IsNullOrEmpty row.head_cid then
          None
        else
          Cid.TryParse row.head_cid

      return
        Some {
          Did = did
          Head = head
          Mst = mst
          Collections = collections
        }
  }

  let saveRepo (repoStore : IRepoStore) (blockStore : IBlockStore) (repo : RepoData) (rev : string) : Async<unit> = async {
    let! rootCid = nodePersister blockStore repo.Mst

    let row : RepoRow = {
      did = repo.Did
      rev = rev
      mst_root_cid = rootCid.ToString()
      head_cid =
        (match repo.Head with
         | Some c -> c.ToString()
         | None -> "")
      collections_json = JsonSerializer.Serialize repo.Collections
    }

    do! repoStore.SaveRepo row
  }

  let signAndStoreCommit
    (blockStore : IBlockStore)
    (keyStore : SigningKeyStore)
    (did : string)
    (mstRootCid : Cid)
    (rev : string)
    (prev : Cid option)
    =
    async {
      let key = keyStore.GetOrCreateKey did

      let unsigned : UnsignedCommit = {
        Did = did
        Version = 3
        Data = mstRootCid
        Rev = rev
        Prev = prev
      }

      let signed = signCommit key unsigned
      let commitBytes = serializeCommit signed
      let! commitCid = blockStore.Put(commitBytes)
      return signed, commitCid
    }

  let broadcastEvent (firehose : FirehoseState) (event : CommitEvent) =
    let eventBytes = Firehose.encodeEvent event
    let segment = ArraySegment<byte>(eventBytes)

    for kvp in firehose.Subscribers do
      let ws = kvp.Value

      if ws.State = WebSocketState.Open then
        try
          ws.SendAsync(segment, WebSocketMessageType.Binary, true, System.Threading.CancellationToken.None)
          |> Async.AwaitTask
          |> Async.RunSynchronously
        with _ ->
          firehose.Subscribers.TryRemove(kvp.Key) |> ignore
