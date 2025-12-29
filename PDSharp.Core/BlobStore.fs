namespace PDSharp.Core

open System.IO
open Amazon.S3
open Amazon.S3.Model
open PDSharp.Core.Config

/// Interface for binary large object (blob) storage
type IBlobStore =
  /// Store a blob by CID
  abstract member Put : Cid * byte[] -> Async<unit>
  /// Retrieve a blob by CID
  abstract member Get : Cid -> Async<byte[] option>
  /// Check if a blob exists (optional optimization)
  abstract member Has : Cid -> Async<bool>
  /// Delete a blob by CID
  abstract member Delete : Cid -> Async<unit>

module BlobStore =

  /// File-system based blob store
  type DiskBlobStore(encodedRootPath : string) =
    let rootPath =
      if Path.IsPathRooted encodedRootPath then
        encodedRootPath
      else
        Path.Combine(Directory.GetCurrentDirectory(), encodedRootPath)

    do
      if not (Directory.Exists rootPath) then
        Directory.CreateDirectory(rootPath) |> ignore

    let getPath (cid : Cid) = Path.Combine(rootPath, cid.ToString())

    interface IBlobStore with
      member _.Put(cid, data) = async {
        let path = getPath cid

        if not (File.Exists path) then
          do! File.WriteAllBytesAsync(path, data) |> Async.AwaitTask
      }

      member _.Get(cid) = async {
        let path = getPath cid

        if File.Exists path then
          let! data = File.ReadAllBytesAsync(path) |> Async.AwaitTask
          return Some data
        else
          return None
      }

      member _.Has(cid) = async { return File.Exists(getPath cid) }

      member _.Delete(cid) = async {
        let path = getPath cid

        if File.Exists path then
          File.Delete path
      }

  /// S3-based blob store
  type S3BlobStore(config : S3Config) =
    let client =
      let clientConfig =
        AmazonS3Config(RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName config.Region)

      match config.ServiceUrl with
      | Some url -> clientConfig.ServiceURL <- url
      | None -> ()

      clientConfig.ForcePathStyle <- config.ForcePathStyle

      match config.AccessKey, config.SecretKey with
      | Some access, Some secret -> new AmazonS3Client(access, secret, clientConfig)
      | _ -> new AmazonS3Client(clientConfig)

    let bucket = config.Bucket

    interface IBlobStore with
      member _.Put(cid, data) = async {
        let request = PutObjectRequest()
        request.BucketName <- bucket
        request.Key <- cid.ToString()
        use ms = new MemoryStream(data)
        request.InputStream <- ms
        let! _ = client.PutObjectAsync(request) |> Async.AwaitTask
        ()
      }

      member _.Get(cid) = async {
        try
          let request = GetObjectRequest()
          request.BucketName <- bucket
          request.Key <- cid.ToString()

          use! response = client.GetObjectAsync(request) |> Async.AwaitTask
          use ms = new MemoryStream()
          do! response.ResponseStream.CopyToAsync(ms) |> Async.AwaitTask
          return Some(ms.ToArray())
        with
        | :? AmazonS3Exception as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound -> return None
        | _ -> return None
      }

      member _.Has(cid) = async {
        try
          let request = GetObjectMetadataRequest()
          request.BucketName <- bucket
          request.Key <- cid.ToString()
          let! _ = client.GetObjectMetadataAsync(request) |> Async.AwaitTask
          return true
        with
        | :? AmazonS3Exception as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound -> return false
        | _ -> return false
      }

      member _.Delete(cid) = async {
        let request = DeleteObjectRequest()
        request.BucketName <- bucket
        request.Key <- cid.ToString()
        let! _ = client.DeleteObjectAsync(request) |> Async.AwaitTask
        ()
      }
