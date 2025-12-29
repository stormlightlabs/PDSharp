namespace PDSharp.Core

open System
open System.IO

/// CARv1 (Content Addressable aRchives) writer module
/// Implements the CAR format per https://ipld.io/specs/transport/car/carv1/
module Car =
  /// Encode an unsigned integer as LEB128 varint
  let encodeVarint (value : int) : byte[] =
    if value < 0 then
      failwith "Varint value must be non-negative"
    elif value = 0 then
      [| 0uy |]
    else
      use ms = new MemoryStream()
      let mutable v = value

      while v > 0 do
        let mutable b = byte (v &&& 0x7F)
        v <- v >>> 7

        if v > 0 then
          b <- b ||| 0x80uy

        ms.WriteByte(b)

      ms.ToArray()

  /// Create CAR header as DAG-CBOR encoded bytes
  /// Header format: { version: 1, roots: [CID, ...] }
  let createHeader (roots : Cid list) : byte[] =
    let headerMap =
      Map.ofList [ ("roots", box (roots |> List.map box)); ("version", box 1) ]

    DagCbor.encode headerMap

  /// Encode a single block section: varint(len) | CID bytes | block data
  let encodeBlock (cid : Cid) (data : byte[]) : byte[] =
    let cidBytes = cid.Bytes
    let sectionLen = cidBytes.Length + data.Length
    let varintBytes = encodeVarint sectionLen

    use ms = new MemoryStream()
    ms.Write(varintBytes, 0, varintBytes.Length)
    ms.Write(cidBytes, 0, cidBytes.Length)
    ms.Write(data, 0, data.Length)
    ms.ToArray()

  /// Create a complete CARv1 file from roots and blocks
  /// CAR format: [varint | header] [varint | CID | block]...
  let createCar (roots : Cid list) (blocks : (Cid * byte[]) seq) : byte[] =
    use ms = new MemoryStream()

    let headerBytes = createHeader roots
    let headerVarint = encodeVarint headerBytes.Length
    ms.Write(headerVarint, 0, headerVarint.Length)
    ms.Write(headerBytes, 0, headerBytes.Length)

    for cid, data in blocks do
      let blockSection = encodeBlock cid data
      ms.Write(blockSection, 0, blockSection.Length)

    ms.ToArray()

  /// Create a CAR from a single root with an async block fetcher
  let createCarAsync (roots : Cid list) (getBlock : Cid -> Async<byte[] option>) (allCids : Cid seq) = async {
    use ms = new MemoryStream()

    let headerBytes = createHeader roots
    let headerVarint = encodeVarint headerBytes.Length
    ms.Write(headerVarint, 0, headerVarint.Length)
    ms.Write(headerBytes, 0, headerBytes.Length)

    for cid in allCids do
      let! dataOpt = getBlock cid

      match dataOpt with
      | Some data ->
        let blockSection = encodeBlock cid data
        ms.Write(blockSection, 0, blockSection.Length)
      | None -> ()

    return ms.ToArray()
  }
