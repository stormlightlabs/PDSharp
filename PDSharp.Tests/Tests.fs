module Tests

open Xunit
open PDSharp.Core.Models
open PDSharp.Core.Config
open PDSharp.Core.Crypto
open PDSharp.Core
open PDSharp.Core.DidResolver
open Org.BouncyCastle.Utilities.Encoders
open System.Text
open System.Text.Json
open Org.BouncyCastle.Math

[<Fact>]
let ``Can instantiate AppConfig`` () =
  let config = {
    PublicUrl = "https://example.com"
    DidHost = "did:web:example.com"
    JwtSecret = "test-secret-key-for-testing-only"
    SqliteConnectionString = "Data Source=:memory:"
    DisableWalAutoCheckpoint = false
    BlobStore = Disk "blobs"
  }

  Assert.Equal("did:web:example.com", config.DidHost)

[<Fact>]
let ``CID TryParse roundtrip`` () =
  let hash = Crypto.sha256Str "test-data"
  let cid = Cid.FromHash hash
  let cidStr = cid.ToString()

  match Cid.TryParse cidStr with
  | Some parsed -> Assert.Equal<byte[]>(cid.Bytes, parsed.Bytes)
  | None -> Assert.Fail "TryParse should succeed for valid CID"

[<Fact>]
let ``CID TryParse returns None for invalid`` () =
  Assert.True(Cid.TryParse("invalid").IsNone)
  Assert.True(Cid.TryParse("").IsNone)
  Assert.True(Cid.TryParse("btooshort").IsNone)

[<Fact>]
let ``Can instantiate DescribeServerResponse`` () =
  let response = {
    availableUserDomains = [ "example.com" ]
    did = "did:web:example.com"
    inviteCodeRequired = true
  }

  Assert.Equal("did:web:example.com", response.did)
  Assert.Equal(1, response.availableUserDomains.Length)

[<Fact>]
let ``SHA-256 Hashing correct`` () =
  let input = "hello world"
  let hash = sha256Str input
  let expected = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9"
  let actual = Hex.ToHexString(hash)
  Assert.Equal(expected, actual)

[<Fact>]
let ``ECDSA P-256 Sign and Verify`` () =
  let keyPair = generateKey P256
  let data = Encoding.UTF8.GetBytes("test message")
  let hash = sha256 data
  let signature = sign keyPair hash
  Assert.True(signature.Length = 64, "Signature should be 64 bytes (R|S)")

  let valid = verify keyPair hash signature
  Assert.True(valid, "Signature verification failed")

[<Fact>]
let ``ECDSA K-256 Sign and Verify`` () =
  let keyPair = generateKey K256
  let data = Encoding.UTF8.GetBytes("test k256")
  let hash = sha256 data
  let signature = sign keyPair hash
  Assert.True(signature.Length = 64, "Signature should be 64 bytes")

  let valid = verify keyPair hash signature
  Assert.True(valid, "Signature verification failed")

[<Fact>]
let ``Low-S Enforcement Logic`` () =
  let n =
    BigInteger("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", 16) // secp256k1 N

  let halfN = n.ShiftRight(1)
  let highS = halfN.Add(BigInteger.One)

  let lowS = enforceLowS highS n
  Assert.True(lowS.CompareTo halfN <= 0, "S value should be <= N/2")
  Assert.Equal(n.Subtract highS, lowS)

[<Fact>]
let ``DidDocument JSON deserialization`` () =
  let json =
    """{
      "id": "did:web:example.com",
      "verificationMethod": [{
        "id": "did:web:example.com#atproto",
        "type": "Multikey",
        "controller": "did:web:example.com",
        "publicKeyMultibase": "zQ3sh..."
      }]
    }"""

  let doc =
    JsonSerializer.Deserialize<DidDocument>(json, JsonSerializerOptions(PropertyNameCaseInsensitive = true))

  Assert.Equal("did:web:example.com", doc.Id)
  Assert.Single doc.VerificationMethod |> ignore
  Assert.Equal("Multikey", doc.VerificationMethod.Head.Type)

[<Fact>]
let ``CID Generation from Hash`` () =
  let hash =
    Hex.Decode "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9"

  let cid = Cid.FromHash hash
  Assert.Equal<byte>(0x01uy, cid.Bytes.[0])
  Assert.Equal<byte>(0x71uy, cid.Bytes.[1])
  Assert.Equal<byte>(0x12uy, cid.Bytes.[2])
  Assert.Equal<byte>(0x20uy, cid.Bytes.[3])

[<Fact>]
let ``DAG-CBOR Canonical Sorting`` () =
  let m = Map.ofList [ "b", box 1; "a", box 2 ]
  let encoded = DagCbor.encode m
  let hex = Hex.ToHexString encoded
  Assert.Equal("a2616102616201", hex)

[<Fact>]
let ``DAG-CBOR Sorting Length vs Bytes`` () =
  let m = Map.ofList [ "aa", box 1; "b", box 2 ]
  let encoded = DagCbor.encode m
  let hex = Hex.ToHexString encoded
  Assert.Equal("a261620262616101", hex)
