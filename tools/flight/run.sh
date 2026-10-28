#!/usr/bin/env bash
# Flight model parity: C# port of JSBSim vs golden logs from native JSBSim 1.2.x (regenerate with gen_golden.py when a JSBSim build is available).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; cd "$HERE"
if command -v dotnet >/dev/null; then dotnet run --project RwFlight.csproj -c Release
else mcs -out:/tmp/rw-flight.exe Program.cs Fly.cs ../../Assets/Scripts/Sim/Flight/*.cs -r:System.Xml.dll -r:System.Core.dll && mono /tmp/rw-flight.exe; fi
