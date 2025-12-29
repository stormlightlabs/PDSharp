namespace PDSharp.Core

open System.Collections.Concurrent

/// Block storage interface for CID → byte[] mappings
module BlockStore =

  /// Interface for content-addressed block storage
  type IBlockStore =
    abstract member Get : Cid -> Async<byte[] option>
    abstract member Put : byte[] -> Async<Cid>
    abstract member Has : Cid -> Async<bool>
    abstract member GetAllCidsAndData : unit -> Async<(Cid * byte[]) list>

  /// In-memory implementation of IBlockStore for testing
  type MemoryBlockStore() =
    let store = ConcurrentDictionary<string, (Cid * byte[])>()

    let cidKey (cid : Cid) =
      System.Convert.ToBase64String(cid.Bytes)

    interface IBlockStore with
      member _.Get(cid : Cid) = async {
        let key = cidKey cid

        match store.TryGetValue(key) with
        | true, (_, data) -> return Some data
        | false, _ -> return None
      }

      member _.Put(data : byte[]) = async {
        let hash = Crypto.sha256 data
        let cid = Cid.FromHash hash
        let key = cidKey cid
        store.[key] <- (cid, data)
        return cid
      }

      member _.Has(cid : Cid) = async {
        let key = cidKey cid
        return store.ContainsKey(key)
      }

      member _.GetAllCidsAndData() = async { return store.Values |> Seq.toList }

    /// Get the number of blocks stored (for testing)
    member _.Count = store.Count

    /// Clear all blocks (for testing)
    member _.Clear() = store.Clear()
