#!/usr/bin/env bash
# Builds native RocketSim reference + the C# port harness and compares them.   usage: tools/parity/build.sh <RocketSim checkout> [jobs]
set -euo pipefail
RS="$(cd "${1:?usage: build.sh <RocketSim checkout> [jobs]}" && pwd)"; JOBS="${2:-$(nproc)}"
HERE="$(cd "$(dirname "$0")" && pwd)"; OUT="${OUT:-/tmp/rw-parity}"; mkdir -p "$OUT/obj"
FLAGS="-std=c++20 -O1 -w -DRS_DONT_LOG -I$RS/src -I$RS/libsrc/bullet3-3.24"
compile() { local o="$OUT/obj/$(echo "$1" | md5sum | cut -c1-12).o"; [ -f "$o" ] || g++ $FLAGS -c "$1" -o "$o"; }
export -f compile; export FLAGS OUT
find "$RS/src" "$RS/libsrc/bullet3-3.24" -name '*.cpp' | xargs -P "$JOBS" -I{} bash -c 'compile {}'
g++ $FLAGS "$HERE/native_ref.cpp" "$OUT"/obj/*.o -o "$OUT/native_ref" -lpthread
dotnet build "$HERE/RwParity.csproj" -c Release -o "$OUT/cs"
python3 "$HERE/compare.py" "$OUT/native_ref" "dotnet $OUT/cs/RwParity.dll"
