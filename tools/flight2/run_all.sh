#!/bin/sh
# Regenerates the native-JSBSim golden logs (pip install jsbsim) and checks the C# port (JsbFdm) against them, then flies the headless tests.
# usage: tools/flight2/run_all.sh   (needs python3 + jsbsim, dotnet 8)
set -e
cd "$(dirname "$0")"; R=../../Assets/Resources/Flight/jsb; T=/tmp/jsbtest; J=${JSBSIM_ROOT:-/home/claude/jsb/jsbsim-master}
rm -rf $T; mkdir -p $T/engine $T/systems; for a in f15e f35b heli delorean; do mkdir -p $T/aircraft/$a; cp $R/$a.xml $T/aircraft/$a/; done
for e in direct F100-PW-229 f135-pw-600 liftfan; do cp $R/$e.xml $T/engine/; done
dotnet restore -p:RestoreSources=/tmp/emptysrc >/dev/null 2>&1 || true
dotnet build -c Release --no-restore 2>&1 | grep -E " error |Build succ" | sort -u
run() { ac=$1; tol=$2; shift 2; fs=""
  for s in "$@"; do python3 gen_golden.py $T $ac $s golden/${ac}_$s.csv 1200 >/dev/null; fs="$fs golden/${ac}_$s.csv"; done
  RW_TOL=$tol RW_SKIP=0.5 dotnet bin/Release/net8.0/RwFlight2.dll $T $ac $fs | grep -E "FAIL|PARITY"; }
run f15e 1e-4 cruise ab slow fast
run f35b 2e-2 vcruise vtol
run heli 2e-2 hover
cd fly; dotnet build -c Release --no-restore 2>&1 | grep -E " error " || true
for s in f15e f35b f35b-vtol delorean heli; do dotnet bin/Release/net8.0/Fly.dll ../$R $s | tail -3; done
