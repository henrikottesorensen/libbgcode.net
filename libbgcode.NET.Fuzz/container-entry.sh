#!/usr/bin/env bash
set -euo pipefail

# Runs inside the fuzzing container (see Dockerfile / fuzz.sh).
HARNESS="${1:-reader}"
DURATION="${2:-60}"
CORPUS="/corpus/$HARNESS"

if [ ! -d "$CORPUS" ] || [ -z "$(ls -A "$CORPUS")" ]; then
    mkdir -p "$CORPUS"
    cp /seed-corpus/"$HARNESS"/* "$CORPUS"/
fi

# Crash inputs land in the mounted corpus root, so they survive the container.
cd /corpus

EXTRA=()
if [ "$DURATION" -gt 0 ]; then
    EXTRA+=("-max_total_time=$DURATION")
fi
exec libfuzzer-dotnet --target_path=/app/libbgcode.NET.Fuzz --target_arg="$HARNESS" \
    -timeout=10 ${EXTRA[@]+"${EXTRA[@]}"} "$CORPUS"
