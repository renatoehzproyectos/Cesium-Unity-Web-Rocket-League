#!/usr/bin/env bash
# Water system checks without Unity:  1) vector tiles / geodesy (C#)   2) 6-DOF boat model tuning (python + numpy)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
if command -v dotnet >/dev/null; then dotnet run --project "$HERE/RwWater.csproj" -c Release
elif command -v mcs >/dev/null; then mcs -out:/tmp/rw-water.exe "$HERE/Program.cs" "$HERE/../../Assets/Tests/EditMode/WaterTestTiles.cs" "$HERE"/../../Assets/Scripts/Sim/Water/*.cs "$HERE/../../Assets/Scripts/Sim/TileCachePolicy.cs" && mono /tmp/rw-water.exe
else echo "need dotnet or mono (mcs)"; exit 2; fi
python3 "$HERE/boat_sim.py"
