namespace PDSharp.Core

open System
open System.Text.Json

module Lexicon =
  type LexiconResult =
    | Ok
    | Error of string

  module Validation =
    let private getProperty (p : string) (element : JsonElement) =
      match element.TryGetProperty(p) with
      | true, prop -> Some prop
      | _ -> None

    let private getString (p : string) (element : JsonElement) =
      match getProperty p element with
      | Some prop when prop.ValueKind = JsonValueKind.String -> Some(prop.GetString())
      | _ -> None

    let private validateStringField
      (element : JsonElement)
      (fieldName : string)
      (maxLength : int option)
      (required : bool)
      : LexiconResult =
      match getProperty fieldName element with
      | Some prop ->
        if prop.ValueKind <> JsonValueKind.String then
          Error $"Field '{fieldName}' must be a string"
        else
          match maxLength with
          | Some maxLen when prop.GetString().Length > maxLen ->
            Error $"Field '{fieldName}' exceeds maximum length of {maxLen}"
          | _ -> Ok
      | None ->
        if required then
          Error $"Missing required field '{fieldName}'"
        else
          Ok

    let private validateIsoDate (element : JsonElement) (fieldName : string) (required : bool) : LexiconResult =
      match getProperty fieldName element with
      | Some prop ->
        if prop.ValueKind <> JsonValueKind.String then
          Error $"Field '{fieldName}' must be a string"
        else
          let s = prop.GetString()
          let mutable dt = DateTimeOffset.MinValue

          if DateTimeOffset.TryParse(s, &dt) then
            Ok
          else
            Error $"Field '{fieldName}' must be a valid ISO 8601 date string"
      | None ->
        if required then
          Error $"Missing required field '{fieldName}'"
        else
          Ok

    let private validateRef (element : JsonElement) (fieldName : string) (required : bool) : LexiconResult =
      match getProperty fieldName element with
      | Some prop ->
        if prop.ValueKind <> JsonValueKind.Object then
          Error $"Field '{fieldName}' must be an object"
        else
          match validateStringField prop "uri" None true, validateStringField prop "cid" None true with
          | Ok, Ok -> Ok
          | Error e, _ -> Error $"Field '{fieldName}': {e}"
          | _, Error e -> Error $"Field '{fieldName}': {e}"
      | None ->
        if required then
          Error $"Missing required field '{fieldName}'"
        else
          Ok

    let validatePost (record : JsonElement) : LexiconResult =
      let textCheck = validateStringField record "text" (Some 3000) true
      let dateCheck = validateIsoDate record "createdAt" true

      match textCheck, dateCheck with
      | Ok, Ok -> Ok
      | Error e, _ -> Error e
      | _, Error e -> Error e

    let validateLike (record : JsonElement) : LexiconResult =
      let subjectCheck = validateRef record "subject" true
      let dateCheck = validateIsoDate record "createdAt" true

      match subjectCheck, dateCheck with
      | Ok, Ok -> Ok
      | Error e, _ -> Error e
      | _, Error e -> Error e

    let validateRepost (record : JsonElement) : LexiconResult =
      let subjectCheck = validateRef record "subject" true
      let dateCheck = validateIsoDate record "createdAt" true

      match subjectCheck, dateCheck with
      | Ok, Ok -> Ok
      | Error e, _ -> Error e
      | _, Error e -> Error e

    let validateFollow (record : JsonElement) : LexiconResult =
      let subjectCheck = validateStringField record "subject" None true
      let dateCheck = validateIsoDate record "createdAt" true

      match subjectCheck, dateCheck with
      | Ok, Ok -> Ok
      | Error e, _ -> Error e
      | _, Error e -> Error e

    let validateProfile (record : JsonElement) : LexiconResult =
      let nameCheck = validateStringField record "displayName" (Some 640) false
      let descCheck = validateStringField record "description" (Some 2560) false

      match nameCheck, descCheck with
      | Ok, Ok -> Ok
      | Error e, _ -> Error e
      | _, Error e -> Error e

  /// Unknown records are valid but unvalidated.
  let validate (collection : string) (record : JsonElement) : LexiconResult =
    match collection with
    | "app.bsky.feed.post" -> Validation.validatePost record
    | "app.bsky.feed.like" -> Validation.validateLike record
    | "app.bsky.feed.repost" -> Validation.validateRepost record
    | "app.bsky.graph.follow" -> Validation.validateFollow record
    | "app.bsky.actor.profile" -> Validation.validateProfile record
    | _ -> Ok
