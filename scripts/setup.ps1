#Requires -Version 5.1
<#
.SYNOPSIS
    AlgoTrading — one-command bootstrap for Windows.

.DESCRIPTION
    Checks prerequisites, prepares .env, starts the Docker infrastructure,
    generates appsettings.Local.json, downloads the FYERS instrument masters,
    builds the .NET solution and creates the Python virtual environment.

    Safe to re-run: every step is idempotent.

.PARAMETER Refresh
    Re-download the instrument master CSVs even if they already exist.

.PARAMETER SkipBuild
    Skip 'dotnet restore' and 'dotnet build'.

.EXAMPLE
    .\scripts\setup.ps1

.EXAMPLE
    .\scripts\setup.ps1 -Refresh
#>
[CmdletBinding()]
param(
    [switch]$Refresh,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Write-Step { param([string]$Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "    OK  $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "    !   $Text" -ForegroundColor Yellow }
function Stop-WithError {
    param([string]$Text)
    Write-Host "`nERROR: $Text" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
Write-Step '1/7  Checking prerequisites'
# ---------------------------------------------------------------------------
$missing = [System.Collections.Generic.List[string]]::new()

if (Get-Command docker -ErrorAction SilentlyContinue) {
    docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError 'Docker is installed but the daemon is not running. Start Docker Desktop and re-run.'
    }
    Write-Ok "docker $((docker --version) -replace '^Docker version ([^,]+),.*$', '$1')"
} else {
    $missing.Add('  - Docker Desktop       https://www.docker.com/products/docker-desktop/')
}

$compose = $null
docker compose version *> $null
if ($LASTEXITCODE -eq 0) {
    $compose = @('docker', 'compose')
    Write-Ok "docker compose $(docker compose version --short)"
} elseif (Get-Command docker-compose -ErrorAction SilentlyContinue) {
    $compose = @('docker-compose')
    Write-Ok 'docker-compose (legacy v1)'
} else {
    $missing.Add('  - Docker Compose       bundled with Docker Desktop')
}

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $dotnetVersion = (dotnet --version).Trim()
    $dotnetMajor = [int]($dotnetVersion -split '\.')[0]
    if ($dotnetMajor -lt 10) {
        Write-Warn ".NET SDK $dotnetVersion found, but this solution targets net10.0"
        $missing.Add('  - .NET SDK 10          https://dotnet.microsoft.com/download/dotnet/10.0')
    } else {
        Write-Ok ".NET SDK $dotnetVersion"
    }
} else {
    $missing.Add('  - .NET SDK 10          https://dotnet.microsoft.com/download/dotnet/10.0')
}

$python = $null
foreach ($candidate in @('python', 'python3', 'py')) {
    $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
    if (-not $cmd) { continue }
    # Skip the Microsoft Store stub, which exits 9009 instead of running.
    & $candidate -c 'import sys; sys.exit(0 if sys.version_info >= (3,10) else 1)' *> $null
    if ($LASTEXITCODE -eq 0) { $python = $candidate; break }
}
if ($python) {
    Write-Ok "Python $(& $python -c 'import platform;print(platform.python_version())') ($python)"
} else {
    $missing.Add('  - Python 3.10+         https://www.python.org/downloads/')
}

if ($missing.Count -gt 0) {
    Write-Host "`nMissing prerequisites:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host $_ }
    Stop-WithError 'Install the tools above, then re-run .\scripts\setup.ps1'
}

# ---------------------------------------------------------------------------
Write-Step '2/7  Preparing .env'
# ---------------------------------------------------------------------------
if (Test-Path .env) {
    Write-Ok '.env already exists (leaving it untouched)'
} else {
    Copy-Item .env.example .env
    Write-Ok 'created .env from .env.example'

    function New-Secret {
        param([int]$Length = 48)
        $bytes = [byte[]]::new($Length)
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        ([Convert]::ToBase64String($bytes) -replace '[/+=]', '').Substring(0, $Length)
    }

    $jwtSecret = New-Secret -Length 48
    $dbPassword = New-Secret -Length 32

    (Get-Content .env) | ForEach-Object {
        if ($_ -match '^JWT_SECRET_KEY=')    { "JWT_SECRET_KEY=$jwtSecret" }
        elseif ($_ -match '^POSTGRES_PASSWORD=') { "POSTGRES_PASSWORD=$dbPassword" }
        else { $_ }
    } | Set-Content .env -Encoding utf8

    Write-Ok 'generated a random JWT signing key and Postgres password'
    Write-Warn 'Add your FYERS_APP_ID and FYERS_SECRET_KEY to .env before trading.'
}

# ---------------------------------------------------------------------------
Write-Step '3/7  Starting infrastructure (PostgreSQL/TimescaleDB, Redis, Prometheus, Grafana)'
# ---------------------------------------------------------------------------
& $compose[0] @($compose[1..($compose.Count - 1)] + @('up', '-d'))
if ($LASTEXITCODE -ne 0) { Stop-WithError 'docker compose up failed.' }
Write-Ok 'containers requested'

Write-Host '    waiting for health checks' -NoNewline
$deadline = (Get-Date).AddSeconds(180)
while ($true) {
    $dbState    = (docker inspect -f '{{.State.Health.Status}}' algotrading_db    2>$null)
    $redisState = (docker inspect -f '{{.State.Health.Status}}' algotrading_redis 2>$null)
    if ($dbState -eq 'healthy' -and $redisState -eq 'healthy') { break }
    if ((Get-Date) -ge $deadline) {
        Write-Host ''
        Stop-WithError 'Timed out. Check: docker compose logs timescaledb redis'
    }
    Write-Host '.' -NoNewline
    Start-Sleep -Seconds 2
}
Write-Host ''
Write-Ok 'PostgreSQL and Redis are healthy'

# ---------------------------------------------------------------------------
Write-Step '4/7  Generating appsettings.Local.json from .env'
# ---------------------------------------------------------------------------
& $python scripts/_gen_local_settings.py
if ($LASTEXITCODE -ne 0) { Stop-WithError 'Failed to generate appsettings.Local.json' }

# ---------------------------------------------------------------------------
Write-Step '5/7  Downloading FYERS instrument masters'
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path data/instruments | Out-Null
function Get-InstrumentMaster {
    param([string]$Url, [string]$Destination)

    $name = Split-Path -Leaf $Destination
    if ((Test-Path $Destination) -and -not $Refresh -and (Get-Item $Destination).Length -gt 0) {
        $sizeMb = [math]::Round((Get-Item $Destination).Length / 1MB, 1)
        Write-Ok "$name already present ($sizeMb MB) - use -Refresh to update"
        return
    }
    $temp = "$Destination.part"
    # Progress rendering makes Invoke-WebRequest an order of magnitude slower.
    $previous = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
    } catch {
        Stop-WithError "Download failed: $Url`n       $($_.Exception.Message)"
    } finally {
        $ProgressPreference = $previous
    }
    Move-Item -Force $temp $Destination
    $sizeMb = [math]::Round((Get-Item $Destination).Length / 1MB, 1)
    Write-Ok "$name ($sizeMb MB)"
}
Get-InstrumentMaster 'https://public.fyers.in/sym_details/NSE_CM.csv' 'data/instruments/NSE_CM.csv'
Get-InstrumentMaster 'https://public.fyers.in/sym_details/NSE_FO.csv' 'data/instruments/NSE_FO.csv'

# ---------------------------------------------------------------------------
Write-Step '6/7  Building the .NET solution'
# ---------------------------------------------------------------------------
if ($SkipBuild) {
    Write-Warn 'skipped (-SkipBuild)'
} else {
    dotnet restore AlgoTrading.slnx --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'dotnet restore failed.' }
    dotnet build AlgoTrading.slnx --nologo -v quiet --no-restore
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'dotnet build failed.' }
    Write-Ok 'solution built'
}

# ---------------------------------------------------------------------------
Write-Step '7/7  Setting up the Python engine'
# ---------------------------------------------------------------------------
if (-not (Test-Path .venv)) {
    & $python -m venv .venv
    Write-Ok 'created virtualenv at .venv'
} else {
    Write-Ok '.venv already exists'
}
$venvPython = Join-Path $RepoRoot '.venv\Scripts\python.exe'
& $venvPython -m pip install --quiet --upgrade pip
& $venvPython -m pip install --quiet -r src/AlgoTrading.PythonEngine/requirements.txt
if ($LASTEXITCODE -ne 0) { Stop-WithError 'pip install failed.' }
Write-Ok 'Python dependencies installed'

# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Setup complete.' -ForegroundColor Green
@'

Next - run these in order:

  1. Start the API (leave it running; it applies DB migrations on boot):

       dotnet run --project src/AlgoTrading.Api

  2. In a second terminal, load reference data (expiry rules + instruments):

       .\scripts\load-data.ps1

  3. Activate the Python engine in that second terminal and launch it:

       .\.venv\Scripts\Activate.ps1
       $env:PYTHONPATH = "$PWD\src\AlgoTrading.PythonEngine"
       python src\AlgoTrading.PythonEngine\algo.py

Services:
  API + Swagger   http://localhost:5025/swagger
  Grafana         http://localhost:3000
  Prometheus      http://localhost:9090

'@ | Write-Host
