namespace PDSharp.Core

module Config =
  type AppConfig = {
    PublicUrl : string
    DidHost : string
    /// HS256 signing key for session tokens
    JwtSecret : string
    /// Connection string for SQLite
    SqliteConnectionString : string
    /// Disable SQLite WAL auto-checkpoint (for Litestream compatibility)
    DisableWalAutoCheckpoint : bool
    /// Blob storage configuration
    BlobStore : BlobStoreConfig
  }

  and BlobStoreConfig =
    | Disk of path : string
    | S3 of S3Config

  and S3Config = {
    Bucket : string
    Region : string
    AccessKey : string option
    SecretKey : string option
    ServiceUrl : string option
    ForcePathStyle : bool
  }
