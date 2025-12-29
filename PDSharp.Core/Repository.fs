namespace PDSharp.Core

open System

/// Repository commit signing and management
module Repository =
  /// TID (Timestamp ID) generation for revision IDs
  module Tid =
    let private chars = "234567abcdefghijklmnopqrstuvwxyz"
    let private clockIdBits = 10
    let private timestampBits = 53

    /// Generate a random clock ID component
    let private randomClockId () =
      let rng = Random()
      rng.Next(1 <<< clockIdBits)

    /// Encode a number to base32 sortable string
    let private encode (value : int64) (length : int) =
      let mutable v = value
      let arr = Array.zeroCreate<char> length

      for i in (length - 1) .. -1 .. 0 do
        arr.[i] <- chars.[int (v &&& 0x1FL)]
        v <- v >>> 5

      String(arr)

    /// Generate a new TID based on current timestamp
    let generate () : string =
      let timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      let clockId = randomClockId ()
      let combined = (timestamp <<< clockIdBits) ||| int64 clockId
      encode combined 13

  /// Unsigned commit record (before signing)
  type UnsignedCommit = {
    Did : string
    Version : int
    Data : Cid
    Rev : string
    Prev : Cid option
  }

  /// Signed commit record
  type SignedCommit = {
    Did : string
    Version : int
    Data : Cid
    Rev : string
    Prev : Cid option
    Sig : byte[]
  }

  /// Convert unsigned commit to CBOR-encodable map
  let private unsignedToCborMap (commit : UnsignedCommit) : Map<string, obj> =
    let baseMap =
      Map.ofList [
        ("did", box commit.Did)
        ("version", box commit.Version)
        ("data", box commit.Data)
        ("rev", box commit.Rev)
      ]

    match commit.Prev with
    | Some prev -> baseMap |> Map.add "prev" (box prev)
    | None -> baseMap

  /// Sign an unsigned commit
  let signCommit (key : Crypto.EcKeyPair) (commit : UnsignedCommit) : SignedCommit =
    let cborMap = unsignedToCborMap commit
    let cborBytes = DagCbor.encode cborMap
    let hash = Crypto.sha256 cborBytes
    let signature = Crypto.sign key hash

    {
      Did = commit.Did
      Version = commit.Version
      Data = commit.Data
      Rev = commit.Rev
      Prev = commit.Prev
      Sig = signature
    }

  /// Verify a signed commit's signature
  let verifyCommit (key : Crypto.EcKeyPair) (commit : SignedCommit) : bool =
    let unsigned = {
      Did = commit.Did
      Version = commit.Version
      Data = commit.Data
      Rev = commit.Rev
      Prev = commit.Prev
    }

    let cborMap = unsignedToCborMap unsigned
    let cborBytes = DagCbor.encode cborMap
    let hash = Crypto.sha256 cborBytes
    Crypto.verify key hash commit.Sig

  /// Convert signed commit to CBOR-encodable map
  let signedToCborMap (commit : SignedCommit) : Map<string, obj> =
    let baseMap =
      Map.ofList [
        ("did", box commit.Did)
        ("version", box commit.Version)
        ("data", box commit.Data)
        ("rev", box commit.Rev)
        ("sig", box commit.Sig)
      ]

    match commit.Prev with
    | Some prev -> baseMap |> Map.add "prev" (box prev)
    | None -> baseMap

  /// Serialize a signed commit to DAG-CBOR bytes
  let serializeCommit (commit : SignedCommit) : byte[] =
    signedToCborMap commit |> DagCbor.encode

  /// Get CID for a signed commit
  let commitCid (commit : SignedCommit) : Cid =
    let bytes = serializeCommit commit
    let hash = Crypto.sha256 bytes
    Cid.FromHash hash
