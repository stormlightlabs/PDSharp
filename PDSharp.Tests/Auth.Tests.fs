module Auth.Tests

open Xunit
open PDSharp.Core.Auth

[<Fact>]
let ``Password hashing produces salt$hash format`` () =
  let hash = hashPassword "mypassword"
  Assert.Contains("$", hash)
  let parts = hash.Split('$')
  Assert.Equal(2, parts.Length)

[<Fact>]
let ``Password verification succeeds for correct password`` () =
  let hash = hashPassword "mypassword"
  Assert.True(verifyPassword "mypassword" hash)

[<Fact>]
let ``Password verification fails for wrong password`` () =
  let hash = hashPassword "mypassword"
  Assert.False(verifyPassword "wrongpassword" hash)

[<Fact>]
let ``Password verification fails for invalid hash format`` () =
  Assert.False(verifyPassword "password" "invalidhash")
  Assert.False(verifyPassword "password" "")

[<Fact>]
let ``JWT access token creation and validation`` () =
  let secret = "test-secret-key-minimum-32-chars!"
  let did = "did:web:test.example"

  let token = createAccessToken secret did

  let parts = token.Split('.')
  Assert.Equal(3, parts.Length)

  match validateToken secret token with
  | Valid(extractedDid, tokenType, _) ->
    Assert.Equal(did, extractedDid)
    Assert.Equal(Access, tokenType)
  | Invalid reason -> Assert.Fail $"Token should be valid, got: {reason}"

[<Fact>]
let ``JWT refresh token has correct type`` () =
  let secret = "test-secret-key-minimum-32-chars!"
  let did = "did:web:test.example"

  let token = createRefreshToken secret did

  match validateToken secret token with
  | Valid(_, tokenType, _) -> Assert.Equal(Refresh, tokenType)
  | Invalid reason -> Assert.Fail $"Token should be valid, got: {reason}"

[<Fact>]
let ``JWT validation fails with wrong secret`` () =
  let secret = "test-secret-key-minimum-32-chars!"
  let wrongSecret = "wrong-secret-key-minimum-32-chars!"
  let did = "did:web:test.example"

  let token = createAccessToken secret did

  match validateToken wrongSecret token with
  | Invalid _ -> Assert.True(true)
  | Valid _ -> Assert.Fail "Token should be invalid with wrong secret"

[<Fact>]
let ``Account creation and lookup by handle`` () =
  resetAccounts ()

  match createAccount "test.user" "password123" (Some "test@example.com") with
  | Error msg -> Assert.Fail msg
  | Ok account ->
    Assert.Equal("test.user", account.Handle)
    Assert.Equal("did:web:test.user", account.Did)
    Assert.Equal(Some "test@example.com", account.Email)

    match getAccountByHandle "test.user" with
    | None -> Assert.Fail "Account should be found"
    | Some found -> Assert.Equal(account.Did, found.Did)

[<Fact>]
let ``Account creation fails for duplicate handle`` () =
  resetAccounts ()

  createAccount "duplicate.user" "password" None |> ignore

  match createAccount "duplicate.user" "password2" None with
  | Error msg -> Assert.Contains("already", msg.ToLower())
  | Ok _ -> Assert.Fail "Should fail for duplicate handle"

[<Fact>]
let ``Account lookup by DID`` () =
  resetAccounts ()

  match createAccount "did.user" "password123" None with
  | Error msg -> Assert.Fail msg
  | Ok account ->
    match getAccountByDid account.Did with
    | None -> Assert.Fail "Account should be found by DID"
    | Some found -> Assert.Equal(account.Handle, found.Handle)
