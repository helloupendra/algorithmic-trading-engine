#Requires -Version 5.1
<#
.SYNOPSIS
    AlgoTrading - pull what was pushed from elsewhere and put it live.

.DESCRIPTION
    Runs one check: fetch origin, and if this machine is behind, fast-forward
    and rebuild whatever actually changed. Meant to be run on a schedule (see
    -Install), not left running as a daemon - a scheduled one-shot has nothing
    to babysit and nothing to leak.

    It keeps the Linux desk's rules (scripts/desk.sh, scripts/lib/desk-common.sh)
    as far as is simple here. Not carried over: the desk's last good build, and
    its wait for a database migration under way.

    Polling rather than a webhook is deliberate. A webhook would have to arrive
    on the Cloudflare quick tunnel, whose hostname changes every time
    cloudflared restarts, so the hook would silently break on the next reboot.
    A pull needs no inbound anything.

    WHAT IT REFUSES TO DO. This box runs live trading and holds broker
    credentials, so the script is built to stop rather than guess:

      * uncommitted local changes    -> skips, touches nothing
      * local commits not on origin  -> skips (a merge here could lose work)
      * not a fast-forward           -> skips (git itself refuses)
      * market hours on a weekday,   -> a commit that changes the console or
        or a strategy is running        the API is not even pulled, and goes out
                                        by itself at a later check once the desk
                                        is quiet; a restart drops the in-memory
                                        run registry and with it the risk guard
                                        enforcing that run's stop-loss. The API
                                        is only asked outside market hours, so
                                        waiting signs in nobody through a session
      * an API that does not build   -> the running API is left alone, nothing of
                                        the commit goes out, and the checkout goes
                                        back. The commit is not tried again until
                                        logs\auto-deploy-failed-sha is deleted;
                                        the next commit gets its own attempt

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
# The commit whose wait was already announced, once per commit; and the commit
# whose API did not build. logs\ is not tracked, so neither dirties the checkout.
$DeferredShaFile = Join-Path $RepoRoot 'logs\auto-deploy-deferred-sha'
$FailedShaFile = Join-Path $RepoRoot 'logs\auto-deploy-failed-sha'
. (Join-Path $PSScriptRoot 'lib\deploy-window.ps1')

function Read-ShaFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return '' }
    return (Get-Content $Path -Raw -EA SilentlyContinue | Out-String).Trim()
}

function Write-ShaFile {
    param([string]$Path, [string]$Sha)
    try {
        Set-Content -Path $Path -Value $Sha -Encoding ascii
    } catch {
        Write-Log "  (could not write $Path`: $($_.Exception.Message))" 'Yellow'
    }
}

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

<#
    Writes what this run actually did, as structured JSON the console can render.

    The Telegram message is prose for a phone; this is the record. It exists
    because "did the machine take my push, and is it live?" is a question with
    four separate answers - pulled, console rebuilt, API restarted, engine left
    alone - and a paragraph cannot be read at a glance while something is wrong.

    Newest first, capped: this is an operations feed, not an archive.
#>
$DeployHistoryFile = Join-Path $RepoRoot 'data\deploy-history.json'
$script:Steps = New-Object System.Collections.Generic.List[object]
$script:StartedUtc = (Get-Date).ToUniversalTime().ToString('o')

function Add-Step {
    param(
        [string]$Name,
        [ValidateSet('ok', 'skipped', 'failed')][string]$Status,
        [string]$Detail
    )
    $script:Steps.Add([ordered]@{
        name   = $Name
        status = $Status
        detail = $Detail
        atUtc  = (Get-Date).ToUniversalTime().ToString('o')
    })
}

function Write-DeployRecord {
    param(
        [ValidateSet('applied', 'skipped', 'failed')][string]$Outcome,
        [string]$FromCommit = '',
        [string]$ToCommit = '',
        [string[]]$Commits = @(),
        [int]$FilesChanged = 0,
        [string]$Summary = ''
    )

    $record = [ordered]@{
        startedUtc   = $script:StartedUtc
        finishedUtc  = (Get-Date).ToUniversalTime().ToString('o')
        outcome      = $Outcome
        summary      = $Summary
        fromCommit   = $FromCommit
        toCommit     = $ToCommit
        commits      = @($Commits)
        filesChanged = $FilesChanged
        # .ToArray(), not @(...): wrapping a List[object] of ordered
        # hashtables in @() makes the enclosing [ordered] cast throw
        # "Argument types do not match" on PowerShell 5.1.
        steps        = $script:Steps.ToArray()
        machine      = $env:COMPUTERNAME
    }

    try {
        $history = @()
        if (Test-Path $DeployHistoryFile) {
            $existing = Get-Content $DeployHistoryFile -Raw -EA Stop | ConvertFrom-Json
            if ($existing) { $history = @($existing) }
        }
        # Newest first, and bounded - an unbounded file on a two-minute schedule
        # is a slow leak nobody notices until it is large.
        $history = @($record) + $history | Select-Object -First 40
        $dir = Split-Path -Parent $DeployHistoryFile
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        # -InputObject, not the pipeline: piping a one-element array sends a
        # single object down it, and the file then holds a bare object where the
        # reader expects a list.
        ConvertTo-Json -InputObject $history -Depth 6 |
            Set-Content -Path $DeployHistoryFile -Encoding utf8
    } catch {
        Write-Log "  (could not write the deploy record: $($_.Exception.Message))" 'Yellow'
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

# How many strategy runs are live; -1 when the API cannot be asked.
function Get-LiveRunCount {
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
        return @($runs | Where-Object { $_.isActive }).Count
    } catch {
        # If the API cannot be asked, assume the worst and protect the restart.
        Write-Log "  could not check for live runs ($($_.Exception.Message)); treating as live." 'Yellow'
        return -1
    }
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

# Its API did not build. Silent like the case above: said once, when it failed.
if ($remote -eq (Read-ShaFile $FailedShaFile)) { exit 0 }

$alreadyDeferred = $remote -eq (Read-ShaFile $DeferredShaFile)
if (-not $alreadyDeferred) {
    Write-Log "origin/main moved: $($local.Substring(0,7)) -> $($remote.Substring(0,7))" 'Cyan'
}

# --- refuse to destroy local work ------------------------------------------
$dirty = git status --porcelain
if ($dirty) {
    $n = ($dirty | Measure-Object -Line).Lines
    Write-Log "SKIPPED - $n uncommitted file(s) here. Commit or stash them first." 'Yellow'
Add-Step 'Pulled from GitHub' 'skipped' "$n uncommitted file(s) on this machine"
    Send-Alert 'Deploy skipped' "This machine has $n uncommitted change(s), so the pull was not attempted.`norigin/main is at $($remote.Substring(0,7))." 'warning'
Write-DeployRecord -Outcome 'skipped' -ToCommit $remote.Substring(0,7) -Summary 'Uncommitted changes on this machine'
    exit 0
}

$ahead = git rev-list origin/main..HEAD
if ($ahead) {
    $n = ($ahead | Measure-Object -Line).Lines
    Write-Log "SKIPPED - $n local commit(s) are not on origin. Push them first." 'Yellow'
Add-Step 'Pulled from GitHub' 'skipped' "$n local commit(s) are not on origin"
    Send-Alert 'Deploy skipped' "This machine has $n commit(s) that are not on origin/main, so it was not fast-forwarded." 'warning'
Write-DeployRecord -Outcome 'skipped' -ToCommit $remote.Substring(0,7) -Summary 'Local commits are not pushed yet'
    exit 0
}

if ($DryRun) {
    Write-Log "DRY RUN - would fast-forward and deploy:" 'Cyan'
    git diff --name-only HEAD origin/main | ForEach-Object { Write-Log "    $_" }
    exit 0
}

# --- may it go out now? -----------------------------------------------------
# What the commit changes, with the same patterns as the Linux desk (desk.sh,
# api_build_inputs; desk-common.sh, console_build_inputs). Until 2 Nov only *.cs
# counted for the API, so a csproj or appsettings change was never rebuilt.
$incoming = git diff --name-only $local $remote
$webChanged = @($incoming | Where-Object { $_ -match '^(web/|docs/|scripts/aws/README\.md$)' }).Count -gt 0
$apiChanged = @($incoming | Where-Object { $_ -match '^(src/AlgoTrading\.(Api|Application|Domain|Infrastructure|Contracts)/|Directory\.(Build|Packages)\.[^/]+$|global\.json$|[Nn][Uu][Gg]et\.[Cc]onfig$)' }).Count -gt 0
$engineChanged = @($incoming | Where-Object { $_ -like 'src/AlgoTrading.PythonEngine/*' }).Count -gt 0

# Decided before the pull. A commit pulled and then held was lost until 2 Nov:
# the next check found this machine level with origin and had nothing left to
# do, so the change was never built. Now it stays behind and every check asks.
if ($webChanged -or $apiChanged) {
    $blocked = Get-DeployBlockReason
    if (-not $blocked) {
        $liveRuns = Get-LiveRunCount
        if ($liveRuns -lt 0) { $blocked = 'the live-run count is unknown, so runs are assumed live' }
        elseif ($liveRuns -gt 0) { $blocked = "$liveRuns run(s) are live" }
    }
    if ($blocked) {
        if (-not $alreadyDeferred) {
            Write-Log "  deferred - $blocked. Nothing is pulled; it goes out by itself when the desk is quiet." 'Yellow'
            Add-Step 'Pulled from GitHub' 'skipped' "Not pulled until the deploy can go out: $blocked"
            Send-Alert 'Deploy deferred' "$($remote.Substring(0,7)) waits: $blocked." 'info'
            Write-DeployRecord -Outcome 'skipped' -ToCommit $remote.Substring(0,7) -Summary "Deferred: $blocked"
            Write-ShaFile $DeferredShaFile $remote
        }
        exit 0
    }
}

# --- fast-forward -----------------------------------------------------------
$before = $local
git merge --ff-only origin/main --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Log "SKIPPED - not a fast-forward; the branches have diverged." 'Red'
Add-Step 'Pulled from GitHub' 'failed' 'Not a fast-forward - the branches have diverged'
    Send-Alert 'Deploy skipped' 'origin/main and this machine have diverged, so no merge was attempted. Resolve it by hand.' 'error'
Write-DeployRecord -Outcome 'skipped' -ToCommit $remote.Substring(0,7) -Summary 'Branches have diverged - resolve by hand'
    exit 0
}
$after = (git rev-parse HEAD).Trim()
$changed = git diff --name-only $before $after
$subjects = git log --oneline "$before..$after"

Write-Log "Fast-forwarded to $($after.Substring(0,7)) - $(($changed | Measure-Object -Line).Lines) file(s) changed." 'Green'
Add-Step 'Pulled from GitHub' 'ok' "$($before.Substring(0,7)) -> $($after.Substring(0,7)), $(($changed | Measure-Object -Line).Lines) file(s)"

$done = New-Object System.Collections.Generic.List[string]
$deferred = New-Object System.Collections.Generic.List[string]

# The API first, as on the desk: a commit whose API does not build is not
# deployed at all. -Force because the window was decided above, and must not
# change its mind between that check and this line.
if ($apiChanged) {
    Write-Log "  the API changed and the desk is quiet - building, then restarting it." 'Cyan'
    & (Join-Path $PSScriptRoot 'deploy.ps1') -Api -Force
    $apiExit = $LASTEXITCODE
    if ($apiExit -eq 2) {
        Write-Log "  the API build FAILED - nothing of $($after.Substring(0,7)) goes out; the running API was left alone." 'Red'
        git reset --keep --quiet $before
        if ($LASTEXITCODE -eq 0) { Write-Log "  checkout put back to $($before.Substring(0,7))." }
        else { Write-Log "  could not put the checkout back to $($before.Substring(0,7))." 'Red' }
        Write-ShaFile $FailedShaFile $remote
        Add-Step 'API build' 'failed' "dotnet build failed - nothing of $($after.Substring(0,7)) went out; the API keeps running $($before.Substring(0,7))"
        Send-Alert 'Deploy FAILED' "$($after.Substring(0,7)) did not build, so nothing of it went out and the API keeps running $($before.Substring(0,7)). The next commit is tried by itself." 'error'
        Write-DeployRecord -Outcome 'failed' -FromCommit $before.Substring(0,7) -ToCommit $after.Substring(0,7) `
            -Commits @($subjects) -FilesChanged (($changed | Measure-Object -Line).Lines) -Summary 'API build FAILED - not deployed'
        exit 0
    }
    if ($apiExit -eq 0) { $done.Add('API rebuilt and restarted'); Add-Step 'API rebuilt and restarted' 'ok' 'Backend is live on the new build' }
    else { $deferred.Add('API restart FAILED - it did not answer after the restart'); Add-Step 'API rebuilt and restarted' 'failed' 'It built, but did not answer after the restart - see logs/api-stdout.log' }
}

if ($webChanged) {
    Write-Log "  the console changed - rebuilding it (no restart needed)." 'Cyan'
    & (Join-Path $PSScriptRoot 'deploy.ps1') -Web
    if ($LASTEXITCODE -eq 0) { $done.Add('console rebuilt'); Add-Step 'Console rebuilt' 'ok' 'New bundle is being served - no restart needed' }
    else { $deferred.Add('console build FAILED'); Add-Step 'Console rebuilt' 'failed' 'Build failed - the old bundle is still being served' }
}

if ($engineChanged) {
    # Restarting the engine would kill whatever it is running mid-position.
    $deferred.Add('Python engine changed - restart the ingestor/runner yourself when the desk is quiet')
    Add-Step 'Python engine' 'skipped' 'Changed - restart the ingestor/runner when the desk is quiet'
}

if (-not $webChanged -and -not $apiChanged -and -not $engineChanged) {
    $done.Add('nothing that runs here changed (docs or config only)')
    Add-Step 'Nothing to rebuild' 'ok' 'Only docs or config changed'
}

# --- say what happened ------------------------------------------------------
$lines = @("Pulled $($before.Substring(0,7)) -> $($after.Substring(0,7))", '')
$subjects | Select-Object -First 8 | ForEach-Object { $lines += "  $_" }
if ($done.Count) { $lines += ''; $lines += 'Applied:'; $done | ForEach-Object { $lines += "  - $_" } }
if ($deferred.Count) { $lines += ''; $lines += 'Needs you:'; $deferred | ForEach-Object { $lines += "  - $_" } }

$severity = 'success'
if ($deferred.Count) { $severity = 'warning' }
Send-Alert 'Deploy' ($lines -join "`n") $severity
$outcome = 'applied'
if ($deferred.Count) { $outcome = 'failed' }
$summary = 'Pulled and live'
if ($deferred.Count) { $summary = 'Pulled, but something still needs you' }
Write-DeployRecord -Outcome $outcome -FromCommit $before.Substring(0,7) `
    -ToCommit $after.Substring(0,7) -Commits @($subjects) `
    -FilesChanged (($changed | Measure-Object -Line).Lines) -Summary $summary
Write-Log "Done." 'Green'
