# Firefly III MCP Gateway

An OAuth 2.1 resource server that puts Keycloak in front of the Firefly III MCP
server, so Claude can reach it over the internet without the Firefly personal
access token ever leaving the LAN.

```
Claude  ──HTTPS──▶  Traefik  ──▶  FireflyMcpGateway  ──▶  firefly-iii-mcp  ──▶  Firefly III
        (OAuth 2.1)   (TLS,          (validates Keycloak      (internal only,
                    IP allowlist)     JWT, injects PAT)        no PAT exposure)
                                              │
                                              ▼
                                    Keycloak (authorization server)
```

The gateway does three things and nothing else:

1. Serves RFC 9728 protected resource metadata so Claude can discover Keycloak.
2. Rejects unauthenticated calls with a `401` carrying the `WWW-Authenticate`
   pointer Claude needs to start the OAuth flow.
3. Validates the Keycloak access token, then swaps it for the Firefly III PAT on
   the way upstream.

## Verified behaviour

Built against .NET 10 with YARP 2.3.0 and `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.11.
Exercised locally on 2026-08-28:

```
GET /.well-known/oauth-protected-resource
→ 200 {"resource":"https://firefly-mcp.peters-elshoff.nl/mcp",
       "authorization_servers":["https://keycloak.peters-elshoff.nl/realms/home"],
       "scopes_supported":["openid","profile","firefly-mcp"],
       "bearer_methods_supported":["header"]}

POST /mcp   (no token)
→ 401 Unauthorized
  WWW-Authenticate: Bearer resource_metadata="https://firefly-mcp.peters-elshoff.nl/.well-known/oauth-protected-resource",
                    error="invalid_token", scope="openid profile firefly-mcp"
```

The upstream proxy path has **not** been exercised end to end — that needs the
real Keycloak realm and a running `firefly-iii-mcp`.

## Keycloak setup

Realm issuer used throughout: `https://keycloak.peters-elshoff.nl/realms/home`
(adjust if your realm is named differently — it must match `Mcp:Issuer`).

### 1. Client scope with an audience mapper

Keycloak does not implement RFC 8707 resource indicators, so it will not stamp
the MCP server URL into `aud` on its own. An audience mapper does it instead.

* **Client scopes → Create client scope**
  * Name: `firefly-mcp`
  * Type: **Default**
  * Protocol: `openid-connect`
  * Include in token scope: **On**
* Open it → **Mappers → Configure a new mapper → Audience**
  * Name: `firefly-mcp-audience`
  * Included Custom Audience: `https://firefly-mcp.peters-elshoff.nl/mcp`
  * Add to access token: **On**

### 2. Client for Claude

* **Clients → Create client**
  * Client ID: `firefly-mcp-claude`
  * Client authentication: **On** (confidential — Claude accepts a client secret)
  * Authentication flow: **Standard flow** only. Direct access grants **off**,
    Service accounts **off**.
  * Valid redirect URIs: `https://claude.ai/api/mcp/auth_callback`
* **Advanced → Proof Key for Code Exchange Code Challenge Method: `S256`**
  (Claude always sends PKCE S256.)
* **Client scopes → Add client scope → `firefly-mcp` → Default**

Copy the client secret from **Credentials**.

### 3. Check discovery

```bash
curl -s https://keycloak.peters-elshoff.nl/realms/home/.well-known/openid-configuration \
  | jq '{issuer, code_challenge_methods_supported, scopes_supported}'
```

`issuer` must equal `Mcp:Issuer` exactly, and `code_challenge_methods_supported`
must contain `S256`. If `scopes_supported` lists `offline_access`, Claude will
request it and you get refresh tokens for free.

## Network

Anthropic's outbound traffic comes from **`160.79.104.0/21`**. That range must
reach **both**:

* `firefly-mcp.peters-elshoff.nl` — the gateway, and
* `keycloak.peters-elshoff.nl` — discovery and token exchange happen from the
  same range.

A LAN-only IP allowlist on Keycloak is the single most likely reason for a
connector that "can't reach the MCP server" while the gateway logs look fine.
Your own browser also needs to reach Keycloak for the consent screen.

Allowlisting that range rather than opening the host to the whole internet keeps
the endpoint effectively private — see `compose.yaml` for the Traefik middleware.

## Configuration

Every key is settable as an environment variable with `__` as the separator.

| Key | Meaning |
|---|---|
| `Mcp__Resource` | Public MCP URL. **Must match what you type into Claude, character for character**, and must equal the audience mapper value. |
| `Mcp__Issuer` | Keycloak realm issuer. No trailing slash — the app refuses to start with one. |
| `Mcp__ResourceMetadataUrl` | Absolute URL of the metadata document this gateway serves. |
| `Mcp__ScopesSupported__N` | Scopes advertised in metadata and in the 401 challenge. |
| `Mcp__RequiredScope` | Optional. When set, a token without this scope gets a 403. Leave empty at first. |
| `Firefly__BaseUrl` | Firefly III URL as reachable from the MCP container. |
| `Firefly__PersonalAccessToken` | Firefly III PAT (Options → Profile → OAuth). Secret — pass by environment, never commit. |
| `ReverseProxy__Clusters__firefly-mcp__Destinations__primary__Address` | Upstream `firefly-iii-mcp` address. |

## Deploy

```bash
export FIREFLY_BASE_URL=http://<truenas>:30105
export FIREFLY_PAT=<firefly personal access token>
docker compose up -d --build
```

On Apple Silicon building for TrueNAS, add `--platform linux/amd64`.

## Connect in Claude

Settings → Connectors → **Add custom connector**

* URL: `https://firefly-mcp.peters-elshoff.nl/mcp`
* OAuth Client ID: `firefly-mcp-claude`
* OAuth Client Secret: *(from Keycloak Credentials)*

## Troubleshooting

| Symptom | Cause |
|---|---|
| "Couldn't reach the MCP server", gateway sees no traffic | Traefik allowlist blocks `160.79.104.0/21`, or DNS/cert not resolving. |
| Gateway sees the request, Keycloak sees nothing | Protected resource metadata unreachable, or `authorization_servers` points somewhere Claude cannot reach. |
| Consent screen appears, then the connection fails | Token has no matching `aud`. The audience mapper is missing, or the client scope is not **Default** on the client. |
| Every token rejected, log says issuer invalid | Trailing slash or realm-name mismatch between `Mcp__Issuer` and Keycloak's `iss`. |
| Intermittent connection failures | Keycloak slower than Anthropic's 10s discovery/token budget (30s for refresh). |
| 403 instead of 401 | `Mcp__RequiredScope` is set but the token lacks that scope. |

## Security notes

* The Firefly PAT is a **full-access** Firefly III credential. It stays inside the
  compose network; only the gateway holds it, and the inbound `Authorization`
  header is stripped before proxying so a caller can never smuggle one through.
* The gateway authenticates *callers*, not *users of Firefly*. Anyone who
  completes the Keycloak flow gets the same Firefly access. Restrict who can log
  in via a Keycloak realm role or group policy on the client if that matters.
* `firefly-iii-mcp` is pinned by tag in `compose.yaml`. Pin a digest instead if
  you want the upstream frozen — it is third-party code holding your finances.
