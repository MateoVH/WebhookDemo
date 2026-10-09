<#
.SYNOPSIS
  Delivers a signed payment_intent.succeeded event to the local receiver, exactly the way Stripe signs it.

.EXAMPLE
  ./scripts/send-stripe-event.ps1                      # one delivery
  ./scripts/send-stripe-event.ps1 -Times 3             # the same event three times: watch the duplicates get absorbed
  ./scripts/send-stripe-event.ps1 -EventId evt_123     # redeliver an event id you sent before
#>
param(
    [int] $Times = 1,
    [string] $EventId = "evt_" + [guid]::NewGuid().ToString("N").Substring(0, 24),
    [string] $Url = "http://localhost:5080/webhooks/stripe",
    [string] $Secret = "whsec_local_demo_secret_not_for_production"
)

$ErrorActionPreference = "Stop"
$utf8 = [System.Text.Encoding]::UTF8
function ConvertTo-Hex([byte[]] $bytes) { -join ($bytes | ForEach-Object { $_.ToString("x2") }) }

$paymentId = "pi_" + (ConvertTo-Hex ([System.Security.Cryptography.SHA256]::Create().ComputeHash($utf8.GetBytes($EventId)))).Substring(0, 24)
$payload = @{
    id      = $EventId
    object  = "event"
    type    = "payment_intent.succeeded"
    created = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    data    = @{
        object = @{
            id              = $paymentId
            object          = "payment_intent"
            amount          = 4900
            amount_received = 4900
            currency        = "eur"
            receipt_email   = "ana@example.com"
            status          = "succeeded"
        }
    }
} | ConvertTo-Json -Depth 5 -Compress

$hmac = [System.Security.Cryptography.HMACSHA256]::new($utf8.GetBytes($Secret))
foreach ($delivery in 1..$Times) {
    # Stripe signs "{timestamp}.{raw body}" with HMAC-SHA256 and sends t=…,v1=… in the Stripe-Signature header.
    $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $signature = ConvertTo-Hex $hmac.ComputeHash($utf8.GetBytes("$timestamp.$payload"))
    Invoke-RestMethod -Method Post -Uri $Url -ContentType "application/json" `
        -Headers @{ "Stripe-Signature" = "t=$timestamp,v1=$signature" } `
        -Body $utf8.GetBytes($payload) | ConvertTo-Json -Depth 5 -Compress
}
