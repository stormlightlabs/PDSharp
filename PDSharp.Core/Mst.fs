namespace PDSharp.Core

open System
open System.Collections.Generic
open System.Formats.Cbor

module Mst =
  type MstEntry = {
    PrefixLen : int
    KeySuffix : string
    Value : Cid
    Tree : Cid option
  }

  type MstNode = { Left : Cid option; Entries : MstEntry list }

  /// Layer Calculation
  let layer (key : string) : int =
    let hash = Crypto.sha256Str key
    let mutable zeros = 0
    let mutable i = 0
    let mutable found = false

    while i < hash.Length && not found do
      let b = hash.[i]

      if b = 0uy then
        zeros <- zeros + 8
        i <- i + 1
      else
        let mutable mask = 0x80uy

        while b &&& mask = 0uy do
          zeros <- zeros + 1
          mask <- mask >>> 1

        found <- true

    zeros / 2

  let entryToCborObj (e : MstEntry) : obj =
    let arr = Array.zeroCreate<obj> 4
    arr.[0] <- e.PrefixLen :> obj
    arr.[1] <- e.KeySuffix :> obj
    arr.[2] <- e.Value :> obj

    arr.[3] <-
      match e.Tree with
      | Some c -> c :> obj
      | None -> null

    arr :> obj

  let nodeToCborObj (node : MstNode) : obj =
    let arr = Array.zeroCreate<obj> 2

    arr.[0] <-
      match node.Left with
      | Some c -> c :> obj
      | None -> null

    let entriesArr = node.Entries |> List.map entryToCborObj |> Seq.toArray
    arr.[1] <- entriesArr :> obj
    arr :> obj

  let serialize (node : MstNode) : byte[] = nodeToCborObj node |> DagCbor.encode

  let readCid (reader : CborReader) =
    let tag = reader.ReadTag()
    let tag42 = LanguagePrimitives.EnumOfValue<uint64, CborTag>(42UL)

    if tag <> tag42 then
      failwith "Expected CID tag 42"

    let bytes = reader.ReadByteString()

    if bytes.Length > 0 && bytes.[0] = 0x00uy then
      let raw = Array.zeroCreate<byte> (bytes.Length - 1)
      Array.Copy(bytes, 1, raw, 0, raw.Length)
      Cid raw
    else
      Cid bytes

  let deserialize (data : byte[]) : MstNode =
    let reader = new CborReader(data.AsMemory(), CborConformanceMode.Strict)
    let len = reader.ReadStartArray()

    if len.HasValue && len.Value <> 2 then
      failwith "MST node must be array of length 2"

    let left =
      match reader.PeekState() with
      | CborReaderState.Null ->
        reader.ReadNull()
        None
      | _ -> Some(readCid reader)

    let entriesLen = reader.ReadStartArray()
    let mutable entries = []

    let count =
      if entriesLen.HasValue then
        entriesLen.Value
      else
        Int32.MaxValue

    let mutable i = 0

    while i < count && reader.PeekState() <> CborReaderState.EndArray do
      let entryLen = reader.ReadStartArray()

      if entryLen.HasValue && entryLen.Value <> 4 then
        failwith "MST entry must be array of length 4"

      let p = reader.ReadInt32()
      let k = reader.ReadTextString()
      let v = readCid reader

      let t =
        match reader.PeekState() with
        | CborReaderState.Null ->
          reader.ReadNull()
          None
        | _ -> Some(readCid reader)

      reader.ReadEndArray()

      entries <- entries @ [ { PrefixLen = p; KeySuffix = k; Value = v; Tree = t } ]
      i <- i + 1

    reader.ReadEndArray()
    reader.ReadEndArray()
    { Left = left; Entries = entries }

  let compareKeys (a : string) (b : string) : int =
    let bytesA = System.Text.Encoding.UTF8.GetBytes(a)
    let bytesB = System.Text.Encoding.UTF8.GetBytes(b)
    let len = min bytesA.Length bytesB.Length
    let mutable res = 0
    let mutable i = 0

    while res = 0 && i < len do
      res <- bytesA.[i].CompareTo(bytesB.[i])
      i <- i + 1

    if res <> 0 then
      res
    else
      bytesA.Length.CompareTo bytesB.Length

  type NodeLoader = Cid -> Async<MstNode option>
  type NodePersister = MstNode -> Async<Cid>

  let storeNode (persister : NodePersister) (node : MstNode) : Async<Cid> = persister node

  let rec get (loader : NodeLoader) (node : MstNode) (key : string) (prevKey : string) : Async<Cid option> = async {
    let mutable currentKey = prevKey
    let mutable foundEntry : MstEntry option = None
    let mutable nextTree : Cid option = node.Left
    let mutable stop = false
    let mutable i = 0

    while not stop && i < node.Entries.Length do
      let e = node.Entries.[i]

      let prefix =
        if e.PrefixLen > currentKey.Length then
          currentKey
        else
          currentKey.Substring(0, e.PrefixLen)

      let fullKey = prefix + e.KeySuffix
      let cmp = compareKeys key fullKey

      if cmp = 0 then
        foundEntry <- Some e
        stop <- true
      elif cmp < 0 then
        stop <- true
      else
        nextTree <- e.Tree
        currentKey <- fullKey
        i <- i + 1

    match foundEntry with
    | Some e -> return Some e.Value
    | None ->
      match nextTree with
      | None -> return None
      | Some cid ->
        let! child = loader cid

        match child with
        | Some cNode -> return! get loader cNode key currentKey
        | None -> return None
  }

  let sharedPrefixLen (a : string) (b : string) =
    let len = min a.Length b.Length
    let mutable i = 0

    while i < len && a.[i] = b.[i] do
      i <- i + 1

    i

  let nodeLayer (node : MstNode) (prevKey : string) =
    match node.Entries with
    | [] -> -1
    | first :: _ ->
      let prefix =
        if first.PrefixLen > prevKey.Length then
          prevKey
        else
          prevKey.Substring(0, first.PrefixLen)

      let fullKey = prefix + first.KeySuffix
      layer fullKey

  /// Splits a node around a key (which is assumed to be higher layer than any in the node).
  ///
  /// Returns (LeftNode, RightNode).
  let rec split
    (loader : NodeLoader)
    (persister : NodePersister)
    (node : MstNode)
    (key : string)
    (prevKey : string)
    : Async<MstNode * MstNode> =
    async {
      let mutable splitIdx = -1
      let mutable found = false
      let mutable currentKey = prevKey
      let mutable i = 0
      let entries = node.Entries
      let mutable splitPrevKey = prevKey

      while i < entries.Length && not found do
        let e = entries.[i]

        let prefix =
          if e.PrefixLen > currentKey.Length then
            currentKey
          else
            currentKey.Substring(0, e.PrefixLen)

        let fullKey = prefix + e.KeySuffix

        if compareKeys key fullKey < 0 then
          splitIdx <- i
          found <- true
        else
          currentKey <- fullKey
          splitPrevKey <- currentKey
          i <- i + 1

      let childCidToSplit =
        if found then
          if splitIdx = 0 then
            node.Left
          else
            entries.[splitIdx - 1].Tree
        else if entries.Length = 0 then
          node.Left
        else
          entries.[entries.Length - 1].Tree

      let! (lChild, rChild) = async {
        match childCidToSplit with
        | None -> return ({ Left = None; Entries = [] }, { Left = None; Entries = [] })
        | Some cid ->
          let! childNodeOpt = loader cid

          match childNodeOpt with
          | None -> return ({ Left = None; Entries = [] }, { Left = None; Entries = [] })
          | Some childNode -> return! split loader persister childNode key currentKey
      }

      let persistOrNone (n : MstNode) = async {
        if n.Entries.IsEmpty && n.Left.IsNone then
          return None
        else
          let! c = persister n
          return Some c
      }

      let! lCid = persistOrNone lChild
      let! rCid = persistOrNone rChild
      let leftEntries = if found then entries |> List.take splitIdx else entries

      let newLeftEntries =
        if leftEntries.IsEmpty then
          []
        else
          let last = leftEntries.[leftEntries.Length - 1]
          let newLast = { last with Tree = lCid }
          (leftEntries |> List.take (leftEntries.Length - 1)) @ [ newLast ]

      let leftNode =
        if leftEntries.IsEmpty then
          { Left = lCid; Entries = [] }
        else
          { Left = node.Left; Entries = newLeftEntries }

      let rightEntries = if found then entries |> List.skip splitIdx else []

      let newRightEntries =
        match rightEntries with
        | [] -> []
        | first :: rest ->
          let firstFullKey =
            let prefix =
              if first.PrefixLen > currentKey.Length then
                currentKey
              else
                currentKey.Substring(0, first.PrefixLen)

            prefix + first.KeySuffix

          let newP = sharedPrefixLen key firstFullKey
          let newSuffix = firstFullKey.Substring(newP)
          let newFirst = { first with PrefixLen = newP; KeySuffix = newSuffix }
          newFirst :: rest

      let rightNode = { Left = rCid; Entries = newRightEntries }
      return (leftNode, rightNode)
    }

  let rec put
    (loader : NodeLoader)
    (persister : NodePersister)
    (node : MstNode)
    (key : string)
    (value : Cid)
    (prevKey : string)
    : Async<MstNode> =
    async {
      let kLayer = layer key

      let nLayer =
        match node.Entries with
        | [] -> -1
        | first :: _ ->
          let prefix =
            if first.PrefixLen > prevKey.Length then
              prevKey
            else
              prevKey.Substring(0, first.PrefixLen)

          let fullKey = prefix + first.KeySuffix
          layer fullKey

      if kLayer > nLayer then
        let! (lNode, rNode) = split loader persister node key prevKey

        let persistOrNone (n : MstNode) = async {
          if n.Entries.IsEmpty && n.Left.IsNone then
            return None
          else
            let! c = persister n
            return Some c
        }

        let! lCid = persistOrNone lNode
        let! rCid = persistOrNone rNode

        let p = sharedPrefixLen prevKey key
        let suffix = key.Substring(p)

        return {
          Left = lCid
          Entries = [
            {
              PrefixLen = p
              KeySuffix = suffix
              Value = value
              Tree = rCid
            }
          ]
        }

      elif kLayer < nLayer then
        let mutable nextCid = node.Left
        let mutable currentKey = prevKey
        let mutable found = false
        let mutable i = 0

        let entries = node.Entries
        let mutable childIdx = -1

        while i < entries.Length && not found do
          let e = entries.[i]

          let prefix =
            if e.PrefixLen > currentKey.Length then
              currentKey
            else
              currentKey.Substring(0, e.PrefixLen)

          let fullKey = prefix + e.KeySuffix

          if compareKeys key fullKey < 0 then
            found <- true
          else
            childIdx <- i
            nextCid <- e.Tree
            currentKey <- fullKey
            i <- i + 1

        let! childNode =
          match nextCid with
          | Some c -> loader c
          | None -> async { return Some { Left = None; Entries = [] } }

        match childNode with
        | None -> return failwith "Failed to load child node"
        | Some cn ->
          let! newChildNode = put loader persister cn key value currentKey
          let! newChildCid = persister newChildNode

          if childIdx = -1 then
            return { node with Left = Some newChildCid }
          else
            return {
              node with
                  Entries =
                    entries
                    |> List.mapi (fun idx x ->
                      if idx = childIdx then
                        { entries.[childIdx] with Tree = Some newChildCid }
                      else
                        x)
            }

      else
        let mutable insertIdx = -1
        let mutable found = false
        let mutable currentKey = prevKey
        let mutable i = 0
        let mutable targetChildCid = None
        let mutable childPrevKey = prevKey

        let fullKeysCache = new List<string>()

        while i < node.Entries.Length && not found do
          let e = node.Entries.[i]

          let prefix =
            if e.PrefixLen > currentKey.Length then
              currentKey
            else
              currentKey.Substring(0, e.PrefixLen)

          let fullKey = prefix + e.KeySuffix
          fullKeysCache.Add(fullKey)

          let cmp = compareKeys key fullKey

          if cmp = 0 then
            insertIdx <- i
            found <- true
          elif cmp < 0 then
            insertIdx <- i
            targetChildCid <- if i = 0 then node.Left else node.Entries.[i - 1].Tree
            childPrevKey <- currentKey
            found <- true
          else
            currentKey <- fullKey
            i <- i + 1

        if not found then
          insertIdx <- node.Entries.Length

          targetChildCid <-
            if node.Entries.Length = 0 then
              node.Left
            else
              node.Entries.[node.Entries.Length - 1].Tree

          childPrevKey <- currentKey

        let entries = node.Entries

        if found && compareKeys key fullKeysCache.[insertIdx] = 0 then
          let e = entries.[insertIdx]
          let newE = { e with Value = value }

          let newEntries =
            entries |> List.mapi (fun idx x -> if idx = insertIdx then newE else x)

          return { node with Entries = newEntries }
        else
          let! (lChild, rChild) = async {
            match targetChildCid with
            | None -> return { Left = None; Entries = [] }, { Left = None; Entries = [] }
            | Some cid ->
              let! cOpt = loader cid

              match cOpt with
              | None -> return { Left = None; Entries = [] }, { Left = None; Entries = [] }
              | Some c -> return! split loader persister c key childPrevKey
          }

          let persistOrNone (n : MstNode) = async {
            if n.Entries.IsEmpty && n.Left.IsNone then
              return None
            else
              let! c = persister n
              return Some c
          }

          let! lCid = persistOrNone lChild
          let! rCid = persistOrNone rChild

          let beforeEntries =
            if insertIdx = 0 then
              []
            else
              let prevE = entries.[insertIdx - 1]
              let newPrevE = { prevE with Tree = lCid }
              (entries |> List.take (insertIdx - 1)) @ [ newPrevE ]

          let newLeft = if insertIdx = 0 then lCid else node.Left
          let getFullKey idx = fullKeysCache.[idx]
          let p = sharedPrefixLen childPrevKey key
          let suffix = key.Substring(p)

          let newEntry = {
            PrefixLen = p
            KeySuffix = suffix
            Value = value
            Tree = rCid
          }

          let afterEntries =
            if insertIdx >= entries.Length then
              []
            else
              let first = entries.[insertIdx]
              let firstFullKey = getFullKey insertIdx

              let newP = sharedPrefixLen key firstFullKey
              let newS = firstFullKey.Substring(newP)
              let newFirst = { first with PrefixLen = newP; KeySuffix = newS }

              [ newFirst ] @ (entries |> List.skip (insertIdx + 1))

          let newEntries = beforeEntries @ [ newEntry ] @ afterEntries

          return { Left = newLeft; Entries = newEntries }
    }

  // --- Merge Operation ---
  let rec merge
    (loader : NodeLoader)
    (persister : NodePersister)
    (leftCid : Cid option)
    (rightCid : Cid option)
    (prevKey : string)
    : Async<MstNode> =
    async {
      match leftCid, rightCid with
      | None, None -> return { Left = None; Entries = [] }
      | Some l, None ->
        let! n = loader l
        return n |> Option.defaultValue { Left = None; Entries = [] }
      | None, Some r ->
        let! n = loader r
        return n |> Option.defaultValue { Left = None; Entries = [] }
      | Some l, Some r ->
        let! lNodeOpt = loader l
        let! rNodeOpt = loader r
        let lNode = lNodeOpt |> Option.defaultValue { Left = None; Entries = [] }
        let rNode = rNodeOpt |> Option.defaultValue { Left = None; Entries = [] }

        let lLayer = nodeLayer lNode prevKey
        let mutable current = prevKey

        for e in lNode.Entries do
          let p =
            if e.PrefixLen > current.Length then
              current
            else
              current.Substring(0, e.PrefixLen)

          current <- p + e.KeySuffix

        let rightPrevKey = current

        let realLLayer = nodeLayer lNode prevKey
        let realRLayer = nodeLayer rNode rightPrevKey

        if realLLayer > realRLayer then
          match lNode.Entries with
          | [] -> return! merge loader persister lNode.Left rightCid prevKey
          | entries ->
            let lastIdx = entries.Length - 1
            let lastEntry = entries.[lastIdx]

            let! mergedChild = merge loader persister lastEntry.Tree rightCid rightPrevKey
            let! mergedCid = persister mergedChild

            let newEntry = { lastEntry with Tree = Some mergedCid }
            let newEntries = (entries |> List.take lastIdx) @ [ newEntry ]
            return { lNode with Entries = newEntries }

        elif realRLayer > realLLayer then
          match rNode.Entries with
          | [] -> return! merge loader persister leftCid rNode.Left prevKey
          | _ ->
            let! mergedChild = merge loader persister leftCid rNode.Left prevKey
            let! mergedCid = persister mergedChild

            return { rNode with Left = Some mergedCid }

        else
          let boundaryL =
            match lNode.Entries with
            | [] -> lNode.Left
            | es -> es.[es.Length - 1].Tree

          let boundaryR = rNode.Left

          let! mergedBoundaryNode = merge loader persister boundaryL boundaryR rightPrevKey
          let! mergedBoundaryCid = persister mergedBoundaryNode

          let newEntries =
            match lNode.Entries with
            | [] -> rNode.Entries
            | lEntries ->
              let lastIdx = lEntries.Length - 1
              let lastE = lEntries.[lastIdx]
              let newLastE = { lastE with Tree = Some mergedBoundaryCid }
              (lEntries |> List.take lastIdx) @ [ newLastE ] @ rNode.Entries

          let newLeft =
            if lNode.Entries.IsEmpty then
              Some mergedBoundaryCid
            else
              lNode.Left

          return { Left = newLeft; Entries = newEntries }
    }

  let rec delete
    (loader : NodeLoader)
    (persister : NodePersister)
    (node : MstNode)
    (key : string)
    (prevKey : string)
    : Async<MstNode option> =
    async {
      let mutable currentKey = prevKey
      let mutable foundIdx = -1
      let mutable nextTreeIdx = -1
      let mutable i = 0
      let mutable found = false

      while i < node.Entries.Length && not found do
        let e = node.Entries.[i]

        let prefix =
          if e.PrefixLen > currentKey.Length then
            currentKey
          else
            currentKey.Substring(0, e.PrefixLen)

        let fullKey = prefix + e.KeySuffix

        let cmp = compareKeys key fullKey

        if cmp = 0 then
          foundIdx <- i
          found <- true
        elif cmp < 0 then
          found <- true
          nextTreeIdx <- i - 1
        else
          currentKey <- fullKey
          i <- i + 1

      if not found then
        nextTreeIdx <- node.Entries.Length - 1

      if foundIdx <> -1 then
        let e = node.Entries.[foundIdx]

        let leftChildCid =
          if foundIdx = 0 then
            node.Left
          else
            node.Entries.[foundIdx - 1].Tree

        let rightChildCid = e.Tree

        let mergePrevKey = if foundIdx = 0 then prevKey else currentKey

        let! mergedChildNode = merge loader persister leftChildCid rightChildCid mergePrevKey
        let! mergedChildCid = persister mergedChildNode

        let newEntries =
          if foundIdx = 0 then
            let rest = node.Entries |> List.skip 1

            match rest with
            | [] -> []
            | first :: rs ->
              let firstFullKey =
                let oldP =
                  if first.PrefixLen > key.Length then
                    key
                  else
                    key.Substring(0, first.PrefixLen)

                oldP + first.KeySuffix

              let newP = sharedPrefixLen prevKey firstFullKey
              let newSuffix = firstFullKey.Substring(newP)
              let newFirst = { first with PrefixLen = newP; KeySuffix = newSuffix }
              newFirst :: rs
          else
            let before = node.Entries |> List.take (foundIdx - 1)
            let prevE = node.Entries.[foundIdx - 1]
            let newPrevE = { prevE with Tree = Some mergedChildCid }

            let rest = node.Entries |> List.skip (foundIdx + 1)

            let newRest =
              match rest with
              | [] -> []
              | first :: rs ->
                let firstFullKey =
                  let oldP =
                    if first.PrefixLen > key.Length then
                      key
                    else
                      key.Substring(0, first.PrefixLen)

                  oldP + first.KeySuffix

                let newP = sharedPrefixLen mergePrevKey firstFullKey
                let newSuffix = firstFullKey.Substring(newP)
                let newFirst = { first with PrefixLen = newP; KeySuffix = newSuffix }
                newFirst :: rs

            before @ [ newPrevE ] @ newRest

        let newLeft = if foundIdx = 0 then Some mergedChildCid else node.Left

        let newNode = { Left = newLeft; Entries = newEntries }

        if newNode.Entries.IsEmpty && newNode.Left.IsNone then
          return None
        else
          return Some newNode

      else
        let childCid =
          if nextTreeIdx = -1 then
            node.Left
          else
            node.Entries.[nextTreeIdx].Tree

        match childCid with
        | None -> return Some node
        | Some cid ->
          let! childNodeOpt = loader cid

          match childNodeOpt with
          | None -> return Some node
          | Some childNode ->
            let! newChildOpt = delete loader persister childNode key currentKey

            match newChildOpt with
            | None ->
              if nextTreeIdx = -1 then
                return Some { node with Left = None }
              else
                let prevE = node.Entries.[nextTreeIdx]
                let newPrevE = { prevE with Tree = None }

                let newEntries =
                  node.Entries
                  |> List.mapi (fun idx x -> if idx = nextTreeIdx then newPrevE else x)

                return Some { node with Entries = newEntries }
            | Some newChild ->
              let! newChildCid = persister newChild

              if nextTreeIdx = -1 then
                return Some { node with Left = Some newChildCid }
              else
                let prevE = node.Entries.[nextTreeIdx]
                let newPrevE = { prevE with Tree = Some newChildCid }

                let newEntries =
                  node.Entries
                  |> List.mapi (fun idx x -> if idx = nextTreeIdx then newPrevE else x)

                return Some { node with Entries = newEntries }
    }

  let fromEntries (loader : NodeLoader) (persister : NodePersister) (entries : (string * Cid) list) : Async<MstNode> = async {
    let mutable root = { Left = None; Entries = [] }
    let sortedEntries = entries |> List.sortBy fst

    for k, v in sortedEntries do
      let! newRoot = put loader persister root k v ""
      root <- newRoot

    return root
  }
