module PDSharp.Tests.Conformance

open Xunit
open System
open System.Text.Json
open PDSharp.Core

module LexiconTests =

  let parse (json : string) =
    JsonSerializer.Deserialize<JsonElement>(json)

  [<Fact>]
  let ``Valid Post passes validation`` () =
    let json =
      """{
            "$type": "app.bsky.feed.post",
            "text": "Hello, world!",
            "createdAt": "2023-10-27T10:00:00Z"
        }"""

    let element = parse json
    let result = Lexicon.validate "app.bsky.feed.post" element
    Assert.Equal(Lexicon.Ok, result)

  [<Fact>]
  let ``Post missing text fails validation`` () =
    let json =
      """{
            "$type": "app.bsky.feed.post",
            "createdAt": "2023-10-27T10:00:00Z"
        }"""

    let element = parse json
    let result = Lexicon.validate "app.bsky.feed.post" element

    match result with
    | Lexicon.Error msg -> Assert.Contains("text", msg)
    | _ -> Assert.Fail("Should have failed validation")

  [<Fact>]
  let ``Post text too long passes validation`` () =
    let longText = String('a', 3001)

    let template =
      """{
            "$type": "app.bsky.feed.post",
            "text": "TEXT_PLACEHOLDER",
            "createdAt": "2023-10-27T10:00:00Z"
        }"""

    let json = template.Replace("TEXT_PLACEHOLDER", longText)
    let element = parse json
    let result = Lexicon.validate "app.bsky.feed.post" element

    match result with
    | Lexicon.Error msg -> Assert.Contains("exceeds maximum length", msg)
    | _ -> Assert.Fail("Should have failed validation")

  [<Fact>]
  let ``Valid Like passes validation`` () =
    let json =
      """{
            "$type": "app.bsky.feed.like",
            "subject": {
                "uri": "at://did:plc:123/app.bsky.feed.post/3k5",
                "cid": "bafyreih..."
            },
            "createdAt": "2023-10-27T10:00:00Z"
        }"""

    let element = parse json
    let result = Lexicon.validate "app.bsky.feed.like" element
    Assert.Equal(Lexicon.Ok, result)

  [<Fact>]
  let ``Like missing subject fails validation`` () =
    let json =
      """{
            "$type": "app.bsky.feed.like",
            "createdAt": "2023-10-27T10:00:00Z"
        }"""

    let element = parse json
    let result = Lexicon.validate "app.bsky.feed.like" element

    match result with
    | Lexicon.Error msg -> Assert.Contains("subject", msg)
    | _ -> Assert.Fail("Should have failed validation")

  [<Fact>]
  let ``Like subject missing uri passes validation (should fail)`` () =
    let json =
      """{
            "$type": "app.bsky.feed.like",
            "subject": {
                "cid": "bafyreih..."
            },
            "createdAt": "2023-10-27T10:00:00Z"
        }"""

    let element = parse json
    let result = Lexicon.validate "app.bsky.feed.like" element

    match result with
    | Lexicon.Error msg -> Assert.Contains("uri", msg)
    | _ -> Assert.Fail("Should have failed validation")

  [<Fact>]
  let ``Valid Profile passes validation`` () =
    let json =
      """{
            "$type": "app.bsky.actor.profile",
            "displayName": "Alice",
            "description": "Bob's friend"
        }"""

    let element = parse json
    let result = Lexicon.validate "app.bsky.actor.profile" element
    Assert.Equal(Lexicon.Ok, result)

  [<Fact>]
  let ``Profile description check length`` () =
    let longDesc = String('a', 2561)

    let template =
      """{
            "$type": "app.bsky.actor.profile",
            "description": "DESC_PLACEHOLDER"
        }"""

    let json = template.Replace("DESC_PLACEHOLDER", longDesc)
    let element = parse json
    let result = Lexicon.validate "app.bsky.actor.profile" element

    match result with
    | Lexicon.Error msg -> Assert.Contains("exceeds maximum length", msg)
    | _ -> Assert.Fail("Should have failed validation")

  [<Fact>]
  let ``Unknown type validation is lax`` () =
    let json = """{ "random": "stuff" }"""
    let element = parse json
    let result = Lexicon.validate "com.unknown.record" element
    Assert.Equal(Lexicon.Ok, result)
