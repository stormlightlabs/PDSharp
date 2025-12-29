namespace PDSharp.Core

module Config =
  type AppConfig = {
    PublicUrl : string
    DidHost : string
    /// HS256 signing key for session tokens
    JwtSecret : string
  }
