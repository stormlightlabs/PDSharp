namespace PDSharp.Core

open System
open System.Collections.Generic
open System.Formats.Cbor
open System.IO
open System.Text

module DagCbor =
  type SortKey = { Length : int; Bytes : byte[] }

  let private getSortKey (key : string) =
    let bytes = Encoding.UTF8.GetBytes(key)
    { Length = bytes.Length; Bytes = bytes }

  let private compareKeys (a : string) (b : string) =
    let ka = getSortKey a
    let kb = getSortKey b

    if ka.Length <> kb.Length then
      ka.Length.CompareTo kb.Length
    else
      let mutable res = 0
      let mutable i = 0

      while res = 0 && i < ka.Bytes.Length do
        res <- ka.Bytes.[i].CompareTo(kb.Bytes.[i])
        i <- i + 1

      res

  let rec private writeItem (writer : CborWriter) (item : obj) =
    match item with
    | null -> writer.WriteNull()
    | :? bool as b -> writer.WriteBoolean(b)
    | :? int as i -> writer.WriteInt32(i)
    | :? int64 as l -> writer.WriteInt64(l)
    | :? string as s -> writer.WriteTextString(s)
    | :? (byte[]) as b -> writer.WriteByteString(b)
    | :? Cid as c ->
      let tag = LanguagePrimitives.EnumOfValue<uint64, CborTag>(42UL)
      writer.WriteTag(tag)
      let rawCid = c.Bytes
      let linkBytes = Array.zeroCreate<byte> (rawCid.Length + 1)
      linkBytes.[0] <- 0x00uy
      Array.Copy(rawCid, 0, linkBytes, 1, rawCid.Length)
      writer.WriteByteString(linkBytes)

    | :? Map<string, obj> as m ->
      let keys = m |> Map.toList |> List.map fst |> List.sortWith compareKeys
      writer.WriteStartMap(keys.Length)

      for k in keys do
        writer.WriteTextString(k)
        writeItem writer (m.[k])

      writer.WriteEndMap()

    | :? IDictionary<string, obj> as d ->
      let keys = d.Keys |> Seq.toList |> List.sortWith compareKeys
      writer.WriteStartMap(d.Count)

      for k in keys do
        writer.WriteTextString(k)
        writeItem writer (d.[k])

      writer.WriteEndMap()

    | :? seq<obj> as l ->
      let arr = l |> Seq.toArray
      writer.WriteStartArray(arr.Length)

      for x in arr do
        writeItem writer x

      writer.WriteEndArray()

    | _ -> failwith $"Unsupported type for DAG-CBOR: {item.GetType().Name}"

  let encode (data : obj) : byte[] =
    let writer = new CborWriter(CborConformanceMode.Strict, false, false)
    writeItem writer data
    writer.Encode()
