#!/usr/bin/env python3
"""Runs every scenario on native RocketSim (native_ref) and on the C# port (RwParity) and fails if they diverge.
usage: compare.py <native_ref> "<dotnet command for RwParity>"   e.g.  compare.py ./native_ref "dotnet out/RwParity.dll"
"""
import math, os, subprocess, sys
here = os.path.dirname(os.path.abspath(__file__))
native, cs = sys.argv[1], sys.argv[2].split()
# thresholds: position (uu), velocity (uu/s), angular velocity (rad/s). 'r1' crosses a ramp crest and lands: looser.
LIMITS = {'default': (1.0, 1.0, 0.05), 'r1_ramp_drive': (12.0, 8.0, 1.0)}
def run(cmd):
    out = subprocess.run(cmd, capture_output=True, text=True, cwd=here).stdout.strip().split('\n')
    return [[float(v) for v in l.split(',')] for l in out if l]
bad = 0
for f in sorted(os.listdir(os.path.join(here, 'scen'))):
    n = f[:-4]
    extra = ['-', 'ramp_tris.txt'] if n.startswith('r') else []
    a = run([native, f'scen/{n}.csv'] + extra); b = run(cs + [f'scen/{n}.csv'] + extra)
    N = min(len(a), len(b))
    pos = max(math.dist(a[i][1:4], b[i][1:4]) for i in range(N))
    vel = max(math.dist(a[i][4:7], b[i][4:7]) for i in range(N))
    ang = max(math.dist(a[i][7:10], b[i][7:10]) for i in range(N))
    lp, lv, la = LIMITS.get(n, LIMITS['default'])
    ok = N > 100 and pos <= lp and vel <= lv and ang <= la
    bad += not ok
    print(f"{'PASS' if ok else 'FAIL'} {n:20s} ticks {N:4d} pos {pos:8.3f}/{lp} uu  vel {vel:7.3f}/{lv} uu/s  angvel {ang:7.4f}/{la} rad/s")
sys.exit(1 if bad else 0)
