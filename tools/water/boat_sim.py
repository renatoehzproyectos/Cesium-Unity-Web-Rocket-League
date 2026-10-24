#!/usr/bin/env python3
"""Offline 6-DOF check of the boat model in Assets/Scripts/Runtime/WaterDriver.cs (same constants, same force formulas, 120 Hz,
semi-implicit Euler like a rigid-body engine, Octane mass / inertia, gravity 13 m/s^2). Unity axes: x right, y up, z forward.
It validates the TUNING (equilibrium, landing, top speed, turning, capsize recovery, stability); PhysX contact feel is not simulated."""
import math, sys
import numpy as np

DT = 1/120; G = 13.0; M = 180.0
# Octane effective hitbox (m) -> inertia, same maths as CarConfig.LocalInertia; Unity local axes (pitch x, yaw y, roll z)
ex, ey, ez = 120.507*.02-.00268, 86.6994*.02-.00268, 38.6591*.02-.00268
k = M/12; I = np.array([k*(ex*ex+ez*ez), k*(ex*ex+ey*ey), k*(ey*ey+ez*ez)])   # x: pitch(Iy sim), y: yaw(Iz sim), z: roll(Ix sim)
HIT_BOTTOM = 0.4151 - ez/2                                                         # hitbox bottom above the body origin

P = dict(hullBottom=-0.60, draft=0.30, hw=0.55, hl=0.95, heave=2400., heaveQ=120., maxThrust=1620., boostMul=1.6, revMul=0.25,
         dF=1.53, dFl=40., dL=60., dLl=300., brake=500., yaw=750., yawD=470., rollD=120., pitchD=250., bank=600., right=2500.,
         swell=0.05)

def qmul(a, b):
    w1,x1,y1,z1 = a; w2,x2,y2,z2 = b
    return np.array([w1*w2-x1*x2-y1*y2-z1*z2, w1*x2+x1*w2+y1*z2-z1*y2, w1*y2-x1*z2+y1*w2+z1*x2, w1*z2+x1*y2-y1*x2+z1*w2])
def qrot(q, v):
    v = np.asarray(v, float); w, u = q[0], q[1:]; return v + 2*np.cross(u, np.cross(u, v) + w*v)
def qinv(q): return np.array([q[0], -q[1], -q[2], -q[3]])
def qaxis(axis, ang): axis = axis/np.linalg.norm(axis); return np.array([math.cos(ang/2), *(axis*math.sin(ang/2))])

class Boat:
    def __init__(s, pos, q=None, vel=(0,0,0), level=0.0, swell=0.0):
        s.p = np.array(pos, float); s.q = np.array([1.,0,0,0]) if q is None else q; s.v = np.array(vel, float); s.w = np.zeros(3)
        s.level = level; s.t = 0.0; s.swell = swell; s.imm = 0; s.stern = 0
    def hull(s, i):
        x = -P['hw'] if (i & 1) == 0 else P['hw']; z = -P['hl'] if (i >> 1) == 0 else (0. if (i >> 1) == 1 else P['hl'])
        return np.array([x, P['hullBottom'], z])
    def sw(s, x, z):
        a = s.swell; t = s.t
        return a*(.6*math.sin(.55*x+.35*z+t*1.7)+.4*math.sin(-.31*x+.62*z+t*1.1+1.3))
    def step(s, throttle=0., steer=0., boost=False, handbrake=False):
        q, p, v, w = s.q, s.p, s.v, s.w; s.t += DT
        F = np.zeros(3); T = np.zeros(3)
        def add(f, at=None):
            nonlocal F, T
            F = F + f
            if at is not None: T = T + np.cross(at - p, f)
        up = qrot(q, [0,1,0]); fwd = qrot(q, [0,0,1]); per = M*G/6
        imm = stern = 0.
        for i in range(6):
            rel = qrot(q, s.hull(i)); wp = p + rel
            d = min(s.level + s.sw(wp[0], wp[2]) - wp[1], 1.2)
            if d <= 0: continue
            ramp = min(1., d/.15); x = d/P['draft']; imm += ramp; stern += ramp if i < 2 else 0
            vy = (v + np.cross(w, rel))[1]
            f = per*.5*x*(1+x*x) - ramp*(P['heave']*vy + P['heaveQ']*vy*abs(vy))/6
            if f > 0: add(np.array([0, f, 0]), wp)
        s.imm = imm/6; s.stern = stern/2; sc = min(1., s.imm*1.5)
        cmd = 1. if boost and s.stern > .2 else throttle
        th = (P['maxThrust']*P['boostMul'] if (boost and s.stern > .2) else (cmd*P['maxThrust'] if cmd >= 0 else cmd*P['maxThrust']*P['revMul']))*s.stern
        if abs(th) > .01: add(fwd*th, p + qrot(q, [0, P['hullBottom']*.4, -P['hl']]))
        inv = qinv(q); lv = qrot(inv, v); lw = qrot(inv, w)
        fx = -(P['dL']*abs(lv[0]) + P['dLl'])*lv[0]; fz = -(P['dF']*abs(lv[2]) + P['dFl'])*lv[2]
        if handbrake: fz -= lv[2]*P['brake']; fx -= lv[0]*P['brake']*.5
        add(qrot(q, [fx, 0, fz])*sc)
        vf = lv[2]; auth = max(min(1., abs(vf)/7), abs(cmd)*.45); dr = -1. if vf < -.5 else 1.
        yaw = steer*P['yaw']*auth*(1/(1+vf*vf/(16.*16.)))*dr*min(1., s.stern+.2); bank = -steer*P['bank']*min(1., abs(vf)/12)*dr*sc
        damp = np.array([-P['pitchD']*lw[0], -P['yawD']*lw[1], -P['rollD']*lw[2]])*(.25+.75*sc)
        T = T + qrot(q, np.array([0, yaw, bank]) + damp)
        tilt = 1 - up[1]
        if tilt > .15:
            ax = np.cross(up, [0,1,0]); ax = fwd if ax@ax < .01 else ax/np.linalg.norm(ax)
            T = T + ax*(P['right']*(tilt-.15)*(.3+.7*sc))
        # rigid body step (gravity, then semi-implicit Euler; world torque -> local inertia)
        v = v + (F/M + np.array([0, -G, 0]))*DT
        lt = qrot(inv, T); w = w + qrot(q, lt/I)*DT
        p = p + v*DT
        a = np.linalg.norm(w)*DT
        if a > 1e-9: q = qmul(qaxis(w, a), q); q /= np.linalg.norm(q)
        s.p, s.q, s.v, s.w = p, q, v, w
        sp = np.linalg.norm(s.v)
        if sp > 46: s.v *= 46/sp
        return s
    @property
    def clearance(s):   # hitbox bottom above the water surface: must stay > 0 or the boat touches the water mesh
        # lowest hitbox corner (box of ez high, ey wide, ex long around the offset centre), tilt included
        lo = 1e9
        for sx in (-1, 1):
            for sz in (-1, 1):
                for sy in (-1, 1):
                    c = s.p + qrot(s.q, [sx*ey/2, .4151 + sy*ez/2, .2776 + sz*ex/2]); lo = min(lo, c[1])
        return lo - s.level
    def euler(s):
        up = qrot(s.q, [0,1,0]); fwd = qrot(s.q, [0,0,1]); right = qrot(s.q, [1,0,0])
        return math.degrees(math.asin(max(-1, min(1, fwd[1])))), math.degrees(math.asin(max(-1, min(1, right[1]))))   # pitch up+, roll right-side-up+

def run(b, secs, **kw):
    for _ in range(int(secs/DT)): b.step(**kw)
    return b

fails = 0
def check(ok, msg):
    global fails; print(("PASS  " if ok else "FAIL  ") + msg); fails += (not ok)

print(f"inertia pitch/yaw/roll = {I[0]:.1f}/{I[1]:.1f}/{I[2]:.1f} kg m2, hitbox bottom = origin + {HIT_BOTTOM:.3f} m")
# 1 equilibrium
b = run(Boat([0,.9,0]), 12); check(abs(b.p[1] - .30) < .02, f"rest height: origin {b.p[1]:.3f} m above the surface (expected 0.30)"); rest = b.p[1]
check(b.clearance > .25, f"hitbox clears the water mesh at rest by {b.clearance:.2f} m")
b = run(Boat([0,.9,0], swell=.11), 20); mn = 9; mx = -9
for _ in range(1200): b.step(); mn = min(mn, b.clearance); mx = max(mx, b.p[1])
check(mn > .15, f"with ocean swell (0.11 m) min clearance {mn:.2f} m")
# 2 drops
for h in (1.0, 3.0, 6.0, 10.0):
    b = Boat([0, rest + h, 0]); lo = 9
    for _ in range(int(8/DT)): b.step(); lo = min(lo, b.clearance)
    check(b.p[1] > .2 and abs(b.p[1] - rest) < .05, f"drop {h:4.1f} m: settles at {b.p[1]:.2f} m, min clearance {lo:+.2f} m" + ("" if lo > 0 else "   <- touches the water mesh (PhysX contact takes over)"))
# 3 speed
b = run(Boat([0, rest, 0]), 20, throttle=1.); v = np.linalg.norm(b.v)
check(21 < v < 23, f"top speed at full throttle {v:.1f} m/s = {v*3.6:.0f} km/h (expected ~22 m/s)")
tr = b.euler()[0]; print(f"INFO  bow-up trim at top speed: {tr:.1f} deg")
b = run(Boat([0, rest, 0]), 25, boost=True); v = np.linalg.norm(b.v); check(28 < v < 31, f"top speed boosted {v:.1f} m/s = {v*3.6:.0f} km/h (expected ~30 m/s)")
b = Boat([0, rest, 0]); t90 = None
for i in range(int(20/DT)):
    b.step(throttle=1.)
    if t90 is None and np.linalg.norm(b.v) > .9*22: t90 = i*DT
print(f"INFO  time to 90% of top speed: {t90:.1f} s")
b = run(Boat([0, rest, 0]), 10, throttle=-1.); check(-9 < qrot(qinv(b.q), b.v)[2] < -5, f"reverse speed {qrot(qinv(b.q), b.v)[2]:.1f} m/s")
b = run(Boat([0, rest, 0], vel=(0,0,22)), 1.0); b2 = run(b, 0.001); d0 = b.p[2]
b = Boat([0, rest, 0], vel=(0,0,22)); run(b, 12, handbrake=True); check(np.linalg.norm(b.v) < .5 and b.p[2] < 120, f"handbrake from 22 m/s stops in {b.p[2]:.0f} m")
b = Boat([0, rest, 0], vel=(0,0,22)); run(b, 30); check(np.linalg.norm(b.v) < .3, f"coasting from 22 m/s: {np.linalg.norm(b.v):.2f} m/s after 30 s")
# 4 turning at speed
b = run(Boat([0, rest, 0]), 15, throttle=1.); run(b, 8, throttle=1., steer=1.)
lv = qrot(qinv(b.q), b.v); yawrate = abs(b.w[1]); slip = math.degrees(math.atan2(lv[0], lv[2])); R = np.linalg.norm(b.v)/max(yawrate, 1e-6)
print(f"INFO  full-lock turn at {np.linalg.norm(b.v):.1f} m/s: yaw rate {yawrate:.2f} rad/s, radius {R:.0f} m, slip {slip:.0f} deg, roll {b.euler()[1]:.1f} deg")
check(.4 < yawrate < 1.6 and 10 < R < 60 and abs(slip) < 25, f"turn is a boat-like arc (radius {R:.0f} m, within 10..60; slip {slip:.0f} deg)")
check(b.imm > .3 and b.clearance > 0, f"stays on the water while turning (immersion {b.imm:.2f}, clearance {b.clearance:.2f})")
dirn = -1 if b.p[0] < 0 else 1; check(b.p[0] > 0, "steer right turns right (+x)")
b = run(Boat([0, rest, 0]), 4, steer=1.); print(f"INFO  stationary, no throttle: yaw rate {abs(b.w[1]):.3f} rad/s"); check(abs(b.w[1]) < .05, "no rudder authority at standstill without throttle")
b = run(Boat([0, rest, 0]), 4, throttle=.3, steer=1.); check(abs(b.w[1]) > .1, f"prop wash lets it turn at low speed ({abs(b.w[1]):.2f} rad/s)")
# 5 capsize recovery
for roll in (60, 110, 170):
    b = Boat([0, rest + .2, 0], q=qaxis(np.array([0,0,1.]), math.radians(roll))); run(b, 15)
    up = qrot(b.q, [0,1,0])[1]; check(up > .95, f"rolled {roll} deg: upright after 15 s (up.y = {up:.2f})")
# 6 stability under random control
rng = np.random.default_rng(7); b = Boat([0, rest, 0], swell=.11); ok = True; worst = 0
for i in range(int(90/DT)):
    if i % 120 == 0: th, st, bo, hb = rng.uniform(-1, 1), rng.uniform(-1, 1), rng.random() < .3, rng.random() < .1
    b.step(throttle=th, steer=st, boost=bo, handbrake=hb); worst = max(worst, np.linalg.norm(b.w))
    if not np.all(np.isfinite(b.p)) or abs(b.p[1]) > 3: ok = False; break
check(ok and worst < 5.5, f"90 s of random controls: finite, level, peak angular speed {worst:.2f} rad/s")
print("ALL OK" if not fails else f"{fails} FAILED"); sys.exit(1 if fails else 0)
