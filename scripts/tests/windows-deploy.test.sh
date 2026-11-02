#!/usr/bin/env bash
# shellcheck disable=SC2319  # "$(cond; echo $?)" is how check() takes a result
# The Windows deploy scripts keep the Linux desk's rules (2 Nov review, F6).
#
# These checks only READ the .ps1 files: the order of what they do, and that the
# hours and patterns match the desk's. PowerShell does not run here. CI parses the
# files on a runner that has it (.github/workflows/ci.yml), but nothing in the repo
# executes them, so a change to them must still be tried on the Windows box.
# Run: bash scripts/tests/windows-deploy.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

line_of() { grep -nF -- "$2" "$1" | head -1 | cut -d: -f1; }
before() {  # file, first text, second text: 0 when the first comes before the second
  local a b
  a="$(line_of "$1" "$2")"; b="$(line_of "$1" "$3")"
  [ -n "$a" ] && [ -n "$b" ] && [ "$a" -lt "$b" ]
}

DEPLOY=scripts/deploy.ps1
AUTO=scripts/auto-deploy.ps1
WINDOW=scripts/lib/deploy-window.ps1

echo "deploy.ps1 -Api"
check "market hours refused before anything is built" "$(before "$DEPLOY" 'Get-DeployBlockReason' 'dotnet build'; echo $?)"
check "and -Force overrides it" "$(grep -qF 'if ($blocked -and -not $Force)' "$DEPLOY"; echo $?)"
check "builds before it stops the API" "$(before "$DEPLOY" 'dotnet build' 'Stop-Process'; echo $?)"
check "into a folder of its own: bin is locked by the running exe" "$(grep -qE 'dotnet build .* -o \$buildCheckDir' "$DEPLOY"; echo $?)"
check "a failed build exits 2 before the stop" "$(before "$DEPLOY" 'exit 2' 'Stop-Process'; echo $?)"
check "no throw: it would end auto-deploy.ps1 before it could report" "$(! grep -qE '^\s*throw\b' "$DEPLOY"; echo $?)"

echo "auto-deploy.ps1"
check "the gate comes before the pull, so a held commit is not lost" "$(before "$AUTO" '$blocked = Get-DeployBlockReason' 'git merge --ff-only'; echo $?)"
check "the API is asked only when the clock allows it" "$(before "$AUTO" 'if (-not $blocked) {' '$liveRuns = Get-LiveRunCount'; echo $?)"
check "an unknown live-run count holds the deploy" "$(grep -qF 'if ($liveRuns -lt 0) { $blocked' "$AUTO"; echo $?)"
check "a deferral is announced once per commit" "$(grep -qF 'if (-not $alreadyDeferred) {' "$AUTO" && grep -qF 'Write-ShaFile $DeferredShaFile $remote' "$AUTO"; echo $?)"
check "the API is built before the console" "$(before "$AUTO" "'deploy.ps1') -Api" "'deploy.ps1') -Web"; echo $?)"
check "a failed build puts the checkout back, and is not retried every check" \
  "$(before "$AUTO" 'if ($apiExit -eq 2) {' 'git reset --keep --quiet $before' && grep -qF 'Write-ShaFile $FailedShaFile $remote' "$AUTO" && grep -qF '$remote -eq (Read-ShaFile $FailedShaFile)' "$AUTO"; echo $?)"

echo "The same rules as the desk"
desk_end="$(sed -n 's/^DEPLOY_MORNING_END="${DEPLOY_MORNING_END:-0*\([0-9]*\)}"$/\1/p' scripts/lib/desk-common.sh)"
desk_close="$(sed -n 's/^MARKET_CLOSE_AT_DEFAULT=//p' scripts/lib/desk-common.sh)"
check "the morning limit is the desk's ($desk_end)" "$(grep -qF "\$hhmm -lt $desk_end " "$WINDOW"; echo $?)"
check "the evening limit is the desk's ($desk_close)" "$(grep -qF "\$hhmm -ge $desk_close" "$WINDOW"; echo $?)"
check "the time is IST whatever the machine's zone" "$(grep -qF "'India Standard Time'" "$WINDOW"; echo $?)"
api_pattern="$(sed -n "s/^api_build_inputs() { grep -cE '\(.*\)' || true; }$/\1/p" scripts/desk.sh)"
check "the API's build inputs match desk.sh" "$(test -n "$api_pattern" && grep -qF -- "-match '$api_pattern'" "$AUTO"; echo $?)"
web_pattern="$(sed -n "s/^console_build_inputs() { grep -cE '\(.*\)' || true; }$/\1/p" scripts/lib/desk-common.sh)"
check "the console's build inputs match desk-common.sh" "$(test -n "$web_pattern" && grep -qF -- "-match '$web_pattern'" "$AUTO"; echo $?)"

echo "Files a Windows PowerShell 5.1 reads correctly"
# With no byte order mark it decodes a script as the machine's ANSI page: a dash
# from a keyboard shortcut turns into junk, and a string with one stops parsing.
for f in "$DEPLOY" "$AUTO" "$WINDOW"; do
  check "$f is plain ASCII" "$(! LC_ALL=C grep -q '[^[:print:][:space:]]' "$f"; echo $?)"
done

echo
if [ "$FAILS" -gt 0 ]; then echo "$FAILS check(s) FAILED"; exit 1; fi
echo "all passed"
