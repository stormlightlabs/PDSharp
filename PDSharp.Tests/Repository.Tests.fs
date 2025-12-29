module RepositoryTests

open Xunit
open PDSharp.Core
open PDSharp.Core.Crypto
open PDSharp.Core.Repository

[<Fact>]
let ``TID generation produces 13 character string`` () =
  let tid = Tid.generate ()
  Assert.Equal(13, tid.Length)

[<Fact>]
let ``TID generation is sortable by time`` () =
  let tid1 = Tid.generate ()
  System.Threading.Thread.Sleep(2)
  let tid2 = Tid.generate ()
  Assert.True(tid2 > tid1, $"Expected {tid2} > {tid1}")

[<Fact>]
let ``Commit signing produces valid signature`` () =
  let keyPair = generateKey P256
  let mstRoot = Cid.FromHash(sha256Str "mst-root")

  let unsigned = {
    Did = "did:plc:test1234"
    Version = 3
    Data = mstRoot
    Rev = Tid.generate ()
    Prev = None
  }

  let signed = signCommit keyPair unsigned

  Assert.Equal(64, signed.Sig.Length)
  Assert.Equal(unsigned.Did, signed.Did)
  Assert.Equal(unsigned.Data, signed.Data)

[<Fact>]
let ``Commit verification succeeds for valid commit`` () =
  let keyPair = generateKey P256
  let mstRoot = Cid.FromHash(sha256Str "data")

  let unsigned = {
    Did = "did:plc:abc"
    Version = 3
    Data = mstRoot
    Rev = Tid.generate ()
    Prev = None
  }

  let signed = signCommit keyPair unsigned

  Assert.True(verifyCommit keyPair signed)

[<Fact>]
let ``Commit verification fails for tampered data`` () =
  let keyPair = generateKey P256
  let mstRoot = Cid.FromHash(sha256Str "original")

  let unsigned = {
    Did = "did:plc:abc"
    Version = 3
    Data = mstRoot
    Rev = Tid.generate ()
    Prev = None
  }

  let signed = signCommit keyPair unsigned
  let tampered = { signed with Did = "did:plc:different" }

  Assert.False(verifyCommit keyPair tampered)

[<Fact>]
let ``Commit with prev CID`` () =
  let keyPair = generateKey P256
  let mstRoot = Cid.FromHash(sha256Str "new-data")
  let prevCid = Cid.FromHash(sha256Str "prev-commit")

  let unsigned = {
    Did = "did:plc:abc"
    Version = 3
    Data = mstRoot
    Rev = Tid.generate ()
    Prev = Some prevCid
  }

  let signed = signCommit keyPair unsigned
  Assert.True(verifyCommit keyPair signed)
  Assert.True(Option.isSome signed.Prev)

[<Fact>]
let ``Commit CID is deterministic`` () =
  let keyPair = generateKey P256
  let mstRoot = Cid.FromHash(sha256Str "data")

  let unsigned = {
    Did = "did:plc:abc"
    Version = 3
    Data = mstRoot
    Rev = "abcdefghijklm"
    Prev = None
  }

  let signed = signCommit keyPair unsigned
  let cid1 = commitCid signed
  let cid2 = commitCid signed

  Assert.Equal<byte[]>(cid1.Bytes, cid2.Bytes)

[<Fact>]
let ``Commit serialization produces valid DAG-CBOR`` () =
  let keyPair = generateKey P256
  let mstRoot = Cid.FromHash(sha256Str "test")

  let unsigned = {
    Did = "did:plc:test"
    Version = 3
    Data = mstRoot
    Rev = Tid.generate ()
    Prev = None
  }

  let signed = signCommit keyPair unsigned
  let bytes = serializeCommit signed

  Assert.True(bytes.Length > 0)
  Assert.True(bytes.[0] >= 0xa0uy && bytes.[0] <= 0xbfuy)
