#!/usr/bin/env bash
# Usage: ./generate-enterprise-license.sh <customer> <days>
# Example: ./generate-enterprise-license.sh "Acme Corp" 365
# Requires: LIVINGDOCS_PRIVATE_KEY env var (path to private key PEM)

set -euo pipefail

CUSTOMER="${1:-}"
DAYS="${2:-365}"
KEY_FILE="${LIVINGDOCS_PRIVATE_KEY:-/tmp/ld_private.pem}"

if [[ -z "$CUSTOMER" ]]; then
  echo "Usage: $0 <customer-name> [days]"
  exit 1
fi

if [[ ! -f "$KEY_FILE" ]]; then
  echo "Error: Private key not found at $KEY_FILE"
  echo "Set LIVINGDOCS_PRIVATE_KEY env var to the path of the private key PEM."
  exit 1
fi

NOW=$(date +%s)
EXP=$(( NOW + DAYS * 86400 ))

# Build JWT header + payload
HEADER=$(printf '{"alg":"RS256","typ":"JWT"}' | openssl base64 -e -A | tr '+/' '-_' | tr -d '=')
PAYLOAD=$(printf '{"iss":"livingdocs","sub":"%s","tier":"enterprise","iat":%d,"exp":%d}' \
  "$CUSTOMER" "$NOW" "$EXP" | openssl base64 -e -A | tr '+/' '-_' | tr -d '=')

# Sign with RS256
SIG=$(printf '%s.%s' "$HEADER" "$PAYLOAD" \
  | openssl dgst -sha256 -sign "$KEY_FILE" \
  | openssl base64 -e -A | tr '+/' '-_' | tr -d '=')

JWT="${HEADER}.${PAYLOAD}.${SIG}"

EXP_DATE=$(date -d "@$EXP" +"%Y-%m-%d" 2>/dev/null || date -r "$EXP" +"%Y-%m-%d")

echo ""
echo "Enterprise License Key for: $CUSTOMER"
echo "Expires: $EXP_DATE"
echo ""
echo "LIVINGDOCS_LICENSE_KEY=${JWT}"
echo ""
