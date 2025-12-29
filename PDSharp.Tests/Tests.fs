module Tests

open System
open Xunit
open PDSharp.Core.Models
open PDSharp.Core.Config

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
