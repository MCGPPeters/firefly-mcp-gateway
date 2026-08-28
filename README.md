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
| `Upstream__Headers__<Name>` | Headers sent to the upstream MCP server, replacing whatever the caller sent. This is where the upstream's own credential goes. Secrets belong in the environment, never in `appsettings.json`. |
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

## A second instance: Home Assistant

The gateway is upstream-agnostic — only `Upstream:Headers` differs per instance —
so the same image can front Home Assistant's MCP server.

**Why this is needed at all.** Home Assistant implements the MCP OAuth handshake
itself: `POST /api/mcp` returns a proper `401` with a `resource_metadata` pointer,
and it serves both well-known documents. On paper it needs no gateway. In practice
its authorization server metadata is missing three fields Claude depends on:

| Field | State | Consequence |
|---|---|---|
| `token_endpoint_auth_methods_supported` | absent | Claude needs `"none"` here *alongside* `client_id_metadata_document_supported` to pick CIMD. Missing, so it falls back to DCR. |
| `registration_endpoint` | absent | IndieAuth does not do dynamic client registration, so the DCR fallback dead-ends. |
| `code_challenge_methods_supported` | absent | The spec requires `["S256"]` to be advertised so clients can verify PKCE support up front. |

Its protected resource metadata also declares `"resource": "https://<host>"` with no
path, while the endpoint is `/api/mcp` — Claude requires `resource` to match the URL
the user types, character for character.

Re-check this before building around it; a Home Assistant release that adds those
fields makes the gateway unnecessary for HA, and connecting directly is better
because it keeps HA's own per-user authorization.

**Running it.** Give the second instance its own hostname, its own Keycloak client
scope with its own audience mapper, and its own container. Do **not** add both
audiences to one instance: without RFC 8707 resource indicators, a token minted for
one upstream would be accepted on the other.

```yaml
environment:
  Mcp__Resource: https://ha-mcp.peters-elshoff.nl/mcp
  Mcp__ResourceMetadataUrl: https://ha-mcp.peters-elshoff.nl/.well-known/oauth-protected-resource
  Mcp__Issuer: https://keycloak.peters-elshoff.nl/realms/home
  Upstream__Headers__Authorization: "Bearer ${HA_LONG_LIVED_TOKEN}"
  ReverseProxy__Clusters__firefly-mcp__Destinations__primary__Address: http://homeassistant:8123/
```

Route `/mcp` to Home Assistant's `/api/mcp` with a YARP path transform, or point the
cluster at the full path.

**The trade-off.** A Home Assistant long-lived access token carries one user's full
rights. Everyone who completes the Keycloak flow gets those rights, and HA's own
per-user authorization is bypassed. That is acceptable for Firefly III, where a PAT is
the only option. For Home Assistant it is a real loss — which is why the direct route
is worth re-testing whenever HA is updated.

## Running it as a TrueNAS Custom App

The image is published to `ghcr.io/<owner>/firefly-mcp-gateway:latest` by
`.github/workflows/publish.yml`. Apps → Discover Apps → Custom App.

| Field | Value |
|---|---|
| Image repository | `ghcr.io/<owner>/firefly-mcp-gateway` |
| Image tag | `latest` |
| Port: container | `8080` |
| Port: node | `30111` |

Environment variables — every real value lives here, never in the image:

```
ASPNETCORE_HTTP_PORTS = 8080
Mcp__Resource = https://firefly-mcp.peters-elshoff.nl/mcp
Mcp__ResourceMetadataUrl = https://firefly-mcp.peters-elshoff.nl/.well-known/oauth-protected-resource
Mcp__Issuer = https://keycloak.peters-elshoff.nl/realms/home
Mcp__ScopesSupported__0 = openid
Mcp__ScopesSupported__1 = profile
Mcp__ScopesSupported__2 = firefly-mcp
Upstream__Headers__Authorization = Bearer <Firefly III PAT>
Upstream__Headers__X-Firefly-III-Url = http://10.69.2.90:30105
ReverseProxy__Clusters__firefly-mcp__Destinations__primary__Address = http://10.69.2.90:30110
```

The gateway listens on node port 30111 and proxies to the bare MCP server on
30110. Traefik's `firefly-mcp` service is repointed from 30110 to 30111, so the
unauthenticated server is no longer reachable from outside the LAN.

The app will not start if `Upstream__Headers__Authorization` is empty — that is
deliberate, so a missing token fails loudly instead of producing 401s from the
upstream that look like a gateway fault.
