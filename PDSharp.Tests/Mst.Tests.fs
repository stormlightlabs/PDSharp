module MstTests

open Xunit
open PDSharp.Core
open PDSharp.Core.Mst

[<Fact>]
let ``Serialization Roundtrip`` () =
  let cid1 = Cid(Crypto.sha256Str "val1")

  let e1 = {
    PrefixLen = 0
    KeySuffix = "apple"
    Value = cid1
    Tree = None
  }

  let e2 = {
    PrefixLen = 2
    KeySuffix = "ricot"
    Value = cid1
    Tree = None
  }

  let node = { Left = None; Entries = [ e1; e2 ] }

  let bytes = Mst.serialize node
  let node2 = Mst.deserialize bytes

  Assert.Equal(node.Entries.Length, node2.Entries.Length)
  Assert.Equal("apple", node2.Entries.[0].KeySuffix)
  Assert.Equal("ricot", node2.Entries.[1].KeySuffix)
  Assert.Equal(2, node2.Entries.[1].PrefixLen)

[<Fact>]
let ``Get Operation Linear Scan`` () =
  let cid1 = Cid(Crypto.sha256Str "val1")
  let cid2 = Cid(Crypto.sha256Str "val2")

  let e1 = {
    PrefixLen = 0
    KeySuffix = "apple"
    Value = cid1
    Tree = None
  }

  let e2 = {
    PrefixLen = 0
    KeySuffix = "banana"
    Value = cid2
    Tree = None
  }

  let node = { Left = None; Entries = [ e1; e2 ] }

  let loader (c : Cid) = async { return None }

  let res1 = Mst.get loader node "apple" "" |> Async.RunSynchronously
  Assert.Equal(Some cid1, res1)

  let res2 = Mst.get loader node "banana" "" |> Async.RunSynchronously
  Assert.Equal(Some cid2, res2)

  let res3 = Mst.get loader node "cherry" "" |> Async.RunSynchronously
  Assert.True(Option.isNone res3)

[<Fact>]
let ``Get Operation With Prefix Compression`` () =
  let cid1 = Cid(Crypto.sha256Str "val1")
  let cid2 = Cid(Crypto.sha256Str "val2")

  let e1 = {
    PrefixLen = 0
    KeySuffix = "apple"
    Value = cid1
    Tree = None
  }

  let e2 = {
    PrefixLen = 2
    KeySuffix = "ricot"
    Value = cid2
    Tree = None
  }

  let node = { Left = None; Entries = [ e1; e2 ] }
  let loader (c : Cid) = async { return None }

  let res1 = Mst.get loader node "apricot" "" |> Async.RunSynchronously
  Assert.Equal(Some cid2, res1)

[<Fact>]
let ``Put Operation Simple Insert`` () =
  let store = System.Collections.Concurrent.ConcurrentDictionary<string, MstNode>()

  let loader (c : Cid) = async {
    let key = System.Convert.ToBase64String(c.Bytes)
    let success, node = store.TryGetValue(key)
    return if success then Some node else None
  }

  let persister (n : MstNode) = async {
    let bytes = Mst.serialize n
    let cid = Cid(Crypto.sha256 bytes)
    let key = System.Convert.ToBase64String(cid.Bytes)
    store.[key] <- n
    return cid
  }

  let node = { Left = None; Entries = [] }
  let cid1 = Cid(Crypto.sha256Str "v1")
  let node2 = Mst.put loader persister node "apple" cid1 "" |> Async.RunSynchronously

  Assert.Equal(1, node2.Entries.Length)
  Assert.Equal("apple", node2.Entries.[0].KeySuffix)
  Assert.Equal(0, node2.Entries.[0].PrefixLen)
  Assert.Equal(cid1, node2.Entries.[0].Value)

  let res = Mst.get loader node2 "apple" "" |> Async.RunSynchronously
  Assert.Equal(Some cid1, res)

[<Fact>]
let ``Put Operation Multiple Sorted`` () =
  let store = System.Collections.Concurrent.ConcurrentDictionary<string, MstNode>()

  let loader (c : Cid) = async {
    let key = System.Convert.ToBase64String(c.Bytes)
    let success, node = store.TryGetValue(key)
    return if success then Some node else None
  }

  let persister (n : MstNode) = async {
    let bytes = Mst.serialize n
    let cid = Cid(Crypto.sha256 bytes)
    let key = System.Convert.ToBase64String(cid.Bytes)
    store.[key] <- n
    return cid
  }

  let mutable node = { Left = None; Entries = [] }

  let k1, v1 = "apple", Cid(Crypto.sha256Str "1")
  let k2, v2 = "banana", Cid(Crypto.sha256Str "2")
  let k3, v3 = "cherry", Cid(Crypto.sha256Str "3")

  node <- Mst.put loader persister node k1 v1 "" |> Async.RunSynchronously
  node <- Mst.put loader persister node k2 v2 "" |> Async.RunSynchronously
  node <- Mst.put loader persister node k3 v3 "" |> Async.RunSynchronously

  let g1 = Mst.get loader node "apple" "" |> Async.RunSynchronously
  let g2 = Mst.get loader node "banana" "" |> Async.RunSynchronously
  let g3 = Mst.get loader node "cherry" "" |> Async.RunSynchronously

  Assert.Equal(Some v1, g1)
  Assert.Equal(Some v2, g2)
  Assert.Equal(Some v3, g3)

[<Fact>]
let ``Put Operation Multiple Reverse`` () =
  let store = System.Collections.Concurrent.ConcurrentDictionary<string, MstNode>()

  let loader (c : Cid) = async {
    let key = System.Convert.ToBase64String(c.Bytes)
    let success, node = store.TryGetValue(key)
    return if success then Some node else None
  }

  let persister (n : MstNode) = async {
    let bytes = Mst.serialize n
    let cid = Cid(Crypto.sha256 bytes)
    let key = System.Convert.ToBase64String(cid.Bytes)
    store.[key] <- n
    return cid
  }

  let mutable node = { Left = None; Entries = [] }

  let data = [ "zebra"; "yak"; "xylophone" ]

  for k in data do
    let v = Cid(Crypto.sha256Str k)
    node <- Mst.put loader persister node k v "" |> Async.RunSynchronously

  for k in data do
    let expected = Cid(Crypto.sha256Str k)
    let actual = Mst.get loader node k "" |> Async.RunSynchronously
    Assert.Equal(Some expected, actual)

[<Fact>]
let ``Delete Operation Simple`` () =
  let store = System.Collections.Concurrent.ConcurrentDictionary<string, MstNode>()

  let loader (c : Cid) = async {
    let key = System.Convert.ToBase64String(c.Bytes)
    let success, node = store.TryGetValue(key)
    return if success then Some node else None
  }

  let persister (n : MstNode) = async {
    let bytes = Mst.serialize n
    let cid = Cid(Crypto.sha256 bytes)
    let key = System.Convert.ToBase64String(cid.Bytes)
    store.[key] <- n
    return cid
  }

  let mutable node = { Left = None; Entries = [] }
  let cid1 = Cid(Crypto.sha256Str "val1")

  node <- Mst.put loader persister node "apple" cid1 "" |> Async.RunSynchronously

  let res1 = Mst.get loader node "apple" "" |> Async.RunSynchronously
  Assert.Equal(Some cid1, res1)

  // Delete
  let nodeOpt = Mst.delete loader persister node "apple" "" |> Async.RunSynchronously

  match nodeOpt with
  | None -> ()
  | Some n ->
    let res2 = Mst.get loader n "apple" "" |> Async.RunSynchronously
    Assert.True(Option.isNone res2)

[<Fact>]
let ``Determinism From Entries`` () =
  let store = System.Collections.Concurrent.ConcurrentDictionary<string, MstNode>()

  let loader (c : Cid) = async {
    let key = System.Convert.ToBase64String(c.Bytes)
    let success, node = store.TryGetValue(key)
    return if success then Some node else None
  }

  let persister (n : MstNode) = async {
    let bytes = Mst.serialize n
    let cid = Cid(Crypto.sha256 bytes)
    let key = System.Convert.ToBase64String(cid.Bytes)
    store.[key] <- n
    return cid
  }

  let data = [
    "apple", Cid(Crypto.sha256Str "1")
    "banana", Cid(Crypto.sha256Str "2")
    "cherry", Cid(Crypto.sha256Str "3")
    "date", Cid(Crypto.sha256Str "4")
    "elderberry", Cid(Crypto.sha256Str "5")
  ]


  let node1 = Mst.fromEntries loader persister data |> Async.RunSynchronously
  let cid1 = persister node1 |> Async.RunSynchronously

  let node2 =
    Mst.fromEntries loader persister (List.rev data) |> Async.RunSynchronously

  let cid2 = persister node2 |> Async.RunSynchronously
  let data3 = [ data.[2]; data.[0]; data.[4]; data.[1]; data.[3] ]
  let node3 = Mst.fromEntries loader persister data3 |> Async.RunSynchronously
  let cid3 = persister node3 |> Async.RunSynchronously

  Assert.Equal(cid1, cid2)
  Assert.Equal(cid1, cid3)
  ()
