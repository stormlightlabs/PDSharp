namespace PDSharp.Core

open System
open System.Text.RegularExpressions

/// AT-URI parsing and validation
module AtUri =
  /// Represents an AT Protocol URI: at://did/collection/rkey
  type AtUri = { Did : string; Collection : string; Rkey : string }

  let private didPattern = @"^did:[a-z]+:[a-zA-Z0-9._:%-]+$"
  let private nsidPattern = @"^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$"
  let private rkeyPattern = @"^[a-zA-Z0-9._~-]+$"

  /// Parse an AT-URI string into components
  let parse (uri : string) : Result<AtUri, string> =
    if not (uri.StartsWith("at://")) then
      Error "AT-URI must start with at://"
    else
      let path = uri.Substring(5)
      let parts = path.Split('/')

      if parts.Length < 3 then
        Error "AT-URI must have format at://did/collection/rkey"
      else
        let did = parts.[0]
        let collection = parts.[1]
        let rkey = parts.[2]

        if not (Regex.IsMatch(did, didPattern)) then
          Error $"Invalid DID format: {did}"
        elif not (Regex.IsMatch(collection, nsidPattern)) then
          Error $"Invalid collection NSID: {collection}"
        elif not (Regex.IsMatch(rkey, rkeyPattern)) then
          Error $"Invalid rkey format: {rkey}"
        else
          Ok { Did = did; Collection = collection; Rkey = rkey }

  /// Convert AtUri back to string
  let toString (uri : AtUri) : string =
    $"at://{uri.Did}/{uri.Collection}/{uri.Rkey}"
