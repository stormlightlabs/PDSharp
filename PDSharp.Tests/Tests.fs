module Tests

open System
open Xunit
open PDSharp.Core.Models
open PDSharp.Core.Config
open PDSharp.Core.Crypto
open PDSharp.Core.DidResolver
open Org.BouncyCastle.Utilities.Encoders
open System.Text
open Org.BouncyCastle.Math

[<Fact>]
let ``My test`` () = Assert.True(true)

[<Fact>]
let ``Can instantiate AppConfig`` () =
  let config = {
    PublicUrl = "https://example.com"
    DidHost = "did:web:example.com"
  }

  Assert.Equal("did:web:example.com", config.DidHost)

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
    System.Text.Json.JsonSerializer.Deserialize<DidDocument>(
      json,
      Json.JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    )

  Assert.Equal("did:web:example.com", doc.Id)
  Assert.Single doc.VerificationMethod |> ignore
  Assert.Equal("Multikey", doc.VerificationMethod.Head.Type)
