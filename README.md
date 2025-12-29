# PDSharp

> A Personal Data Server (PDS) for the AT Protocol, written in F# with Giraffe.

## Goal

Build and deploy a single-user PDS that can host your AT Protocol repository, serve blobs, and federate with Bluesky.

## Requirements

- .NET 9.0 SDK
- [Just](https://github.com/casey/just) (optional, for potential future task running)

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

## API Testing

### Server Info

```bash
curl http://localhost:5000/xrpc/com.atproto.server.describeServer
```

### Record Operations

**Create a record:**

```bash
curl -X POST http://localhost:5000/xrpc/com.atproto.repo.createRecord \
  -H "Content-Type: application/json" \
  -d '{"repo":"did:web:test","collection":"app.bsky.feed.post","record":{"text":"Hello, ATProto!"}}'
```

**Get a record** (use the rkey from createRecord response):

```bash
curl "http://localhost:5000/xrpc/com.atproto.repo.getRecord?repo=did:web:test&collection=app.bsky.feed.post&rkey=<RKEY>"
```

**Put a record** (upsert with explicit rkey):

```bash
curl -X POST http://localhost:5000/xrpc/com.atproto.repo.putRecord \
  -H "Content-Type: application/json" \
  -d '{"repo":"did:web:test","collection":"app.bsky.feed.post","rkey":"my-post","record":{"text":"Updated!"}}'
```

### Sync & CAR Export

**Get entire repository as CAR:**

```bash
curl "http://localhost:5000/xrpc/com.atproto.sync.getRepo?did=did:web:test" -o repo.car
```

**Get specific blocks** (comma-separated CIDs):

```bash
curl "http://localhost:5000/xrpc/com.atproto.sync.getBlocks?did=did:web:test&cids=<CID1>,<CID2>" -o blocks.car
```

**Get a blob by CID:**

```bash
curl "http://localhost:5000/xrpc/com.atproto.sync.getBlob?did=did:web:test&cid=<BLOB_CID>"
```

### Firehose (WebSocket)

Subscribe to real-time commit events using [websocat](https://github.com/vi/websocat):

```bash
# Install websocat (macOS)
brew install websocat

# Connect to firehose
websocat ws://localhost:5000/xrpc/com.atproto.sync.subscribeRepos
```

Then create/update records in another terminal to see CBOR-encoded commit events stream in real-time.

**With cursor for resumption:**

```bash
websocat "ws://localhost:5000/xrpc/com.atproto.sync.subscribeRepos?cursor=5"
```

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

## Architecture

### App (Giraffe)

- `XrpcRouter`: `/xrpc/<NSID>` routing
- `Auth`: Session management (JWTs)
- `RepoApi`: Write/Read records (`putRecord`, `getRecord`)
- `ServerApi`: Server meta (`describeServer`)

### Core (Pure F#)

- `DidResolver`: Identity resolution
- `RepoEngine`: MST, DAG-CBOR, CIDs, Blocks
- `Models`: Data types for XRPC/Database

### Infra

- SQLite/Postgres for persistence
- S3/Disk for blob storage
