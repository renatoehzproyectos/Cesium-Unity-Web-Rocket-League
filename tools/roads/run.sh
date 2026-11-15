#!/usr/bin/env bash
# Road system checks without Unity: engine-free core (format, centreline, height fit, mesh, carving, cache policy) + Python <-> C# tile format agreement.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; S="$HERE/../../Assets/Scripts/Sim"
command -v mcs >/dev/null || { echo "need mono (mcs)"; exit 2; }
mcs -out:/tmp/rw-roads.exe "$HERE/Program.cs" "$S"/Roads/*.cs "$S/Water/Geo.cs" 2>&1 | grep -v warning || true
mono /tmp/rw-roads.exe
python3 - <<PY
import sys; sys.path.insert(0, "$HERE")
import build_tiles as b
segs=[dict(id=b.fnv('abc'),cls=3,surface=2,flags=1,level=1,width=7.5,lanes=2,conns=[(b.fnv('c1'),0.0),(b.fnv('c2'),1.0)],pts=[(-3.7038,40.4168),(-3.7021,40.4174),(-3.6990,40.4180)])]
open('/tmp/py.rwr','wb').write(b.encode_tile('2026-10-15.0',14,8000,5000,segs))
PY
mono /tmp/rw-roads.exe /tmp/py.rwr
