#!/usr/bin/env bash
# Delivers a signed payment_intent.succeeded event to the local receiver, exactly the way Stripe signs it.
#
#   ./scripts/send-stripe-event.sh             one delivery
#   ./scripts/send-stripe-event.sh 3           the same event three times: watch the duplicates get absorbed
#   ./scripts/send-stripe-event.sh 1 evt_123   redeliver an event id you sent before
#
# URL and STRIPE_SECRET can be overridden with environment variables.
set -euo pipefail

url="${URL:-http://localhost:5080/webhooks/stripe}"
secret="${STRIPE_SECRET:-whsec_local_demo_secret_not_for_production}"
times="${1:-1}"
event_id="${2:-evt_$(openssl rand -hex 12)}"
payment_id="pi_$(printf '%s' "$event_id" | openssl dgst -sha256 | awk '{print substr($NF, 1, 24)}')"

payload=$(printf '{"id":"%s","object":"event","type":"payment_intent.succeeded","created":%s,"data":{"object":{"id":"%s","object":"payment_intent","amount":4900,"amount_received":4900,"currency":"eur","receipt_email":"ana@example.com","status":"succeeded"}}}' \
  "$event_id" "$(date +%s)" "$payment_id")

for _ in $(seq 1 "$times"); do
  # Stripe signs "{timestamp}.{raw body}" with HMAC-SHA256 and sends t=…,v1=… in the Stripe-Signature header.
  timestamp=$(date +%s)
  signature=$(printf '%s' "$timestamp.$payload" | openssl dgst -sha256 -hmac "$secret" | awk '{print $NF}')
  curl -s -X POST "$url" \
    -H "Content-Type: application/json" \
    -H "Stripe-Signature: t=$timestamp,v1=$signature" \
    --data-binary "$payload"
  echo
done
