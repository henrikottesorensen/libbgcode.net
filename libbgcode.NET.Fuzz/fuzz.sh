#!/usr/bin/env bash
set -euo pipefail

# Coverage-guided fuzzing via SharpFuzz + libFuzzer.
#
# Usage: ./fuzz.sh [reader|meatpack] [seconds]
#   harness  "reader" (the whole container path, default) or
#            "meatpack" (the MeatPack decoder alone)
#   seconds  how long to fuzz; 0 means run until interrupted (default 60)
#
# libFuzzer's extra-counters transport (how the .NET coverage reaches the
# fuzzer) only exists on ELF platforms, so the pipeline runs natively on
# Linux and inside a Linux container (Docker) on macOS. The corpus lives
# in ./corpus on the host either way; crashing inputs are written next to
# it and can be re-run with:
#   dotnet run -- replay <harness> <crash-file>

HARNESS="${1:-reader}"
DURATION="${2:-60}"

case "$HARNESS" in
    reader|meatpack) ;;
    *) echo "unknown harness: $HARNESS (expected reader or meatpack)" >&2; exit 2 ;;
esac

cd "$(dirname "$0")"

if [ "$(uname)" = "Darwin" ]; then
    echo "-- macOS: running the fuzzing pipeline in a Linux container"
    docker build -t libbgcode-net-fuzz -f Dockerfile ..
    mkdir -p corpus
    exec docker run --rm -v "$PWD/corpus:/corpus" libbgcode-net-fuzz "$HARNESS" "$DURATION"
fi

TOOLS=.tools
BIN=bin/fuzz
CORPUS="corpus/$HARNESS"

# 1. Publish the harness and the library under test.
dotnet publish -c Release -o "$BIN" libbgcode.NET.Fuzz.csproj

# 2. Write the seed corpus on first run, using the uninstrumented build
#    (instrumented assemblies only run under the fuzzer).
if [ ! -d "$CORPUS" ] || [ -z "$(ls -A "$CORPUS")" ]; then
    dotnet bin/Release/net10.0/libbgcode.NET.Fuzz.dll seed corpus
fi

# 3. Instrument the library IL for coverage feedback (idempotent). Both
#    libraries: the reader harness exercises MeatPack.NET through ReadText,
#    and the meatpack harness exercises it alone.
if [ ! -x "$TOOLS/sharpfuzz" ]; then
    dotnet tool install --tool-path "$TOOLS" SharpFuzz.CommandLine
fi
for DLL in "$BIN/libbgcode.NET.dll" "$BIN/MeatPack.NET.dll"; do
    INSTRUMENT_OUT=$("$TOOLS/sharpfuzz" "$DLL" 2>&1) || {
        if ! grep -q "already instrumented" <<< "$INSTRUMENT_OUT"; then
            echo "$INSTRUMENT_OUT" >&2
            exit 1
        fi
    }
done

# 4. Build the native libfuzzer-dotnet driver if needed.
if [ ! -x "$TOOLS/libfuzzer-dotnet" ]; then
    mkdir -p "$TOOLS"
    curl -sfL -o "$TOOLS/libfuzzer-dotnet.cc" \
        https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/master/libfuzzer-dotnet.cc
    "${CXX:-clang++}" -fsanitize=fuzzer -O2 -std=c++17 -o "$TOOLS/libfuzzer-dotnet" "$TOOLS/libfuzzer-dotnet.cc"
fi

# 5. Fuzz.
EXTRA=()
if [ "$DURATION" -gt 0 ]; then
    EXTRA+=("-max_total_time=$DURATION")
fi
exec "$TOOLS/libfuzzer-dotnet" --target_path="$BIN/libbgcode.NET.Fuzz" --target_arg="$HARNESS" \
    -timeout=10 ${EXTRA[@]+"${EXTRA[@]}"} "$CORPUS"
