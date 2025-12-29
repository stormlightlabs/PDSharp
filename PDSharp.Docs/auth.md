# AT Protocol Session & Account Authentication

## Session Authentication (Legacy Bearer JWT)

Based on the [XRPC Spec](https://atproto.com/specs/xrpc#authentication):

### Token Types

| Token         | JWT `typ` Header | Lifetime                    | Purpose                        |
| ------------- | ---------------- | --------------------------- | ------------------------------ |
| Access Token  | `at+jwt`         | Short (~2min refresh cycle) | Authenticate most API requests |
| Refresh Token | `refresh+jwt`    | Longer (~2 months)          | Obtain new access tokens       |

### Endpoints

- **`createSession`**: Login with identifier (handle/email) + password → returns `{accessJwt, refreshJwt, handle, did}`
- **`refreshSession`**: Uses refresh JWT in Bearer header → returns new `{accessJwt, refreshJwt, handle, did}`
- **`createAccount`**: Register new account → returns session tokens + creates DID

### JWT Claims (Server-Generated)

Servers should implement **domain separation** using the `typ` header field:

- Access: `typ: at+jwt` (per [RFC 9068](https://www.rfc-editor.org/rfc/rfc9068.html))
- Refresh: `typ: refresh+jwt`

Standard JWT claims: `sub` (DID), `iat`, `exp`, `jti` (nonce)

### Configuration Required

Yes, JWT signing requires a **secret key** for HMAC-SHA256 (HS256). This should be:

- Loaded from configuration/environment variable (e.g., `PDS_JWT_SECRET`)
- At least 32 bytes of cryptographically random data
- Never hardcoded or committed to source control

## Account Storage

### Reference PDS Approach

The Bluesky reference PDS uses:

- **SQLite database per user** (recent architecture)
- `account.sqlite` contains: handle, email, DID, password hash
- Accounts indexed by DID (primary) and handle (unique)

## App Passwords

App passwords are a security feature allowing restricted access:

- Format: `xxxx-xxxx-xxxx-xxxx`
- Created/revoked independently from main password
- Grants limited permissions (no auth settings changes)

## Inter-Service Auth (Different from Session Auth)

For service-to-service requests, different mechanism:

- Uses **asymmetric signing** (ES256/ES256K) with account's signing key
- Short-lived tokens (~60sec)
- Validated against DID document

## Summary: Implementation Decisions

| Aspect          | Decision                   | Rationale                            |
| --------------- | -------------------------- | ------------------------------------ |
| Token signing   | HS256 (symmetric)          | Simpler, standard for session tokens |
| Secret storage  | Config/env var             | Required for security                |
| Account storage | In-memory (initial)        | Matches existing patterns            |
| Password hash   | SHA-256 + salt             | Uses existing Crypto.fs              |
| Token lifetimes | Access: 15min, Refresh: 7d | Conservative defaults                |

## References

- [XRPC Authentication Spec](https://atproto.com/specs/xrpc#authentication)
- [RFC 9068 - JWT Access Tokens](https://www.rfc-editor.org/rfc/rfc9068.html)
- [Bluesky PDS GitHub](https://github.com/bluesky-social/pds)
