<!-- markdownlint-disable MD033 -->
# PDSharp

A Personal Data Server (PDS) for the AT Protocol, written in F# with Giraffe.

## Goal

Build and deploy a single-user PDS that can host your AT Protocol repository, serve blobs, and federate with Bluesky.

## Requirements

.NET 9.0 SDK

## Getting Started

### Restore & Build the project

```bash
dotnet restore
dotnet build
```

### Run the tests

```bash
dotnet test
```

### Run the Server

```bash
dotnet run --project PDSharp/PDSharp.fsproj
```

The server will start at `http://localhost:5000`.

## Configuration

The application uses `appsettings.json` and supports Environment Variable overrides.

| Key         | Env Var             | Default                 | Description               |
| ----------- | ------------------- | ----------------------- | ------------------------- |
| `DidHost`   | `PDSHARP_DidHost`   | `did:web:localhost`     | The DID of the PDS itself |
| `PublicUrl` | `PDSHARP_PublicUrl` | `http://localhost:5000` | Publicly reachable URL    |

Example `appsettings.json`:

```json
{
  "PublicUrl": "http://localhost:5000",
  "DidHost": "did:web:localhost"
}
```

## API Testing

<details>
<summary>Server Info</summary>

```bash
curl http://localhost:5000/xrpc/com.atproto.server.describeServer
```

</details>

### Record Operations

<details>
<summary>Create a record</summary>

```bash
curl -X POST http://localhost:5000/xrpc/com.atproto.repo.createRecord \
  -H "Content-Type: application/json" \
  -d '{"repo":"did:web:test","collection":"app.bsky.feed.post","record":{"text":"Hello, ATProto!"}}'
```

</details>

<details>
<summary>Get a record</summary>

```bash
curl "http://localhost:5000/xrpc/com.atproto.repo.getRecord?repo=did:web:test&collection=app.bsky.feed.post&rkey=<RKEY>"
```

</details>

<details>
<summary>Put a record</summary>

```bash
curl -X POST http://localhost:5000/xrpc/com.atproto.repo.putRecord \
  -H "Content-Type: application/json" \
  -d '{"repo":"did:web:test","collection":"app.bsky.feed.post","rkey":"my-post","record":{"text":"Updated!"}}'
```

</details>

### Sync & CAR Export

<details>
<summary>Get entire repository as CAR</summary>

```bash
curl "http://localhost:5000/xrpc/com.atproto.sync.getRepo?did=did:web:test" -o repo.car
```

</details>

<details>
<summary>Get specific blocks</summary>

```bash
curl "http://localhost:5000/xrpc/com.atproto.sync.getBlocks?did=did:web:test&cids=<CID1>,<CID2>" -o blocks.car
```

</details>

<details>
<summary>Get a blob by CID</summary>

```bash
curl "http://localhost:5000/xrpc/com.atproto.sync.getBlob?did=did:web:test&cid=<BLOB_CID>"
```

</details>

### Firehose (WebSocket)

Subscribe to real-time commit events using [websocat](https://github.com/vi/websocat):

<details>
<summary>Open a WebSocket connection</summary>

```bash
websocat ws://localhost:5000/xrpc/com.atproto.sync.subscribeRepos
```

</details>

<br />
Then create/update records in another terminal to see CBOR-encoded commit events stream in real-time.

<br />

<details>
<summary>Open a WebSocket connection with cursor for resumption</summary>

```bash
websocat "ws://localhost:5000/xrpc/com.atproto.sync.subscribeRepos?cursor=5"
```

</details>

## Architecture

<details>
<summary>App (Giraffe)</summary>

- `XrpcRouter`: `/xrpc/<NSID>` routing
- `Auth`: Session management (JWTs)
- `RepoApi`: Write/Read records (`putRecord`, `getRecord`)
- `ServerApi`: Server meta (`describeServer`)

</details>

<details>
<summary>Core (Pure F#)</summary>

- `DidResolver`: Identity resolution
- `RepoEngine`: MST, DAG-CBOR, CIDs, Blocks
- `Models`: Data types for XRPC/Database

</details>

<details>
<summary>Infra</summary>

- SQLite/Postgres for persistence
- S3/Disk for blob storage

</details>
