#!/usr/bin/env bash
# Verify the C# FPV port against the original TypeScript model.
#   SHRAMKO_DIR=/path/to/ShramkoGSFPV-main tools/fpv/run.sh
set -euo pipefail
cd "$(dirname "$0")"
if [ -n "${SHRAMKO_DIR:-}" ]; then
  npx tsx gen_golden.mts            # regenerate golden/*.json from the original model
  python3 gen_presets.py "$SHRAMKO_DIR"   # regenerate FpvPresets.cs (a diff here means the presets changed upstream)
fi
dotnet run -c Release                 # replays golden/*.json on Assets/Scripts/Sim/Fpv, exit 1 on any drift
