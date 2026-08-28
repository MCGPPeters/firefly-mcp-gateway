#!/usr/bin/env bash
# Preflight for the Firefly MCP connector. Run after each step.
#
#   ./verify.sh
#
# DNS is checked over DoH on purpose: AdGuard answers these names with LAN
# addresses, so asking your own resolver would tell you everything is fine
# while Claude still sees NXDOMAIN.
#
# Caveat: AdGuard blocks DNS-bypass providers by default, so from inside the
# LAN the DoH check may not get through at all. It reports that honestly
# rather than pretending the record is missing.

set -uo pipefail

MCP_HOST="${MCP_HOST:-firefly-mcp.peters-elshoff.nl}"
KC_HOST="${KC_HOST:-keycloak.peters-elshoff.nl}"
KC_REALM="${KC_REALM:-home}"
MCP_RESOURCE="${MCP_RESOURCE:-https://$MCP_HOST/mcp}"
ISSUER="https://$KC_HOST/realms/$KC_REALM"

pass=0; fail=0; skip=0
ok()   { printf '  \033[32mPASS\033[0m  %s\n' "$1"; pass=$((pass+1)); }
bad()  { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; fail=$((fail+1)); }
skp()  { printf '  \033[33mSKIP\033[0m  %s\n' "$1"; skip=$((skip+1)); }
note() { printf '        %s\n' "$1"; }

# Try more than one resolver; give up cleanly rather than guessing.
doh() {
  local name=$1 out
  for ep in "https://dns.google/resolve?name=$name&type=A" \
            "https://cloudflare-dns.com/dns-query?name=$name&type=A"; do
    if out=$(curl -sfS --max-time 8 -H 'accept: application/dns-json' "$ep" 2>/dev/null); then
      printf '%s' "$out"
      return 0
    fi
  done
  return 1
}

echo
echo "1. Public DNS (via DoH, bypassing AdGuard)"
doh_blocked=0
for h in "$MCP_HOST" "$KC_HOST"; do
  if ! r=$(doh "$h"); then
    skp "$h — no DoH resolver reachable from this network"
    doh_blocked=1
    continue
  fi
  status=$(printf '%s' "$r" | jq -r '.Status // 99')
  ips=$(printf '%s' "$r" | jq -r '[.Answer[]?|select(.type==1)|.data]|join(", ")')
  if [ "$status" = "0" ] && [ -n "$ips" ]; then
    ok "$h -> $ips"
    case "$ips" in
      104.*|172.6[4-9].*|172.7[0-1].*|188.114.*|162.159.*)
        note "Looks like a Cloudflare proxy IP. Turn the orange cloud OFF:"
        note "it terminates TLS and breaks the 160.79.104.0/21 allowlist." ;;
    esac
  else
    bad "$h has no public A record (Status=$status)"
  fi
done

if [ "$doh_blocked" = "1" ]; then
  note ""
  note "AdGuard blocks DNS-bypass providers by default, so this check cannot"
  note "run from inside the LAN. Confirm the records in the Cloudflare"
  note "dashboard, or re-run this from a tethered connection."
fi

echo
echo "2. Keycloak OIDC discovery"
disc=$(curl -sfS --max-time 10 "$ISSUER/.well-known/openid-configuration" 2>/dev/null || echo '')
if [ -z "$disc" ]; then
  bad "discovery unreachable at $ISSUER/.well-known/openid-configuration"
  note "checked from this machine; a 403 here means the ip-allowlist is too tight"
else
  iss=$(printf '%s' "$disc" | jq -r '.issuer // empty')
  [ "$iss" = "$ISSUER" ] && ok "issuer matches ($iss)" \
    || bad "issuer is '$iss' but the gateway expects '$ISSUER'"

  printf '%s' "$disc" | jq -e '.code_challenge_methods_supported|index("S256")' >/dev/null \
    && ok "PKCE S256 advertised" || bad "S256 missing from code_challenge_methods_supported"

  printf '%s' "$disc" | jq -e '.scopes_supported|index("offline_access")' >/dev/null \
    && ok "offline_access advertised (refresh tokens will work)" \
    || note "offline_access not advertised; Claude will not get a refresh token"
fi

echo
echo "3. Gateway protected resource metadata"
prm=$(curl -sfS --max-time 10 "https://$MCP_HOST/.well-known/oauth-protected-resource" 2>/dev/null || echo '')
if [ -z "$prm" ]; then
  bad "no metadata document at https://$MCP_HOST/.well-known/oauth-protected-resource"
  note "if the old MCP container still answers this host, the gateway is not in front yet"
else
  res=$(printf '%s' "$prm" | jq -r '.resource // empty')
  [ "$res" = "$MCP_RESOURCE" ] && ok "resource matches ($res)" \
    || bad "resource is '$res' but you will type '$MCP_RESOURCE' into Claude"

  as=$(printf '%s' "$prm" | jq -r '.authorization_servers[0] // empty')
  [ "$as" = "$ISSUER" ] && ok "authorization_servers[0] matches ($as)" \
    || bad "authorization_servers[0] is '$as', expected '$ISSUER'"
fi

echo
echo "4. Unauthenticated call returns a usable challenge"
hdrs=$(curl -sS -o /dev/null -D - --max-time 10 -X POST "$MCP_RESOURCE" 2>/dev/null || echo '')
code=$(printf '%s' "$hdrs" | awk 'NR==1{print $2}')
wa=$(printf '%s' "$hdrs" | tr -d '\r' | awk -F': ' 'tolower($1)=="www-authenticate"{print $2}')

if [ "$code" = "401" ]; then
  ok "HTTP 401"
else
  bad "HTTP ${code:-none} (Claude only reads the challenge on a 401)"
  [ "$code" = "400" ] && note "400 is the bare firefly-iii-mcp answering; the gateway is not in front yet"
fi

case "$wa" in
  *resource_metadata=*) ok "WWW-Authenticate carries resource_metadata" ;;
  "") bad "no WWW-Authenticate header" ;;
  *)  bad "WWW-Authenticate has no resource_metadata: $wa" ;;
esac

echo
echo "────────────────────────────────────────"
printf '  %d passed, %d failed, %d skipped\n' "$pass" "$fail" "$skip"
if [ "$fail" -eq 0 ] && [ "$skip" -eq 0 ]; then
  echo "  Ready to add the connector in Claude."
elif [ "$fail" -eq 0 ]; then
  echo "  Nothing failed, but some checks could not run. See the notes above."
else
  echo "  Fix the failures above first."
fi
echo
echo "  Note: steps 2-4 run from this machine. Inside the LAN they hit Traefik"
echo "  directly, so they do not prove Claude can reach it. Public DNS plus the"
echo "  allowlist are what decide that."
echo

[ "$fail" -eq 0 ]
