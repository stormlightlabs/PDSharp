namespace PDSharp.Core

open System
open System.Text

/// Minimal Base32 (RFC 4648 Lowercase)
module Base32Encoding =
  let private alphabet = "abcdefghijklmnopqrstuvwxyz234567"

  let ToString (data : byte[]) : string =
    if data.Length = 0 then
      ""
    else
      let mutable i = 0
      let mutable index = 0
      let mutable digit = 0
      let mutable currByte = 0
      let mutable nextByte = 0
      let sb = StringBuilder((data.Length + 7) * 8 / 5)

      while i < data.Length do
        currByte <- (int data.[i]) &&& 0xFF

        if index > 3 then
          if (i + 1) < data.Length then
            nextByte <- (int data.[i + 1]) &&& 0xFF
          else
            nextByte <- 0

          digit <- currByte &&& (0xFF >>> index)
          index <- (index + 5) % 8
          digit <- digit <<< index
          digit <- digit ||| (nextByte >>> (8 - index))
          i <- i + 1
        else
          digit <- currByte >>> 8 - (index + 5) &&& 0x1F
          index <- (index + 5) % 8

          if index = 0 then
            i <- i + 1

        sb.Append(alphabet.[digit]) |> ignore

      sb.ToString()

/// Basic CID implementation for AT Protocol (CIDv1 + dag-cbor + sha2-256)
///
/// Constants for ATProto defaults:
///  - Version 1 (0x01)
///  - Codec: dag-cbor (0x71)
///  - Hash: sha2-256 (0x12) - Length 32 (0x20)
[<Struct>]
type Cid =
  val Bytes : byte[]
  new(bytes : byte[]) = { Bytes = bytes }

  static member FromHash(hash : byte[]) =
    if hash.Length <> 32 then
      failwith "Hash must be 32 bytes (sha2-256)"

    let cidBytes = Array.zeroCreate<byte> 36
    cidBytes.[0] <- 0x01uy
    cidBytes.[1] <- 0x71uy
    cidBytes.[2] <- 0x12uy
    cidBytes.[3] <- 0x20uy
    Array.Copy(hash, 0, cidBytes, 4, 32)
    Cid cidBytes

  override this.ToString() =
    "b" + Base32Encoding.ToString(this.Bytes)
