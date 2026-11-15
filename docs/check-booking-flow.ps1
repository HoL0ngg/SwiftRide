param(
    [string]$GatewayUrl = "http://localhost:5080",
    [string]$RiderAccessToken = "",
    [string]$DriverAccessToken = "",
    [ValidateSet("Rest", "Events")]
    [string]$ExpectedTransport = "Rest"
)

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::DefaultConnectionLimit = 100
[System.Net.ServicePointManager]::Expect100Continue = $false

Write-Host "=== SwiftRide End-to-End Booking Smoke Test ===" -ForegroundColor Cyan
Write-Host "Gateway: $GatewayUrl"
Write-Host "Expected Transport: $ExpectedTransport"

$riderId = "smoke-rider-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$driverId = "smoke-driver-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"

# Obtain tokens from parameter or Gateway dev/token endpoint in Development
if ([string]::IsNullOrWhiteSpace($RiderAccessToken)) {
    Write-Host "Obtaining rider token from Gateway dev endpoint..." -ForegroundColor Gray
    try {
        $res = Invoke-RestMethod -Uri "$GatewayUrl/dev/token?userId=$riderId&role=rider" -Method Post -TimeoutSec 15
        $RiderAccessToken = if ($res.access_token) { $res.access_token } else { $res.token }
    } catch {
        Write-Error "Failed to obtain Rider dev token from $GatewayUrl/dev/token. Pass -RiderAccessToken when targeting non-development environments. Error: $_"
        exit 1
    }
}

if ([string]::IsNullOrWhiteSpace($DriverAccessToken)) {
    Write-Host "Obtaining driver token from Gateway dev endpoint..." -ForegroundColor Gray
    try {
        $res = Invoke-RestMethod -Uri "$GatewayUrl/dev/token?userId=$driverId&role=driver" -Method Post -TimeoutSec 15
        $DriverAccessToken = if ($res.access_token) { $res.access_token } else { $res.token }
    } catch {
        Write-Error "Failed to obtain Driver dev token from $GatewayUrl/dev/token. Pass -DriverAccessToken when targeting non-development environments. Error: $_"
        exit 1
    }
}

$riderHeaders = @{
    "Authorization" = "Bearer $RiderAccessToken"
    "Content-Type" = "application/json"
}

$driverHeaders = @{
    "Authorization" = "Bearer $DriverAccessToken"
    "Content-Type" = "application/json"
}

# 1. Driver goes online
Write-Host "`n[1/7] Driver going online at Ben Thanh Market..." -ForegroundColor Yellow
$locationPayload = @{
    latitude = 10.7725
    longitude = 106.6980
    isOnline = $true
} | ConvertTo-Json

Invoke-RestMethod -Uri "$GatewayUrl/drivers/$driverId/location" -Method Put -Headers $driverHeaders -Body $locationPayload -TimeoutSec 15 | Out-Null
Write-Host "  -> Driver online registered." -ForegroundColor Green

# 1b. Verify actor isolation: Rider attempting driver endpoint must be rejected with 403 Forbidden
Write-Host "`n[Actor Check] Rider attempting driver-only location endpoint (expected 403)..." -ForegroundColor Yellow
$actorRejected = $false
try {
    Invoke-RestMethod -Uri "$GatewayUrl/drivers/$driverId/location" -Method Put -Headers $riderHeaders -Body $locationPayload -TimeoutSec 15
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 403 -or $_.Exception.Response.StatusCode -eq [System.Net.HttpStatusCode]::Forbidden) {
        $actorRejected = $true
    }
}
if (-not $actorRejected) {
    Write-Error "Security failure: Rider was not rejected (expected 403 Forbidden) when accessing driver endpoint."
    exit 1
}
Write-Host "  -> Rider correctly rejected with 403 Forbidden." -ForegroundColor Green

# 2. Rider requests a trip
Write-Host "`n[2/7] Rider requesting trip from Ben Thanh to Landmark 81..." -ForegroundColor Yellow
$createPayload = @{
    pickup = @{ latitude = 10.7725; longitude = 106.6980 }
    pickupName = "Chợ Bến Thành"
    pickupAddress = "Lê Lợi, Quận 1"
    destination = @{ latitude = 10.7951; longitude = 106.7219 }
    destinationName = "Landmark 81"
    destinationAddress = "Bình Thạnh"
    paymentMethod = "wallet"
    mode = "standard"
} | ConvertTo-Json

$createKey = [Guid]::NewGuid().ToString()
$createHeaders = $riderHeaders.Clone()
$createHeaders["Idempotency-Key"] = $createKey

$trip = Invoke-RestMethod -Uri "$GatewayUrl/trips" -Method Post -Headers $createHeaders -Body $createPayload -TimeoutSec 15
$tripId = $trip.id
$quoteId = $trip.quoteId
$initialFare = $trip.fareAmount
Write-Host "  -> Trip Created: ID = $tripId, Status = $($trip.status), Fare = $initialFare VND" -ForegroundColor Green

# Replay create trip with same idempotency key
Write-Host "  -> Replaying trip creation with same idempotency key..." -ForegroundColor Gray
$replayedTrip = Invoke-RestMethod -Uri "$GatewayUrl/trips" -Method Post -Headers $createHeaders -Body $createPayload -TimeoutSec 15
if ($replayedTrip.id -ne $tripId -or $replayedTrip.fareAmount -ne $initialFare) {
    Write-Error "Idempotency replay failed for trip creation: expected ID $tripId, got $($replayedTrip.id)"
    exit 1
}
Write-Host "  -> Idempotent replay verified (same TripId and fare)." -ForegroundColor Green

# 3. Rider confirms quote
Write-Host "`n[3/7] Rider confirming quote..." -ForegroundColor Yellow
$confirmPayload = @{ quoteId = $quoteId } | ConvertTo-Json
$confirmHeaders = $riderHeaders.Clone()
$confirmHeaders["Idempotency-Key"] = [Guid]::NewGuid().ToString()

$trip = Invoke-RestMethod -Uri "$GatewayUrl/trips/$tripId/confirm-quote" -Method Post -Headers $confirmHeaders -Body $confirmPayload -TimeoutSec 15
Write-Host "  -> Quote Confirmed: Status = $($trip.status), OfferedDriver = $($trip.offeredDriverId)" -ForegroundColor Green

if ($trip.offeredDriverId -and $trip.offeredDriverId -ne $driverId) {
    Write-Host "  -> Switching driver token to matched driver: $($trip.offeredDriverId)" -ForegroundColor Gray
    $res = Invoke-RestMethod -Uri "$GatewayUrl/dev/token?userId=$($trip.offeredDriverId)&role=driver" -Method Post -TimeoutSec 15
    $driverHeaders["Authorization"] = "Bearer $(if ($res.access_token) { $res.access_token } else { $res.token })"
}

# 4. Driver accepts trip
Write-Host "`n[4/7] Driver accepting trip..." -ForegroundColor Yellow
$acceptHeaders = $driverHeaders.Clone()
$acceptHeaders["Idempotency-Key"] = [Guid]::NewGuid().ToString()

$trip = Invoke-RestMethod -Uri "$GatewayUrl/trips/$tripId/driver-accept" -Method Post -Headers $acceptHeaders -TimeoutSec 15
Write-Host "  -> Driver Accepted: Status = $($trip.status), AssignedDriver = $($trip.assignedDriverId)" -ForegroundColor Green

# 5. Driver en-route & pickup
Write-Host "`n[5/7] Driver heading to pickup and picking up rider..." -ForegroundColor Yellow
$enRouteHeaders = $driverHeaders.Clone()
$enRouteHeaders["Idempotency-Key"] = [Guid]::NewGuid().ToString()
$trip = Invoke-RestMethod -Uri "$GatewayUrl/trips/$tripId/en-route" -Method Post -Headers $enRouteHeaders -TimeoutSec 15

$pickupHeaders = $driverHeaders.Clone()
$pickupHeaders["Idempotency-Key"] = [Guid]::NewGuid().ToString()
$trip = Invoke-RestMethod -Uri "$GatewayUrl/trips/$tripId/pickup" -Method Post -Headers $pickupHeaders -TimeoutSec 15
Write-Host "  -> Rider Picked Up: Status = $($trip.status)" -ForegroundColor Green

# 6. Driver drops off
Write-Host "`n[6/7] Driver dropping off rider..." -ForegroundColor Yellow
$dropoffKey = [Guid]::NewGuid().ToString()
$dropoffHeaders = $driverHeaders.Clone()
$dropoffHeaders["Idempotency-Key"] = $dropoffKey
$trip = Invoke-RestMethod -Uri "$GatewayUrl/trips/$tripId/dropoff" -Method Post -Headers $dropoffHeaders -TimeoutSec 15
Write-Host "  -> Dropped Off: Status = $($trip.status)" -ForegroundColor Green

# Replay dropoff with same idempotency key
Write-Host "  -> Replaying dropoff with same idempotency key..." -ForegroundColor Gray
$replayedDropoff = Invoke-RestMethod -Uri "$GatewayUrl/trips/$tripId/dropoff" -Method Post -Headers $dropoffHeaders -TimeoutSec 15
if ($replayedDropoff.id -ne $tripId) {
    Write-Error "Idempotency replay failed for dropoff: expected ID $tripId, got $($replayedDropoff.id)"
    exit 1
}
Write-Host "  -> Idempotent replay for dropoff verified." -ForegroundColor Green

# 7. Poll until Paid
Write-Host "`n[7/7] Polling trip until Paid status..." -ForegroundColor Yellow
$maxAttempts = 40
$paid = $false
for ($i = 1; $i -le $maxAttempts; $i++) {
    Start-Sleep -Seconds 1
    $polled = Invoke-RestMethod -Uri "$GatewayUrl/trips/$tripId" -Method Get -Headers $riderHeaders -TimeoutSec 15
    Write-Host "  Attempt ${i}: Status = $($polled.status)"
    if ($polled.status -eq "Paid") {
        $paid = $true
        $trip = $polled
        break
    }
}

if ($paid) {
    $cur = if ($trip.currency) { $trip.currency } else { $trip.fareCurrency }
    if ($cur -ne "VND" -or $trip.fareAmount -le 0) {
        Write-Error "`nFAILURE: Trip reached Paid status with invalid fare: $($trip.fareAmount) $cur"
        exit 1
    }
    Write-Host "`nSUCCESS: Trip $tripId reached Paid status!" -ForegroundColor Green
    Write-Host "  Fare: $($trip.fareAmount) $cur"
    Write-Host "  Payment ID: $($trip.paymentId)"
} else {
    Write-Error "`nFAILURE: Trip did not reach Paid status within timeout. Current status: $($trip.status)"
    exit 1
}
