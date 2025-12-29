namespace PDSharp.Core

open System
open System.Text
open Org.BouncyCastle.Crypto.Digests
open Org.BouncyCastle.Crypto.Signers
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Math
open Org.BouncyCastle.Asn1.X9
open Org.BouncyCastle.Crypto.Generators
open Org.BouncyCastle.Security
open Org.BouncyCastle.Asn1.Sec

module Crypto =
  let sha256 (data : byte[]) : byte[] =
    let digest = Sha256Digest()
    digest.BlockUpdate(data, 0, data.Length)
    let size = digest.GetDigestSize()
    let result = Array.zeroCreate<byte> size
    digest.DoFinal(result, 0) |> ignore
    result

  let sha256Str (input : string) : byte[] = sha256 (Encoding.UTF8.GetBytes(input))

  type Curve =
    | P256
    | K256

  let getCurveParams (curve : Curve) =
    match curve with
    | P256 -> ECNamedCurveTable.GetByName("secp256r1")
    | K256 -> ECNamedCurveTable.GetByName("secp256k1")

  let getDomainParams (curve : Curve) =
    let ecP = getCurveParams curve
    ECDomainParameters(ecP.Curve, ecP.G, ecP.N, ecP.H, ecP.GetSeed())

  type EcKeyPair = {
    PrivateKey : ECPrivateKeyParameters option
    PublicKey : ECPublicKeyParameters
    Curve : Curve
  }

  let generateKey (curve : Curve) : EcKeyPair =
    let domainParams = getDomainParams curve
    let genParam = ECKeyGenerationParameters(domainParams, SecureRandom())
    let generator = ECKeyPairGenerator()
    generator.Init(genParam)
    let pair = generator.GenerateKeyPair()

    {
      PrivateKey = Some(pair.Private :?> ECPrivateKeyParameters)
      PublicKey = (pair.Public :?> ECPublicKeyParameters)
      Curve = curve
    }

  let enforceLowS (s : BigInteger) (n : BigInteger) : BigInteger =
    let halfN = n.ShiftRight(1)
    if s.CompareTo(halfN) > 0 then n.Subtract(s) else s

  let sign (key : EcKeyPair) (digest : byte[]) : byte[] =
    match key.PrivateKey with
    | None -> failwith "Private key required for signing"
    | Some privParams ->
      let signer = ECDsaSigner()
      signer.Init(true, privParams)
      let inputs = digest
      let signature = signer.GenerateSignature(inputs)
      let r = signature.[0]
      let s = signature.[1]

      let n = privParams.Parameters.N
      let canonicalS = enforceLowS s n

      let to32Bytes (bi : BigInteger) =
        let bytes = bi.ToByteArrayUnsigned()

        if bytes.Length > 32 then
          failwith "Signature component too large"

        let padded = Array.zeroCreate<byte> 32
        Array.Copy(bytes, 0, padded, 32 - bytes.Length, bytes.Length)
        padded

      let rBytes = to32Bytes r
      let sBytes = to32Bytes canonicalS
      Array.append rBytes sBytes

  let verify (key : EcKeyPair) (digest : byte[]) (signature : byte[]) : bool =
    if signature.Length <> 64 then
      false
    else
      let rBytes = Array.sub signature 0 32
      let sBytes = Array.sub signature 32 32

      let r = BigInteger(1, rBytes)
      let s = BigInteger(1, sBytes)

      let domainParams = key.PublicKey.Parameters
      let n = domainParams.N
      let halfN = n.ShiftRight(1)

      if s.CompareTo(halfN) > 0 then
        false
      else
        let signer = ECDsaSigner()
        signer.Init(false, key.PublicKey)
        signer.VerifySignature(digest, r, s)
