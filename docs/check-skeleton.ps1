param([string]$BaseUrl = 'http://localhost:5080', [switch]$Production)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(30)
function Assert-Status([string]$Method, [string]$Path, [int]$Expected, [string]$Token = '') {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$BaseUrl$Path")
    if ($Token) { $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token) }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    try {
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne $Expected) { throw "$Method $Path expected $Expected, got $([int]$response.StatusCode)" }
        Write-Host "$Method $Path -> $Expected"
        return $body
    } finally { $response.Dispose(); $request.Dispose() }
}
try {
    $null = Assert-Status GET /health 200
    $null = Assert-Status POST /trips 401
    foreach ($path in @('/payments', '/payments/example/refund', '/match', '/quote')) {
        $null = Assert-Status POST $path 404
    }
    if ($Production) {
        $null = Assert-Status POST '/dev/token?userId=example&role=rider' 404
    } else {
        $null = Assert-Status POST '/dev/token?userId=example&role=admin' 400
        $token = (Assert-Status POST '/dev/token?userId=example&role=rider' 200 | ConvertFrom-Json).access_token
        $null = Assert-Status POST /trips 501 $token
        $null = Assert-Status POST /trips 401 "$token.invalid"
        foreach ($path in @('confirm-quote', 'cancel', 'driver-accept', 'driver-reject', 'en-route', 'pickup', 'dropoff')) {
            $null = Assert-Status POST "/trips/example/$path" 501 $token
        }
        $null = Assert-Status GET /trips/example 501 $token
        $driverToken = (Assert-Status POST '/dev/token?userId=example&role=driver' 200 | ConvertFrom-Json).access_token
        $null = Assert-Status PUT /drivers/example/location 501 $driverToken
    }
} finally { $client.Dispose() }
