module Auth.Tests

open Xunit
open PDSharp.Core.Auth
open System
open System.Collections.Concurrent

/// Mock in-memory store for testing
type VolatileAccountStore() =
  let accounts = ConcurrentDictionary<string, Account>()
  let handles = ConcurrentDictionary<string, string>()

  interface IAccountStore with
    member _.CreateAccount(account : Account) = async {
      if handles.ContainsKey(account.Handle) then
        return Error "Handle already taken"
      elif accounts.ContainsKey(account.Did) then
        return Error "Account already exists"
      else
        accounts.TryAdd(account.Did, account) |> ignore
        handles.TryAdd(account.Handle, account.Did) |> ignore
        return Ok()
    }

    member _.GetAccountByHandle(handle : string) = async {
      match handles.TryGetValue(handle) with
      | true, did ->
        match accounts.TryGetValue(did) with
        | true, acc -> return Some acc
        | _ -> return None
      | _ -> return None
    }

    member _.GetAccountByDid(did : string) = async {
      match accounts.TryGetValue(did) with
      | true, acc -> return Some acc
      | _ -> return None
    }

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
  let store = VolatileAccountStore()

  match
    createAccount store "test.user" "password123" (Some "test@example.com")
    |> Async.RunSynchronously
  with
  | Error msg -> Assert.Fail msg
  | Ok account ->
    Assert.Equal("test.user", account.Handle)
    Assert.Equal("did:web:test.user", account.Did)
    Assert.Equal(Some "test@example.com", account.Email)

    let found =
      (store :> IAccountStore).GetAccountByHandle "test.user"
      |> Async.RunSynchronously

    match found with
    | None -> Assert.Fail "Account should be found"
    | Some foundAcc -> Assert.Equal(account.Did, foundAcc.Did)

[<Fact>]
let ``Account creation fails for duplicate handle`` () =
  let store = VolatileAccountStore()

  createAccount store "duplicate.user" "password" None
  |> Async.RunSynchronously
  |> ignore

  match createAccount store "duplicate.user" "password2" None |> Async.RunSynchronously with
  | Error msg -> Assert.Contains("already", msg.ToLower())
  | Ok _ -> Assert.Fail "Should fail for duplicate handle"

[<Fact>]
let ``Account lookup by DID`` () =
  let store = VolatileAccountStore()

  match createAccount store "did.user" "password123" None |> Async.RunSynchronously with
  | Error msg -> Assert.Fail msg
  | Ok account ->
    let found =
      (store :> IAccountStore).GetAccountByDid account.Did |> Async.RunSynchronously

    match found with
    | None -> Assert.Fail "Account should be found by DID"
    | Some foundAcc -> Assert.Equal(account.Handle, foundAcc.Handle)
