module CarTests

open Xunit
open PDSharp.Core
open PDSharp.Core.Car
open PDSharp.Core.Crypto

[<Fact>]
let ``Varint encodes zero correctly`` () =
  let result = encodeVarint 0
  Assert.Equal<byte[]>([| 0uy |], result)

[<Fact>]
let ``Varint encodes single byte values correctly`` () =
  let result1 = encodeVarint 1
  Assert.Equal<byte[]>([| 1uy |], result1)

  let result127 = encodeVarint 127
  Assert.Equal<byte[]>([| 127uy |], result127)

[<Fact>]
let ``Varint encodes multi-byte values correctly`` () =
  let result128 = encodeVarint 128
  Assert.Equal<byte[]>([| 0x80uy; 0x01uy |], result128)

  let result300 = encodeVarint 300
  Assert.Equal<byte[]>([| 0xACuy; 0x02uy |], result300)

  let result16384 = encodeVarint 16384
  Assert.Equal<byte[]>([| 0x80uy; 0x80uy; 0x01uy |], result16384)

[<Fact>]
let ``CAR header starts with version and roots`` () =
  let hash = sha256Str "test-root"
  let root = Cid.FromHash hash
  let header = createHeader [ root ]

  Assert.True(header.Length > 0, "Header should not be empty")
  Assert.True(header.[0] >= 0xa0uy && header.[0] <= 0xbfuy, "Header should be a CBOR map")

[<Fact>]
let ``CAR block section is varint + CID + data`` () =
  let hash = sha256Str "test-block"
  let cid = Cid.FromHash hash
  let data = [| 1uy; 2uy; 3uy; 4uy |]

  let block = encodeBlock cid data

  Assert.Equal(40uy, block.[0])
  Assert.Equal(41, block.Length)

[<Fact>]
let ``Full CAR creation produces valid structure`` () =
  let hash = sha256Str "root-data"
  let rootCid = Cid.FromHash hash
  let blocks = [ (rootCid, [| 1uy; 2uy; 3uy |]) ]
  let car = createCar [ rootCid ] blocks

  Assert.True(car.Length > 0, "CAR should not be empty")
  Assert.True(car.[0] < 128uy, "Header length should fit in one varint byte for small headers")

[<Fact>]
let ``CAR with multiple blocks`` () =
  let hash1 = sha256Str "block1"
  let hash2 = sha256Str "block2"
  let cid1 = Cid.FromHash hash1
  let cid2 = Cid.FromHash hash2

  let blocks = [ cid1, [| 1uy; 2uy; 3uy |]; cid2, [| 4uy; 5uy; 6uy; 7uy |] ]
  let car = createCar [ cid1 ] blocks
  Assert.True(car.Length > 80, "CAR with two blocks should be substantial")
