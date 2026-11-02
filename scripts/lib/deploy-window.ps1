<#
    When a deploy may build and restart on a Windows box: the clock half of the
    Linux desk's gate (deploy_clock_allows in scripts/lib/desk-common.sh, with its
    default 08:40 and 23:58). Weekends, or weekdays before the morning job and
    after the evening close, in IST whatever this machine's own time zone is.

    As on the desk, an exchange holiday counts as a weekday: a wait until the
    night, never a deploy in a session.

    Returns why a deploy must wait, or $null when the clock allows it.
#>
function Get-DeployBlockReason {
    $ist = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow, 'India Standard Time')
    if ($ist.DayOfWeek -eq [DayOfWeek]::Saturday -or $ist.DayOfWeek -eq [DayOfWeek]::Sunday) { return $null }
    $hhmm = $ist.Hour * 100 + $ist.Minute
    if ($hhmm -lt 840 -or $hhmm -ge 2358) { return $null }
    return "market hours ($($ist.ToString('HH:mm')) IST on a weekday, waits for 23:58)"
}
