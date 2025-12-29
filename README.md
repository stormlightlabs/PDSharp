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

### Verify

Check the `describeServer` endpoint:

```bash
curl http://localhost:5000/xrpc/com.atproto.server.describeServer
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
