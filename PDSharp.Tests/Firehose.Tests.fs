module Firehose.Tests

open Xunit
open PDSharp.Core
open PDSharp.Core.Firehose
open PDSharp.Core.Crypto

[<Fact>]
let ``nextSeq monotonically increases`` () =
  resetSeq ()
  let seq1 = nextSeq ()
  let seq2 = nextSeq ()
  let seq3 = nextSeq ()

  Assert.Equal(1L, seq1)
  Assert.Equal(2L, seq2)
  Assert.Equal(3L, seq3)

[<Fact>]
let ``currentSeq returns without incrementing`` () =
  resetSeq ()
  let _ = nextSeq () // 1
  let _ = nextSeq () // 2
  let current = currentSeq ()
  let next = nextSeq ()

  Assert.Equal(2L, current)
  Assert.Equal(3L, next)

[<Fact>]
let ``createCommitEvent has correct fields`` () =
  resetSeq ()
  let hash = sha256Str "test"
  let cid = Cid.FromHash hash
  let carBytes = [| 0x01uy; 0x02uy |]

  let event = createCommitEvent "did:web:test" "rev123" cid carBytes

  Assert.Equal(1L, event.Seq)
  Assert.Equal("did:web:test", event.Did)
  Assert.Equal("rev123", event.Rev)
  Assert.Equal<byte[]>(cid.Bytes, event.Commit.Bytes)
  Assert.Equal<byte[]>(carBytes, event.Blocks)

[<Fact>]
let ``encodeEvent produces valid CBOR`` () =
  resetSeq ()
  let hash = sha256Str "test"
  let cid = Cid.FromHash hash
  let carBytes = [| 0x01uy; 0x02uy |]
  let event = createCommitEvent "did:web:test" "rev123" cid carBytes
  let encoded = encodeEvent event

  Assert.True(encoded.Length > 0)
  Assert.True(encoded.[0] >= 0xa0uy, "Should encode as CBOR map")
