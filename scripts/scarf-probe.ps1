<#
.SYNOPSIS
    Probes a Scarf API endpoint and prints the status code and response body.

.DESCRIPTION
    Reads the API token from the environment variable named by -TokenVariable
    (SCARF_DAPR_API_TOKEN by default) and never echoes it. Windows PowerShell
    5.1 has no -SkipHttpErrorCheck, so a non-2xx response throws; the catch
    block recovers the status code and the API's own error body so a 403 or
    404 is as readable as a success.

.PARAMETER Url
    The full Scarf API URL to request.

.PARAMETER MaxChars
    Truncate the printed response body to this many characters. Aggregation
    exports can be large and only the shape matters here.

.PARAMETER OutFile
    Also write the untruncated response body to this path, so a single download
    can be analysed repeatedly without hitting the API again.

.PARAMETER TokenVariable
    Name of the environment variable holding the bearer token. Defaults to
    SCARF_DAPR_API_TOKEN; pass SCARF_DIAGRID_API_TOKEN to probe the Diagrid
    account instead.

.EXAMPLE
    .\scripts\scarf-probe.ps1 "https://api.scarf.sh/v2/pixels/dapr/overview?per_page=50"

.EXAMPLE
    .\scripts\scarf-probe.ps1 "https://api.scarf.sh/v2/tracking-pixels/dapr/<id>/events" -MaxChars 1500

.EXAMPLE
    .\scripts\scarf-probe.ps1 "https://api.scarf.sh/v2/pixels/Diagrid/overview?per_page=50" -TokenVariable SCARF_DIAGRID_API_TOKEN
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Url,

    [Parameter(Position = 1)]
    [int] $MaxChars = 4000,

    [Parameter()]
    [string] $OutFile,

    [Parameter()]
    [string] $TokenVariable = 'SCARF_DAPR_API_TOKEN'
)

$ErrorActionPreference = 'Stop'

$token = (Get-Item "env:$TokenVariable" -ErrorAction SilentlyContinue).Value

if ([string]::IsNullOrWhiteSpace($token)) {
    throw "$TokenVariable is not set. Set it in this terminal before running the probe."
}

$headers = @{ Authorization = "Bearer $token" }

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
