namespace PDSharp.Core

open System
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization

module DidResolver =
  type VerificationMethod = {
    [<JsonPropertyName("id")>]
    Id : string
    [<JsonPropertyName("type")>]
    Type : string
    [<JsonPropertyName("controller")>]
    Controller : string
    [<JsonPropertyName("publicKeyMultibase")>]
    PublicKeyMultibase : string option
  }

  type DidDocument = {
    [<JsonPropertyName("id")>]
    Id : string
    [<JsonPropertyName("verificationMethod")>]
    VerificationMethod : VerificationMethod list
  }

  let private httpClient = new HttpClient()

  let private fetchJson<'T> (url : string) : Async<'T option> = async {
    try
      let! response = httpClient.GetAsync url |> Async.AwaitTask

      if response.IsSuccessStatusCode then
        let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        let! doc = JsonSerializer.DeserializeAsync<'T>(stream, options).AsTask() |> Async.AwaitTask
        return Some doc
      else
        return None
    with _ ->
      return None
  }

  let resolveDidWeb (did : string) : Async<DidDocument option> = async {
    let parts = did.Split(':')

    if parts.Length < 3 then
      return None
    else
      let domain = parts.[2]

      let url =
        if domain = "localhost" then
          "http://localhost:5000/.well-known/did.json"
        else
          $"https://{domain}/.well-known/did.json"

      return! fetchJson<DidDocument> url
  }

  let resolveDidPlc (did : string) : Async<DidDocument option> = async {
    let url = $"https://plc.directory/{did}"
    return! fetchJson<DidDocument> url
  }

  let resolve (did : string) : Async<DidDocument option> = async {
    if did.StartsWith("did:web:") then
      return! resolveDidWeb did
    elif did.StartsWith("did:plc:") then
      return! resolveDidPlc did
    else
      return None
  }
