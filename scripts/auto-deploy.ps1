#Requires -Version 5.1
<#
.SYNOPSIS
    AlgoTrading - pull what was pushed from elsewhere and put it live.

.DESCRIPTION
    Runs one check: fetch origin, and if this machine is behind, fast-forward
    and rebuild whatever actually changed. Meant to be run on a schedule (see
    -Install), not left running as a daemon - a scheduled one-shot has nothing
    to babysit and nothing to leak.

    Polling rather than a webhook is deliberate. A webhook would have to arrive
    on the Cloudflare quick tunnel, whose hostname changes every time
    cloudflared restarts, so the hook would silently break on the next reboot.
    A pull needs no inbound anything.

    WHAT IT REFUSES TO DO. This box runs live trading and holds broker
    credentials, so the script is built to stop rather than guess:

      * uncommitted local changes    -> skips, touches nothing
      * local commits not on origin  -> skips (a merge here could lose work)
      * not a fast-forward           -> skips (git itself refuses)
      * a strategy is running        -> deploys the frontend, but will NOT
                                        restart the API, because a restart drops
                                        the in-memory run registry and with it
                                        the risk guard enforcing that run's
                                        stop-loss

    Every outcome is announced on the platform's own alert path
    (Redis alerts:new -> Telegram), so a deploy that landed and a deploy that
    was skipped are both visible without reading a log file.

.PARAMETER Install
    Register the scheduled task that runs this every few minutes.

.PARAMETER Uninstall
    Remove that scheduled task.

.PARAMETER IntervalMinutes
    Minutes between checks when installing. Default 3.

.PARAMETER DryRun
    Report what would happen; change nothing.

.EXAMPLE
    .\scripts\auto-deploy.ps1

.EXAMPLE
    .\scripts\auto-deploy.ps1 -Install -IntervalMinutes 2
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Uninstall,
    [int]$IntervalMinutes = 3,
    [switch]$DryRun
)

# See deploy.ps1: PowerShell 5.1 treats a native tool's stderr as an error, and
# git writes ordinary progress there.
$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$TaskName = 'AlgoTrading auto-deploy'
$LogFile = Join-Path $RepoRoot 'logs\auto-deploy.log'

function Write-Log {
    param([string]$Message, [string]$Colour = 'Gray')
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line -ForegroundColor $Colour
    try {
        $dir = Split-Path -Parent $LogFile
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        Add-Content -Path $LogFile -Value $line -Encoding utf8
    } catch {
        # A log that cannot be written must not stop a deploy.
    }
}

<#
    Announce on the same channel the platform uses for everything else. Keys are
    PascalCase because AlertSubscriberService deserialises case-sensitively -
    lowercase ones bind to nothing and the row lands blank.
#>
function Send-Alert {
    param([string]$Title, [string]$Message, [string]$Severity = 'info')
    if ($DryRun) { return }
    $payload = @{
        Title      = $Title
        Message    = $Message
        Source     = 'process'
        Severity   = $Severity
        Underlying = 'DEPLOY'
    } | ConvertTo-Json -Compress
    try {
        docker exec algotrading_redis redis-cli PUBLISH alerts:new "$payload" | Out-Null
    } catch {
        Write-Log "  (could not publish the alert: $($_.Exception.Message))"
    }
}

# --------------------------------------------------------------- install ----
if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -EA SilentlyContinue
    Write-Log "Removed the scheduled task '$TaskName'." 'Yellow'
    exit 0
}

if ($Install) {
    $ps = (Get-Command powershell.exe).Source
    $action = New-ScheduledTaskAction -Execute $ps `
        -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$PSCommandPath`"" `
        -WorkingDirectory $RepoRoot
    # schtasks.exe rather than Register-ScheduledTask.
    #
    # The cmdlet writes into the root task folder, which needs elevation on a
    # normal Windows install - it fails with a bare "Access is denied" that says
    # nothing about why. schtasks creates the task in the calling user's own
    # context and usually succeeds without a prompt. It also takes the schedule
    # directly, so the RepetitionDuration that Task Scheduler rejected
    # ([TimeSpan]::MaxValue serialises to P99999999DT23H59M59S) never appears.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    # Through wscript.exe and a VBS shim, not powershell.exe directly: a task
    # that runs powershell flashes a console window on screen every time it
    # fires, which on a two-minute schedule is a black rectangle blinking at the
    # operator all day. -WindowStyle Hidden does not fix it - the window is
    # created and then hidden. wscript has no console to begin with.
    $shim = Join-Path $PSScriptRoot 'run-hidden.vbs'
    if (-not (Test-Path $shim)) {
        Write-Log "FAILED: $shim is missing." 'Red'
        exit 1
    }
    $command = "wscript.exe `"$shim`" `"$PSCommandPath`""

    $output = & schtasks /Create /F /TN $TaskName /TR $command /SC MINUTE /MO $IntervalMinutes 2>&1
    $created = $LASTEXITCODE -eq 0

    if (-not $created) {
        Write-Log "FAILED to install '$TaskName'." 'Red'
        Write-Log "  schtasks said: $($output -join ' ')" 'Red'
        if (-not $elevated) {
            Write-Log "  This shell is NOT running as Administrator, which is the usual cause." 'Yellow'
            Write-Log "  Right-click PowerShell -> Run as administrator, then run this again." 'Yellow'
        }
        exit 1
    }

    # Read it back. An earlier version printed "Installed" whatever happened, so
    # a rejected registration still looked like a success - the one outcome
    # where being told the truth actually matters.
    $registered = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $registered) {
        Write-Log "schtasks reported success but the task is not there. Not installed." 'Red'
        exit 1
    }

    Write-Log "Installed '$TaskName' - every $IntervalMinutes minute(s)." 'Green'
    Write-Log "  state: $($registered.State)"
    Write-Log "Remove it with: .\scripts\auto-deploy.ps1 -Uninstall"
    exit 0
}

# ------------------------------------------------------------------ run -----
Set-Location $RepoRoot

git fetch origin --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Log "git fetch failed; leaving everything alone." 'Yellow'
    exit 0
}

$local = (git rev-parse HEAD).Trim()
$remote = (git rev-parse origin/main).Trim()

if ($local -eq $remote) {
    # The overwhelmingly common case. Silent on purpose: this runs every few
    # minutes and a log line per check would bury the ones that matter.
    exit 0
}

Write-Log "origin/main moved: $($local.Substring(0,7)) -> $($remote.Substring(0,7))" 'Cyan'

# --- refuse to destroy local work ------------------------------------------
$dirty = git status --porcelain
if ($dirty) {
    $n = ($dirty | Measure-Object -Line).Lines
    Write-Log "SKIPPED - $n uncommitted file(s) here. Commit or stash them first." 'Yellow'
    Send-Alert 'Deploy skipped' "This machine has $n uncommitted change(s), so the pull was not attempted.`norigin/main is at $($remote.Substring(0,7))." 'warning'
    exit 0
}

$ahead = git rev-list origin/main..HEAD
if ($ahead) {
    $n = ($ahead | Measure-Object -Line).Lines
    Write-Log "SKIPPED - $n local commit(s) are not on origin. Push them first." 'Yellow'
    Send-Alert 'Deploy skipped' "This machine has $n commit(s) that are not on origin/main, so it was not fast-forwarded." 'warning'
    exit 0
}

if ($DryRun) {
    Write-Log "DRY RUN - would fast-forward and deploy:" 'Cyan'
    git diff --name-only HEAD origin/main | ForEach-Object { Write-Log "    $_" }
    exit 0
}

# --- fast-forward -----------------------------------------------------------
$before = $local
git merge --ff-only origin/main --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Log "SKIPPED - not a fast-forward; the branches have diverged." 'Red'
    Send-Alert 'Deploy skipped' 'origin/main and this machine have diverged, so no merge was attempted. Resolve it by hand.' 'error'
    exit 0
}
$after = (git rev-parse HEAD).Trim()
$changed = git diff --name-only $before $after
$subjects = git log --oneline "$before..$after"

Write-Log "Fast-forwarded to $($after.Substring(0,7)) - $(($changed | Measure-Object -Line).Lines) file(s) changed." 'Green'

$webChanged = @($changed | Where-Object { $_ -like 'web/*' }).Count -gt 0
$apiChanged = @($changed | Where-Object { $_ -like 'src/*' -and $_ -like '*.cs' }).Count -gt 0
$engineChanged = @($changed | Where-Object { $_ -like 'src/AlgoTrading.PythonEngine/*' }).Count -gt 0

# --- is anything trading right now? ----------------------------------------
$liveRuns = 0
try {
    $env_ = @{}
    Get-Content (Join-Path $RepoRoot '.env') -EA Stop | ForEach-Object {
        if ($_ -match '^\s*([A-Z_0-9]+)\s*=\s*(.*)$') { $env_[$Matches[1]] = $Matches[2].Trim() }
    }
    $auth = Invoke-RestMethod -Uri 'http://localhost:5025/api/UserAuth/login' -Method Post `
        -ContentType 'application/json' -TimeoutSec 10 `
        -Body (@{ userNameOrEmail = $env_['ADMIN_USERNAME']; password = $env_['ADMIN_PASSWORD'] } | ConvertTo-Json)
    $runs = Invoke-RestMethod -Uri 'http://localhost:5025/api/Strategy/runs' -TimeoutSec 15 `
        -Headers @{ Authorization = "Bearer $($auth.accessToken)" }
    $liveRuns = @($runs | Where-Object { $_.isActive }).Count
} catch {
    # If the API cannot be asked, assume the worst and protect the restart.
    Write-Log "  could not check for live runs ($($_.Exception.Message)); treating as live." 'Yellow'
    $liveRuns = -1
}

$done = New-Object System.Collections.Generic.List[string]
$deferred = New-Object System.Collections.Generic.List[string]

if ($webChanged) {
    Write-Log "  web/ changed - rebuilding the console (no restart needed)." 'Cyan'
    & (Join-Path $PSScriptRoot 'deploy.ps1') -Web
    if ($LASTEXITCODE -eq 0) { $done.Add('console rebuilt') }
    else { $deferred.Add('console build FAILED - the old bundle is still being served') }
}

if ($apiChanged) {
    if ($liveRuns -eq 0) {
        Write-Log "  src/**.cs changed and nothing is running - rebuilding and restarting the API." 'Cyan'
        & (Join-Path $PSScriptRoot 'deploy.ps1') -Api
        if ($LASTEXITCODE -eq 0) { $done.Add('API rebuilt and restarted') }
        else { $deferred.Add('API build FAILED') }
    } else {
        $who = if ($liveRuns -lt 0) { 'the API could not be reached' } else { "$liveRuns run(s) are live" }
        Write-Log "  src/**.cs changed but $who - NOT restarting." 'Yellow'
        $deferred.Add("API changes are pulled but NOT built or restarted ($who). Run: .\scripts\deploy.ps1 -Api")
    }
}

if ($engineChanged) {
    # Restarting the engine would kill whatever it is running mid-position.
    $deferred.Add('Python engine changed - restart the ingestor/runner yourself when the desk is quiet')
}

if (-not $webChanged -and -not $apiChanged -and -not $engineChanged) {
    $done.Add('nothing that runs here changed (docs or config only)')
}

# --- say what happened ------------------------------------------------------
$lines = @("Pulled $($before.Substring(0,7)) -> $($after.Substring(0,7))", '')
$subjects | Select-Object -First 8 | ForEach-Object { $lines += "  $_" }
if ($done.Count) { $lines += ''; $lines += 'Applied:'; $done | ForEach-Object { $lines += "  - $_" } }
if ($deferred.Count) { $lines += ''; $lines += 'Needs you:'; $deferred | ForEach-Object { $lines += "  - $_" } }

$severity = 'success'
if ($deferred.Count) { $severity = 'warning' }
Send-Alert 'Deploy' ($lines -join "`n") $severity
Write-Log "Done." 'Green'
