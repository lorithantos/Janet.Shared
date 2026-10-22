# Janet.Mcp.Security

Front-door security for a local MCP server over HTTP, shared by janet-mcp and razorgraph-mcp.

## The loopback guard

Binding 127.0.0.1 is not a boundary on its own: a DNS-rebinding page reaches the port while
still sending its own Host and Origin. `LoopbackGuard` refuses

- a Host that is not 127.0.0.1, localhost or [::1] (any port): **400**
- more than one Origin, or an Origin that is not http(s) on one of those names with nothing
  after the port (no userinfo, path, query or fragment; `null` refused): **403**
- a POST that is not `application/json`: **415**

Each refusal is plain text with `X-Content-Type-Options: nosniff`, and names the refused value
(capped at 200 characters). The decisions (`IsLoopbackHost`, `IsAllowedOrigin`,
`IsJsonContentType`, `Check`) are pure functions; `UseLoopbackGuard()` is the middleware.

## The token

`McpTokenStore` keeps one per-machine bearer token: 32 random bytes, handed out as unpadded
base64url. The registry holds only the DPAPI blob (`CurrentUser` scope, entropy
`McpTokenStore.EntropyText`) as REG_BINARY at `HKCU\Software\Janet\McpToken`, so a copied hive or
another account gets bytes it cannot decrypt.

```csharp
string? token = McpTokenStore.Default.Read();    // null when there is none
string token  = McpTokenStore.Default.Ensure();  // create once, then the same one
string token  = McpTokenStore.Default.Rotate();  // replace and return
```

A value that cannot be read back throws, naming the key and saying to rotate; it is never
silently replaced. Windows only. Tests construct `new McpTokenStore(@"Software\Janet.Tests\<guid>")`.

## Bearer authentication

```csharp
var bearer = new BearerAuthenticator(BearerMode.Report, () => McpTokenStore.Default.Read());
app.UseMcpSecurity(bearer);                      // the guard, then the bearer check
app.MapGet("/health", () => new { ..., auth = bearer.Snapshot() });
```

- `Off` installs only the guard. `Report` lets everything through and counts ok / missing /
  wrong / unconfigured. `Require` answers 401 to anything but the right token.
- The guard always runs first; there is no public way to install the bearer check alone.
- `/health` is never refused or counted. `Snapshot()` is what it shows.
- Tokens are compared as SHA-256 hashes with `CryptographicOperations.FixedTimeEquals`.
- The provider is asked at startup and cached; a mismatch re-asks at most once per 30 seconds,
  so a rotated token is picked up without a restart. Re-asks are served by one consumer reading
  a single-reader channel: every request waiting is answered by one provider call, and nothing
  elects who refreshes. Dispose the authenticator when the server stops; requests still waiting
  then fail with `ObjectDisposedException` instead of hanging.
- The 401 is plain text and ends with a hint saying where to get the header. The server
  supplies it as the constructor's `headerHint` (for example
  ``"Run `my-tool headers` to print the header to send."``); without one it is
  `BearerAuthenticator.DefaultHeaderHint`, a sentence that names no tool. It sends no `WWW-Authenticate`: MCP clients answer a 401 by looking for OAuth, and
  there is none here.
- No token, expected or received, is logged, echoed or put in the snapshot.
