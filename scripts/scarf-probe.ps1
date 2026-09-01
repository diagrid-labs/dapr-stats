<#
.SYNOPSIS
    Probes a Scarf API endpoint and prints the status code and response body.

.DESCRIPTION
    Reads the API token from the SCARFAPITOKEN environment variable and never
    echoes it. Windows PowerShell 5.1 has no -SkipHttpErrorCheck, so a non-2xx
    response throws; the catch block recovers the status code and the API's own
    error body so a 403 or 404 is as readable as a success.

.PARAMETER Url
    The full Scarf API URL to request.

.PARAMETER MaxChars
    Truncate the printed response body to this many characters. Aggregation
    exports can be large and only the shape matters here.

.PARAMETER OutFile
    Also write the untruncated response body to this path, so a single download
    can be analysed repeatedly without hitting the API again.

.EXAMPLE
    .\scripts\scarf-probe.ps1 "https://api.scarf.sh/v2/pixels/dapr/overview?per_page=50"

.EXAMPLE
    .\scripts\scarf-probe.ps1 "https://api.scarf.sh/v2/tracking-pixels/dapr/<id>/events" -MaxChars 1500
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Url,

    [Parameter(Position = 1)]
    [int] $MaxChars = 4000,

    [Parameter()]
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:SCARFAPITOKEN)) {
    throw 'SCARFAPITOKEN is not set. Set it in this terminal before running the probe.'
}

$headers = @{ Authorization = "Bearer $env:SCARFAPITOKEN" }

try {
    $response = Invoke-WebRequest -Uri $Url -Headers $headers -UseBasicParsing
    "-- HTTP $($response.StatusCode)"
    $body = $response.Content
}
catch {
    "-- HTTP $($_.Exception.Response.StatusCode.value__)"
    $body = $_.ErrorDetails.Message
}

if ([string]::IsNullOrEmpty($body)) {
    '<empty body>'
    return
}

if ($OutFile) {
    # -Encoding utf8 because Set-Content defaults to the system ANSI codepage.
    Set-Content -Path $OutFile -Value $body -Encoding utf8 -NoNewline
    "-- wrote $($body.Length) chars to $OutFile"
}

$body.Substring(0, [Math]::Min($MaxChars, $body.Length))
