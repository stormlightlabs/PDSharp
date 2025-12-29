namespace PDSharp.Core

open System
open System.Text
open Org.BouncyCastle.Crypto.Digests
open Org.BouncyCastle.Crypto.Macs
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Security

/// Authentication module for sessions and accounts
/// TODO: Migrate account storage from in-memory to SQLite/Postgres for production
module Auth =
  /// Hash a password with a random salt using SHA-256
  ///
  /// Returns: base64(salt)$base64(hash)
  let hashPassword (password : string) : string =
    let salt = Array.zeroCreate<byte> 16
    SecureRandom().NextBytes(salt)

    let passwordBytes = Encoding.UTF8.GetBytes(password)
    let toHash = Array.append salt passwordBytes

    let digest = Sha256Digest()
    digest.BlockUpdate(toHash, 0, toHash.Length)
    let hash = Array.zeroCreate<byte> (digest.GetDigestSize())
    digest.DoFinal(hash, 0) |> ignore

    $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}"

  /// Verify a password against a stored hash
  let verifyPassword (password : string) (storedHash : string) : bool =
    let parts = storedHash.Split('$')

    if parts.Length <> 2 then
      false
    else
      try
        let salt = Convert.FromBase64String(parts.[0])
        let expectedHash = Convert.FromBase64String(parts.[1])

        let passwordBytes = Encoding.UTF8.GetBytes(password)
        let toHash = Array.append salt passwordBytes

        let digest = Sha256Digest()
        digest.BlockUpdate(toHash, 0, toHash.Length)
        let actualHash = Array.zeroCreate<byte> (digest.GetDigestSize())
        digest.DoFinal(actualHash, 0) |> ignore

        actualHash = expectedHash
      with _ ->
        false

  let private base64UrlEncode (bytes : byte[]) : string =
    Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

  let private base64UrlDecode (str : string) : byte[] =
    let padded =
      match str.Length % 4 with
      | 2 -> str + "=="
      | 3 -> str + "="
      | _ -> str

    Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))

  let private hmacSha256 (secret : byte[]) (data : byte[]) : byte[] =
    let hmac = HMac(Sha256Digest())
    hmac.Init(KeyParameter(secret))
    hmac.BlockUpdate(data, 0, data.Length)
    let result = Array.zeroCreate<byte> (hmac.GetMacSize())
    hmac.DoFinal(result, 0) |> ignore
    result

  /// Token type for domain separation per AT Protocol spec
  type TokenType =
    | Access // typ: at+jwt
    | Refresh // typ: refresh+jwt

  /// Create a JWT token
  let createToken (secret : string) (tokenType : TokenType) (did : string) (expiresIn : TimeSpan) : string =
    let now = DateTimeOffset.UtcNow
    let exp = now.Add(expiresIn)

    let typ =
      match tokenType with
      | Access -> "at+jwt"
      | Refresh -> "refresh+jwt"

    let jti = Guid.NewGuid().ToString("N")

    let header = $"""{{ "alg": "HS256", "typ": "{typ}" }}"""
    let headerB64 = base64UrlEncode (Encoding.UTF8.GetBytes(header))

    let payload =
      $"""{{ "sub": "{did}", "iat": {now.ToUnixTimeSeconds()}, "exp": {exp.ToUnixTimeSeconds()}, "jti": "{jti}" }}"""

    let payloadB64 = base64UrlEncode (Encoding.UTF8.GetBytes(payload))

    let signingInput = $"{headerB64}.{payloadB64}"
    let secretBytes = Encoding.UTF8.GetBytes(secret)
    let signature = hmacSha256 secretBytes (Encoding.UTF8.GetBytes(signingInput))
    let signatureB64 = base64UrlEncode signature

    $"{headerB64}.{payloadB64}.{signatureB64}"

  /// Create an access token (short-lived)
  let createAccessToken (secret : string) (did : string) : string =
    createToken secret Access did (TimeSpan.FromMinutes(15.0))

  /// Create a refresh token (longer-lived)
  let createRefreshToken (secret : string) (did : string) : string =
    createToken secret Refresh did (TimeSpan.FromDays(7.0))

  /// Validation result
  type TokenValidation =
    | Valid of did : string * tokenType : TokenType * exp : int64
    | Invalid of reason : string

  /// Validate a JWT token and extract claims
  let validateToken (secret : string) (token : string) : TokenValidation =
    let parts = token.Split('.')

    if parts.Length <> 3 then
      Invalid "Invalid token format"
    else
      try
        let headerB64, payloadB64, signatureB64 = parts.[0], parts.[1], parts.[2]

        let signingInput = $"{headerB64}.{payloadB64}"
        let secretBytes = Encoding.UTF8.GetBytes(secret)
        let expectedSig = hmacSha256 secretBytes (Encoding.UTF8.GetBytes(signingInput))
        let actualSig = base64UrlDecode signatureB64

        if expectedSig <> actualSig then
          Invalid "Invalid signature"
        else
          let payloadJson = Encoding.UTF8.GetString(base64UrlDecode payloadB64)
          let headerJson = Encoding.UTF8.GetString(base64UrlDecode headerB64)

          let typMatch =
            System.Text.RegularExpressions.Regex.Match(headerJson, "\"typ\"\\s*:\\s*\"([^\"]+)\"")

          let tokenType =
            if typMatch.Success then
              match typMatch.Groups.[1].Value with
              | "at+jwt" -> Access
              | "refresh+jwt" -> Refresh
              | _ -> Access
            else
              Access

          let subMatch =
            System.Text.RegularExpressions.Regex.Match(payloadJson, "\"sub\"\\s*:\\s*\"([^\"]+)\"")

          let expMatch =
            System.Text.RegularExpressions.Regex.Match(payloadJson, "\"exp\"\\s*:\\s*([0-9]+)")

          if not subMatch.Success || not expMatch.Success then
            Invalid "Missing claims"
          else
            let did = subMatch.Groups.[1].Value
            let exp = Int64.Parse(expMatch.Groups.[1].Value)
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

            if now > exp then
              Invalid "Token expired"
            else
              Valid(did, tokenType, exp)
      with ex ->
        Invalid $"Parse error: {ex.Message}"

  /// Account record
  type Account = {
    Did : string
    Handle : string
    PasswordHash : string
    Email : string option
    CreatedAt : DateTimeOffset
  }

  let mutable private accounts : Map<string, Account> = Map.empty
  let mutable private handleIndex : Map<string, string> = Map.empty

  let createAccount (handle : string) (password : string) (email : string option) : Result<Account, string> =
    if Map.containsKey handle handleIndex then
      Error "Handle already taken"
    else
      let did = $"did:web:{handle}"

      if Map.containsKey did accounts then
        Error "Account already exists"
      else
        let account = {
          Did = did
          Handle = handle
          PasswordHash = hashPassword password
          Email = email
          CreatedAt = DateTimeOffset.UtcNow
        }

        accounts <- Map.add did account accounts
        handleIndex <- Map.add handle did handleIndex
        Ok account

  /// Get account by handle
  let getAccountByHandle (handle : string) : Account option =
    handleIndex
    |> Map.tryFind handle
    |> Option.bind (fun did -> Map.tryFind did accounts)

  /// Get account by DID
  let getAccountByDid (did : string) : Account option = Map.tryFind did accounts

  /// Clear all accounts (for testing)
  let resetAccounts () =
    accounts <- Map.empty
    handleIndex <- Map.empty
