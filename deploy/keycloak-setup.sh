#!/usr/bin/env bash
# Create (or update) the Keycloak client scope, audience mapper and client that
# the Firefly MCP gateway needs. Idempotent: safe to re-run.
#
#   export KC_URL=https://keycloak.peters-elshoff.nl
#   export KC_REALM=home                 # your realm, NOT master
#   export KC_ADMIN=admin
#   export KC_ADMIN_PASSWORD=...         # not stored anywhere
#   ./keycloak-setup.sh
#
# Prints the client secret at the end. That plus the client id go into Claude.

set -euo pipefail

: "${KC_URL:?set KC_URL, e.g. https://keycloak.peters-elshoff.nl}"
: "${KC_REALM:?set KC_REALM, e.g. home}"
: "${KC_ADMIN:?set KC_ADMIN}"
: "${KC_ADMIN_PASSWORD:?set KC_ADMIN_PASSWORD}"

MCP_RESOURCE="${MCP_RESOURCE:-https://firefly-mcp.peters-elshoff.nl/mcp}"
CLIENT_ID="${CLIENT_ID:-firefly-mcp-claude}"
SCOPE_NAME="${SCOPE_NAME:-firefly-mcp}"

KC_URL="${KC_URL%/}"
api="$KC_URL/admin/realms/$KC_REALM"

command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

echo "==> Authenticating to $KC_URL as $KC_ADMIN"
TOKEN=$(curl -sfS -X POST "$KC_URL/realms/master/protocol/openid-connect/token" \
  -d grant_type=password -d client_id=admin-cli \
  -d "username=$KC_ADMIN" --data-urlencode "password=$KC_ADMIN_PASSWORD" \
  | jq -r '.access_token')

[ -n "$TOKEN" ] && [ "$TOKEN" != "null" ] || { echo "could not obtain admin token" >&2; exit 1; }

auth=(-H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json")

# Fail early with a clear message rather than a confusing 404 later.
curl -sfS "${auth[@]}" "$api" >/dev/null \
  || { echo "realm '$KC_REALM' not found at $KC_URL" >&2; exit 1; }

# --- client scope -------------------------------------------------------------
echo "==> Client scope '$SCOPE_NAME'"
SCOPE_ID=$(curl -sfS "${auth[@]}" "$api/client-scopes" \
  | jq -r --arg n "$SCOPE_NAME" '.[] | select(.name==$n) | .id' | head -1)

if [ -z "$SCOPE_ID" ]; then
  curl -sfS -X POST "${auth[@]}" "$api/client-scopes" -d @- <<JSON >/dev/null
{
  "name": "$SCOPE_NAME",
  "protocol": "openid-connect",
  "description": "Access to the Firefly III MCP endpoint",
  "attributes": {
    "include.in.token.scope": "true",
    "display.on.consent.screen": "true"
  }
}
JSON
  SCOPE_ID=$(curl -sfS "${auth[@]}" "$api/client-scopes" \
    | jq -r --arg n "$SCOPE_NAME" '.[] | select(.name==$n) | .id' | head -1)
  echo "    created ($SCOPE_ID)"
else
  echo "    exists ($SCOPE_ID)"
fi

# --- audience mapper ----------------------------------------------------------
# Keycloak does not implement RFC 8707 resource indicators, so it will not put
# the MCP URL into 'aud' on its own. This mapper is what makes the gateway's
# audience check pass.
echo "==> Audience mapper -> $MCP_RESOURCE"
MAPPER_ID=$(curl -sfS "${auth[@]}" "$api/client-scopes/$SCOPE_ID/protocol-mappers/models" \
  | jq -r '.[] | select(.name=="firefly-mcp-audience") | .id' | head -1)

mapper_body=$(cat <<JSON
{
  "name": "firefly-mcp-audience",
  "protocol": "openid-connect",
  "protocolMapper": "oidc-audience-mapper",
  "config": {
    "included.custom.audience": "$MCP_RESOURCE",
    "access.token.claim": "true",
    "id.token.claim": "false",
    "introspection.token.claim": "true"
  }
}
JSON
)

if [ -z "$MAPPER_ID" ]; then
  printf '%s' "$mapper_body" | curl -sfS -X POST "${auth[@]}" \
    "$api/client-scopes/$SCOPE_ID/protocol-mappers/models" -d @- >/dev/null
  echo "    created"
else
  printf '%s' "$mapper_body" \
    | jq --arg id "$MAPPER_ID" '. + {id: $id}' \
    | curl -sfS -X PUT "${auth[@]}" \
        "$api/client-scopes/$SCOPE_ID/protocol-mappers/models/$MAPPER_ID" -d @- >/dev/null
  echo "    updated"
fi

# --- client -------------------------------------------------------------------
# Redirect URIs: the first is every hosted Claude surface (web, desktop, mobile,
# Cowork). The loopback ones are Claude Code, which uses an ephemeral port --
# verify port-agnostic matching in your Keycloak version if you want that.
echo "==> Client '$CLIENT_ID'"
client_body=$(cat <<JSON
{
  "clientId": "$CLIENT_ID",
  "name": "Claude (Firefly III MCP)",
  "enabled": true,
  "protocol": "openid-connect",
  "publicClient": false,
  "standardFlowEnabled": true,
  "implicitFlowEnabled": false,
  "directAccessGrantsEnabled": false,
  "serviceAccountsEnabled": false,
  "frontchannelLogout": true,
  "redirectUris": [
    "https://claude.ai/api/mcp/auth_callback",
    "http://localhost/callback",
    "http://127.0.0.1/callback"
  ],
  "webOrigins": [],
  "attributes": {
    "pkce.code.challenge.method": "S256",
    "client.secret.creation.time": "0"
  }
}
JSON
)

UUID=$(curl -sfS "${auth[@]}" "$api/clients?clientId=$CLIENT_ID" | jq -r '.[0].id // empty')

if [ -z "$UUID" ]; then
  printf '%s' "$client_body" | curl -sfS -X POST "${auth[@]}" "$api/clients" -d @- >/dev/null
  UUID=$(curl -sfS "${auth[@]}" "$api/clients?clientId=$CLIENT_ID" | jq -r '.[0].id')
  echo "    created ($UUID)"
else
  printf '%s' "$client_body" \
    | jq --arg id "$UUID" '. + {id: $id}' \
    | curl -sfS -X PUT "${auth[@]}" "$api/clients/$UUID" -d @- >/dev/null
  echo "    updated ($UUID)"
fi

# --- attach the scope as a DEFAULT scope --------------------------------------
# Default, not optional: the audience must be stamped regardless of which
# scopes Claude happens to request.
echo "==> Attaching '$SCOPE_NAME' as a default client scope"
curl -sfS -X PUT "${auth[@]}" "$api/clients/$UUID/default-client-scopes/$SCOPE_ID" >/dev/null
echo "    done"

SECRET=$(curl -sfS "${auth[@]}" "$api/clients/$UUID/client-secret" | jq -r '.value')

cat <<EOF

────────────────────────────────────────────────────────────
Add the custom connector in Claude with:

  URL            $MCP_RESOURCE
  Client ID      $CLIENT_ID
  Client Secret  $SECRET

Gateway configuration must use:

  Mcp__Issuer    $KC_URL/realms/$KC_REALM
  Mcp__Resource  $MCP_RESOURCE
────────────────────────────────────────────────────────────
EOF
