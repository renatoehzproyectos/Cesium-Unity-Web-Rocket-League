#!/usr/bin/env bash
# Engine-free checks of the priority multipliers (Assets/Scripts/Sim/PriorityLogic.cs).
set -e
cd "$(dirname "$0")"
mcs -out:/tmp/rw_priority.exe Test.cs ../../Assets/Scripts/Sim/PriorityLogic.cs && mono /tmp/rw_priority.exe
