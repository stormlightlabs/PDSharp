module AtUriTests

open Xunit
open PDSharp.Core.AtUri

[<Fact>]
let ``Parse valid AT-URI`` () =
  let uri = "at://did:plc:abcd1234/app.bsky.feed.post/3kbq5vk4beg2f"
  let result = parse uri

  match result with
  | Ok parsed ->
    Assert.Equal("did:plc:abcd1234", parsed.Did)
    Assert.Equal("app.bsky.feed.post", parsed.Collection)
    Assert.Equal("3kbq5vk4beg2f", parsed.Rkey)
  | Error msg -> Assert.Fail(msg)

[<Fact>]
let ``Parse did:web AT-URI`` () =
  let uri = "at://did:web:example.com/app.bsky.actor.profile/self"
  let result = parse uri

  match result with
  | Ok parsed ->
    Assert.Equal("did:web:example.com", parsed.Did)
    Assert.Equal("app.bsky.actor.profile", parsed.Collection)
    Assert.Equal("self", parsed.Rkey)
  | Error msg -> Assert.Fail(msg)

[<Fact>]
let ``Parse fails without at:// prefix`` () =
  let uri = "http://did:plc:abcd/app.bsky.feed.post/123"
  let result = parse uri

  Assert.True(Result.isError result)

[<Fact>]
let ``Parse fails with invalid DID`` () =
  let uri = "at://not-a-did/app.bsky.feed.post/123"
  let result = parse uri

  Assert.True(Result.isError result)

[<Fact>]
let ``Parse fails with invalid collection`` () =
  let uri = "at://did:plc:abcd/NotAnNsid/123"
  let result = parse uri

  Assert.True(Result.isError result)

[<Fact>]
let ``Parse fails with missing parts`` () =
  let uri = "at://did:plc:abcd/app.bsky.feed.post"
  let result = parse uri

  Assert.True(Result.isError result)

[<Fact>]
let ``ToString roundtrip`` () =
  let original = {
    Did = "did:plc:abcd"
    Collection = "app.bsky.feed.post"
    Rkey = "123"
  }

  let str = toString original
  let parsed = parse str

  match parsed with
  | Ok p ->
    Assert.Equal(original.Did, p.Did)
    Assert.Equal(original.Collection, p.Collection)
    Assert.Equal(original.Rkey, p.Rkey)
  | Error msg -> Assert.Fail(msg)
