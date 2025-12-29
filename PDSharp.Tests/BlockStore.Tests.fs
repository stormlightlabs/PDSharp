module BlockStoreTests

open Xunit
open PDSharp.Core
open PDSharp.Core.BlockStore

[<Fact>]
let ``MemoryBlockStore Put and Get roundtrip`` () =
  let store = MemoryBlockStore() :> IBlockStore
  let data = System.Text.Encoding.UTF8.GetBytes("hello world")

  let cid = store.Put(data) |> Async.RunSynchronously
  let retrieved = store.Get(cid) |> Async.RunSynchronously

  Assert.True(Option.isSome retrieved)
  Assert.Equal<byte[]>(data, Option.get retrieved)

[<Fact>]
let ``MemoryBlockStore Has returns true for existing`` () =
  let store = MemoryBlockStore() :> IBlockStore
  let data = System.Text.Encoding.UTF8.GetBytes("test data")

  let cid = store.Put(data) |> Async.RunSynchronously
  let exists = store.Has(cid) |> Async.RunSynchronously

  Assert.True(exists)

[<Fact>]
let ``MemoryBlockStore Has returns false for missing`` () =
  let store = MemoryBlockStore() :> IBlockStore
  let fakeCid = Cid.FromHash(Crypto.sha256Str "nonexistent")

  let exists = store.Has(fakeCid) |> Async.RunSynchronously

  Assert.False(exists)

[<Fact>]
let ``MemoryBlockStore Get returns None for missing`` () =
  let store = MemoryBlockStore() :> IBlockStore
  let fakeCid = Cid.FromHash(Crypto.sha256Str "nonexistent")

  let result = store.Get(fakeCid) |> Async.RunSynchronously

  Assert.True(Option.isNone result)

[<Fact>]
let ``MemoryBlockStore CID is content-addressed`` () =
  let store = MemoryBlockStore() :> IBlockStore
  let data = System.Text.Encoding.UTF8.GetBytes("same content")
  let cid1 = store.Put data |> Async.RunSynchronously
  let cid2 = store.Put data |> Async.RunSynchronously
  Assert.Equal<byte[]>(cid1.Bytes, cid2.Bytes)
