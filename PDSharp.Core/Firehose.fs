namespace PDSharp.Core

open System
open System.Threading

/// Event stream (firehose) for com.atproto.sync.subscribeRepos
module Firehose =

  /// Commit event sent to subscribers
  type CommitEvent = {
    Seq : int64
    Did : string
    Rev : string
    Commit : Cid
    Blocks : byte[]
    Time : DateTimeOffset
  }

  /// Mutable sequence counter for firehose events
  let private seqCounter = ref 0L

  /// Get the next sequence number (thread-safe, monotonic)
  let nextSeq () : int64 = Interlocked.Increment(seqCounter)

  /// Get current sequence without incrementing (for cursor resume)
  let currentSeq () : int64 = seqCounter.Value

  /// Create a commit event for a repository write
  let createCommitEvent (did : string) (rev : string) (commitCid : Cid) (carBytes : byte[]) : CommitEvent = {
    Seq = nextSeq ()
    Did = did
    Rev = rev
    Commit = commitCid
    Blocks = carBytes
    Time = DateTimeOffset.UtcNow
  }

  /// Encode a commit event to DAG-CBOR bytes for WebSocket transmission
  /// Format follows AT Protocol #commit message structure
  let encodeEvent (event : CommitEvent) : byte[] =
    let eventMap : Map<string, obj> =
      Map.ofList [
        "$type", box "com.atproto.sync.subscribeRepos#commit"
        "seq", box event.Seq
        "did", box event.Did
        "rev", box event.Rev
        "commit", box event.Commit
        "blocks", box event.Blocks
        "time", box (event.Time.ToString("o"))
      ]

    DagCbor.encode eventMap

  /// Reset sequence counter (for testing)
  let resetSeq () =
    Interlocked.Exchange(seqCounter, 0L) |> ignore
