#Requires -Version 5.1
<#
.SYNOPSIS
    AlgoTrading - push local changes to the running instance.

.DESCRIPTION
    The console is served by the API out of src/AlgoTrading.Api/wwwroot, and the
    Cloudflare tunnel points at the API. So "going live" is not a deploy to a
    server - it is rebuilding into wwwroot, and restarting the API only when the
    backend itself changed.

    Which switch you need depends on what you edited:

      web/**            -> -Web   (no restart; the API serves wwwroot from disk)
      src/**/*.cs       -> -Api   (rebuild + restart; a few seconds of downtime)
      both              -> -All

    -Api follows the same rules as the Linux desk (scripts/lib/desk-common.sh):
    it builds first with the API still running, stops the API only when that
    build passed, and does nothing in market hours on a weekday without -Force.

    Exit codes for -Api: 0 done; 2 the build failed and the running API was left
    alone; 3 the API did not answer after the restart; 4 refused, market hours.

    The public URL does NOT change here. It belongs to the cloudflared process,
    not to the API - restarting the API leaves the tunnel connected and it
    resumes as soon as the API answers again. Only stopping cloudflared itself
    (or rebooting) gets you a new *.trycloudflare.com address, because a quick
    tunnel is assigned a fresh random hostname every time it starts.

.PARAMETER Web
    Build the React client with relative API URLs and copy it into wwwroot.

.PARAMETER Api
    Build the API and, when that passes, restart it.

.PARAMETER All
    Both of the above.

.PARAMETER NoRestart
    With -Api, check that it builds and leave the running API alone.

.PARAMETER Force
    With -Api, go ahead in market hours. A build on a busy box has cost a session's
    ticks before now (scripts/lib/desk-common.sh has the dates): only for a hotfix.

.EXAMPLE
    .\scripts\deploy.ps1 -Web

.EXAMPLE
    .\scripts\deploy.ps1 -All
#>
[CmdletBinding()]
param(
    [switch]$Web,
    [switch]$Api,
    [switch]$All,
    [switch]$NoRestart,
    [switch]$Force
)

# Deliberately not 'Stop'. Windows PowerShell 5.1 turns anything a native exe
# writes to stderr into an ErrorRecord, and both npm and dotnet write ordinary
# warnings there - vite's chunk-size notice alone was enough to abort the whole
# script on a successful build. Real failure is read from $LASTEXITCODE instead,
# which is the only thing that actually says whether the tool worked.
$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot
. (Join-Path $PSScriptRoot 'lib\deploy-window.ps1')

if ($All) { $Web = $true; $Api = $true }
if (-not $Web -and -not $Api) {
    # The common case by a wide margin, and the harmless one.
    Write-Host "No target given; defaulting to -Web." -ForegroundColor Yellow
    $Web = $true
}

$ApiUrl = 'http://localhost:5025'

function Wait-ForApi {
    param([int]$TimeoutSeconds = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-WebRequest -Uri "$ApiUrl/" -UseBasicParsing -TimeoutSec 3 | Out-Null
            return $true
        } catch {
            # A refused connection here is the API still coming up, not a failure.
            Start-Sleep -Seconds 2
        }
    }
    return $false
}

# ---------------------------------------------------------------- web ------
if ($Web) {
    Write-Host "==> Building the web client..." -ForegroundColor Cyan

    # No VITE_API_BASE_URL here on purpose. Setting it to '' looked like the
    # way to ask for relative URLs, but PowerShell DELETES an environment
    # variable assigned an empty string, so vite never saw it and the bundle
    # fell back to the absolute http://localhost:5025 - which a phone resolves
    # to itself. The default now lives in web/src/lib/api.ts, where a
    # production build is relative without anyone having to remember this.
    Push-Location "$RepoRoot\web"
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { Write-Error "npm run build failed."; exit 1 }
    } finally {
        Pop-Location
    }

    Write-Host "==> Copying the build into wwwroot..." -ForegroundColor Cyan
    $wwwroot = "$RepoRoot\src\AlgoTrading.Api\wwwroot"
    if (Test-Path $wwwroot) { Remove-Item -Recurse -Force $wwwroot }
    New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
    Copy-Item -Recurse -Force "$RepoRoot\web\dist\*" $wwwroot

    $bundleFile = Get-ChildItem "$wwwroot\assets" -Filter 'index-*.js' | Select-Object -First 1
    Write-Host "    wwwroot now holds $($bundleFile.Name)" -ForegroundColor Green

    # A bundle that names localhost is a bundle that only works on this
    # machine. That shipped undetected for hours because nothing checked, and
    # the symptom only shows on a different device - so the check lives here,
    # where it cannot be forgotten.
    if (Select-String -Path $bundleFile.FullName -Pattern 'localhost:5025' -SimpleMatch -Quiet) {
        Write-Host "    REFUSING THIS BUILD: the bundle contains an absolute localhost:5025 URL." -ForegroundColor Red
        Write-Host "    It would work here and fail on every phone and remote browser." -ForegroundColor Red
        Write-Host "    Check VITE_API_BASE_URL and the default in web/src/lib/api.ts." -ForegroundColor Red
        exit 1
    }
    Write-Host "    Bundle uses relative API paths - safe behind the tunnel." -ForegroundColor Green
    Write-Host "    No restart needed - refresh the browser (Ctrl+Shift+R)." -ForegroundColor Green
}

# ---------------------------------------------------------------- api ------
if ($Api) {
    $blocked = Get-DeployBlockReason
    if ($blocked -and -not $Force) {
        Write-Host "==> NOT building or restarting the API: $blocked. Run it after the close, or give -Force for a hotfix." -ForegroundColor Yellow
        exit 4
    }

    Write-Host "==> Checking for live runs before touching the API..." -ForegroundColor Cyan
    # Restarting drops the in-memory run registry, which is what the risk guard
    # sweeps. Stopping the API under a live run means nothing is enforcing that
    # run's stop-loss until it is back.
    try {
        $probe = Invoke-RestMethod -Uri "$ApiUrl/api/MarketSession/check" -TimeoutSec 5 -EA Stop
        if ($probe.isMarketOpen) {
            Write-Host "    NSE is OPEN. If a strategy is running, stop it first." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "    (API not answering; continuing.)" -ForegroundColor DarkGray
    }

    # Into a folder of its own: the running exe keeps bin\ locked, which is why
    # the API used to be stopped first, and a build that then failed left it down
    # with nothing to bring it back. `dotnet run` below compiles for bin\ from the
    # same source, once the API is stopped.
    Write-Host "==> Building the API, with the running one left alone..." -ForegroundColor Cyan
    $buildCheckDir = Join-Path $env:TEMP 'algotrading-api-build-check'
    if (Test-Path $buildCheckDir) { Remove-Item -Recurse -Force $buildCheckDir -EA SilentlyContinue }
    dotnet build "$RepoRoot\src\AlgoTrading.Api\AlgoTrading.Api.csproj" -v q --nologo -o $buildCheckDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    dotnet build FAILED. The running API was not touched." -ForegroundColor Red
        exit 2
    }
    Write-Host "    Build succeeded." -ForegroundColor Green

    if ($NoRestart) {
        Write-Host "    -NoRestart given; the API was not restarted." -ForegroundColor Yellow
    } else {
        $apiProcs = Get-CimInstance Win32_Process -Filter "Name='AlgoTrading.Api.exe'" -EA SilentlyContinue
        if ($apiProcs) {
            Write-Host "    Stopping the running API..." -ForegroundColor DarkGray
            foreach ($p in $apiProcs) { Stop-Process -Id $p.ProcessId -Force -EA SilentlyContinue }
            Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
                Where-Object { $_.CommandLine -like '*run --project src/AlgoTrading.Api*' } |
                ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
            Start-Sleep -Seconds 3
        }

        Write-Host "==> Starting the API..." -ForegroundColor Cyan
        # Production, and without the launch profile: launchSettings.json pins
        # ASPNETCORE_ENVIRONMENT to Development for both profiles, which leaves
        # Swagger served and stack traces in 500 responses - neither belongs on
        # something reachable from the internet.
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $env:ASPNETCORE_URLS = $ApiUrl
        Start-Process -FilePath 'dotnet' `
            -ArgumentList 'run', '--project', 'src/AlgoTrading.Api', '--no-launch-profile' `
            -WorkingDirectory $RepoRoot `
            -RedirectStandardOutput "$RepoRoot\logs\api-stdout.log" `
            -RedirectStandardError "$RepoRoot\logs\api-stderr.log" `
            -WindowStyle Hidden | Out-Null

        if (Wait-ForApi) {
            Write-Host "    API is up on $ApiUrl" -ForegroundColor Green
        } else {
            # An exit code, not a throw: a throw ends auto-deploy.ps1 too, before
            # it can say what happened.
            Write-Host "    The API did not answer in time. See logs\api-stdout.log." -ForegroundColor Red
            exit 3
        }
    }
}

# ------------------------------------------------------------- tunnel ------
$tunnel = Get-CimInstance Win32_Process -Filter "Name='cloudflared.exe'" -EA SilentlyContinue
if ($tunnel) {
    Write-Host ""
    Write-Host "Tunnel is still up (pid $($tunnel.ProcessId)) - the public URL is unchanged." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "cloudflared is NOT running. Starting it gives a NEW random URL:" -ForegroundColor Yellow
    Write-Host "  `$env:LOCALAPPDATA\cloudflared\cloudflared.exe tunnel --url $ApiUrl" -ForegroundColor DarkGray
}
