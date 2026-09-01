#Requires -Version 5.1
<#
.SYNOPSIS
    AlgoTrading — load reference data into a running system.

.DESCRIPTION
    Run this AFTER 'dotnet run --project src/AlgoTrading.Api' is up, because the
    API creates the schema (EF Core migrations) on boot.

    Steps: wait for the API -> seed expiry rules -> import instrument masters.
    Safe to re-run.

.EXAMPLE
    .\scripts\load-data.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Write-Step { param([string]$Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "    OK  $Text" -ForegroundColor Green }
function Stop-WithError {
    param([string]$Text)
    Write-Host "`nERROR: $Text" -ForegroundColor Red
    exit 1
}

# Read a key out of .env without executing it.
function Get-EnvValue {
    param([string]$Key, [string]$Default = '')
    if (-not (Test-Path .env)) { return $Default }
    foreach ($line in Get-Content .env) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\s*#' -or $trimmed -notmatch '=') { continue }
        $name, $value = $trimmed -split '=', 2
        if ($name.Trim() -eq $Key) {
            return $value.Trim().Trim('"').Trim("'")
        }
    }
    return $Default
}

$apiBaseUrl    = Get-EnvValue 'API_BASE_URL'   'http://localhost:5025'
$postgresUser  = Get-EnvValue 'POSTGRES_USER'  'postgres'
$postgresDb    = Get-EnvValue 'POSTGRES_DB'    'algotrading'
if (-not $apiBaseUrl)   { $apiBaseUrl   = 'http://localhost:5025' }
if (-not $postgresUser) { $postgresUser = 'postgres' }
if (-not $postgresDb)   { $postgresDb   = 'algotrading' }

$adminUsername = Get-EnvValue 'ADMIN_USERNAME' 'admin'
$adminPassword = Get-EnvValue 'ADMIN_PASSWORD' 'admin'

# ---------------------------------------------------------------------------
Write-Step "1/3  Waiting for the API at $apiBaseUrl"
# ---------------------------------------------------------------------------
$deadline = (Get-Date).AddSeconds(120)
$ready = $false
Write-Host '    ' -NoNewline
while (-not $ready) {
    try {
        Invoke-WebRequest -Uri "$apiBaseUrl/swagger/index.html" -UseBasicParsing -TimeoutSec 5 *> $null
        $ready = $true
    } catch {
        if ((Get-Date) -ge $deadline) {
            Write-Host ''
            Stop-WithError "API did not respond within 120s.`n       Start it first:  dotnet run --project src/AlgoTrading.Api"
        }
        Write-Host '.' -NoNewline
        Start-Sleep -Seconds 2
    }
}
Write-Host ''
Write-Ok 'API is responding'

# ---------------------------------------------------------------------------
Write-Step '1.5/3 Logging in to the API'
# ---------------------------------------------------------------------------
$loginBody = @{ userNameOrEmail = $adminUsername; password = $adminPassword } | ConvertTo-Json -Compress
try {
    $loginResponse = Invoke-RestMethod -Uri "$apiBaseUrl/api/UserAuth/login" -Method Post -ContentType 'application/json' -Body $loginBody
    $token = $loginResponse.accessToken
    Write-Ok 'Successfully authenticated as admin'
} catch {
    $errBody = $_.Exception.Response.GetResponseStream()
    if ($errBody) {
        $reader = New-Object System.IO.StreamReader($errBody)
        $errText = $reader.ReadToEnd()
    }
    Stop-WithError "Failed to log in as $adminUsername. HTTP Status: $($_.Exception.Response.StatusCode). Error: $errText"
}

# ---------------------------------------------------------------------------
Write-Step '2/3  Seeding derivative expiry rules'
# ---------------------------------------------------------------------------
Get-Content database/seed/001_expiry_rules.sql -Raw |
    docker exec -i algotrading_db psql -v ON_ERROR_STOP=1 -q -U $postgresUser -d $postgresDb
if ($LASTEXITCODE -ne 0) {
    Stop-WithError 'Failed to apply database/seed/001_expiry_rules.sql - is the algotrading_db container running?'
}
Write-Ok 'expiry rules applied (NSE:BANKNIFTY, BSE:SENSEX)'

# ---------------------------------------------------------------------------
Write-Step '3/3  Importing instrument masters'
# ---------------------------------------------------------------------------
function Import-InstrumentCsv {
    param([string]$Label, [string]$RelativePath)

    $absolute = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path $absolute) -or (Get-Item $absolute).Length -eq 0) {
        Stop-WithError "$RelativePath is missing. Run .\scripts\setup.ps1 to download it."
    }
    $sizeMb = [math]::Round((Get-Item $absolute).Length / 1MB, 1)
    Write-Host "    importing $Label ($sizeMb MB) ... " -NoNewline

    # POST the path as JSON rather than a query parameter, so Windows
    # backslashes and spaces need no URL encoding.
    $body = @{ filePath = $absolute } | ConvertTo-Json -Compress
    $headers = @{ Authorization = "Bearer $token" }
    try {
        $response = Invoke-RestMethod -Uri "$apiBaseUrl/api/Instruments/import-local" `
                                      -Method Post `
                                      -Headers $headers `
                                      -ContentType 'application/json' `
                                      -Body $body
    } catch {
        Write-Host ''
        Stop-WithError "Import failed for $RelativePath`n       $($_.Exception.Message)"
    }
    Write-Host 'done'
    Write-Host "      $($response | ConvertTo-Json -Compress)"
}

Import-InstrumentCsv 'Cash Market'       'data/instruments/NSE_CM.csv'
Import-InstrumentCsv 'Futures & Options' 'data/instruments/NSE_FO.csv'

Write-Host ''
Write-Host 'Reference data loaded.' -ForegroundColor Green
@'

Start the Python engine next:

    .\.venv\Scripts\Activate.ps1
    $env:PYTHONPATH = "$PWD\src\AlgoTrading.PythonEngine"
    python src\AlgoTrading.PythonEngine\algo.py

'@ | Write-Host
