#!/bin/sh
set -eu

if [ -z "${METRICS_SCRAPE_TOKEN:-}" ]; then
    echo 'METRICS_SCRAPE_TOKEN is missing; run scripts/_gen_local_settings.py and recreate Prometheus.' >&2
    exit 1
fi
if [ "${#METRICS_SCRAPE_TOKEN}" -lt 32 ]; then
    echo 'METRICS_SCRAPE_TOKEN must contain at least 32 characters.' >&2
    exit 1
fi
case "$METRICS_SCRAPE_TOKEN" in
    *[[:space:]]*) echo 'METRICS_SCRAPE_TOKEN must not contain whitespace.' >&2; exit 1 ;;
esac

# Prometheus reads the bearer credential from a private file, not from a
# command-line argument or a generated configuration containing the secret.
umask 077
token_file="${METRICS_TOKEN_FILE:-/tmp/metrics-scrape-token}"
temporary="$(mktemp "${token_file}.XXXXXX")"
trap 'rm -f "$temporary"' EXIT
trap 'exit 1' HUP INT TERM
printf '%s' "$METRICS_SCRAPE_TOKEN" > "$temporary"
mv -f "$temporary" "$token_file"
trap - EXIT HUP INT TERM
unset METRICS_SCRAPE_TOKEN
exec prometheus "$@"
