#!/usr/bin/env python3
"""Run native JSBSim (pip install jsbsim) and log, per frame: state BEFORE the frame, commands, outputs AFTER the frame.
usage: gen_golden.py <jsbsim_root> <aircraft> <scenario> <out.csv> [frames]"""
import sys, csv, math, jsbsim
root, ac, scen, out = sys.argv[1:5]; N = int(sys.argv[5]) if len(sys.argv) > 5 else 1200
fdm = jsbsim.FGFDMExec(root, None); fdm.set_debug_level(0); fdm.load_model(ac); fdm.set_dt(1/120)
S = {  # scenario: (alt ft, kt, throttle, gear, flaps)
 'cruise': (20000, 350, 0.8, 0, 0), 'slow': (3000, 160, 0.6, 1, 1), 'ab': (10000, 450, 1.0, 0, 0), 'fast': (30000, 900, 1.2, 0, 0), 'vtol': (1500, 90, 0.0, 0, 0), 'vcruise': (10000, 350, 0.0, 0, 0), 'hover': (500, 0, 0.0, 0, 0)}
alt, kt, thr, gear, flap = S[scen]
for k, v in (('ic/h-sl-ft', alt), ('ic/vt-kts', kt), ('ic/psi-true-deg', 90), ('ic/gamma-deg', 0)): fdm[k] = v
fdm.run_ic()
nE = fdm.get_propulsion().get_num_engines()
fdm['propulsion/set-running'] = -1
fdm['gear/gear-cmd-norm'] = gear; fdm['fcs/flap-cmd-norm'] = flap
def xin(t):
    if ac == 'heli': return {'fcs/collective-cmd-norm': 0.5 + 0.06*math.sin(t*0.9), 'fcs/vert-cmd-norm': 0.5*math.sin(t*0.3)}
    if ac != 'f35b': return {}
    if scen == 'vtol': return {'fcs/stovl-cmd-norm': 1.0 if t > 0.5 else 0.0, 'fcs/lever-norm': 0.3 + 0.3*math.sin(t*0.3), 'fcs/vert-cmd-norm': 0.6*math.sin(t*0.5)}
    return {'fcs/stovl-cmd-norm': 0.0, 'fcs/lever-norm': 0.55 + 0.4*math.sin(t*0.4)}
def cmd(t):  # scripted stick: pitch pulse, roll, rudder doublet, speedbrake, throttle ramp
    return dict(el=0.15*math.sin(t*1.3) , ai=0.4*math.sin(t*0.8)*(1 if t>1 else 0), ru=0.3*math.sin(t*0.5), thr=thr, sb=0.5 if 4 < t < 6 else 0.0)
pre_extra = ['aero/alpha-rad','aero/qbar-psf','velocities/mach','velocities/vc-kts','aero/alphadot-rad_sec','aero/beta-rad','aero/h_b-mac-ft','aero/bi2vel']
cols_state = ['velocities/u-fps','velocities/v-fps','velocities/w-fps','velocities/p-rad_sec','velocities/q-rad_sec','velocities/r-rad_sec','position/h-sl-ft','position/h-agl-ft','attitude/phi-rad','attitude/theta-rad','attitude/psi-rad','accelerations/udot-ft_sec2','accelerations/vdot-ft_sec2','accelerations/wdot-ft_sec2']
cols_out = ['forces/fbx-aero-lbs','forces/fby-aero-lbs','forces/fbz-aero-lbs','moments/l-aero-lbsft','moments/m-aero-lbsft','moments/n-aero-lbsft','forces/fbx-prop-lbs','forces/fby-prop-lbs','forces/fbz-prop-lbs','moments/l-prop-lbsft','moments/m-prop-lbsft','moments/n-prop-lbsft','inertia/weight-lbs','inertia/cg-x-in','fcs/elevator-pos-rad','fcs/left-aileron-pos-rad','fcs/rudder-pos-rad','fcs/flap-pos-deg','fcs/speedbrake-pos-norm','gear/gear-pos-norm']
import re as _re
cat=[l.split(' ')[0] for l in fdm.query_property_catalog('aero/').split('\n') if l.strip()]
coef=[c for c in cat if c.startswith('aero/coefficient/') or c.startswith('aero/function/')]
extra = []
for i in range(nE):
    extra += [f'propulsion/engine[{i}]/thrust-lbs', f'propulsion/engine[{i}]/n1', f'propulsion/engine[{i}]/n2']
CAT = set(fdm.query_property_catalog('').split('\n')) if False else None
extra = [e for e in extra if fdm.get_property_catalog is not None]
def _ok(k):
    try: fdm[k]; return True
    except Exception: return False
cols_out = [k for k in cols_out if _ok(k)] + [k for k in ('forces/fbx-external-lbs','forces/fby-external-lbs','forces/fbz-external-lbs','moments/l-external-lbsft','moments/m-external-lbsft','moments/n-external-lbsft') if _ok(k)]
with open(out, 'w', newline='') as f:
    w = csv.writer(f); XK = sorted(xin(0).keys())
    w.writerow(['t','el','ai','ru','thr','sb','flap','gear','nE'] + ['in:'+k for k in XK] + cols_state + pre_extra + cols_out + extra)
    for n in range(N):
        t = n/120; c = cmd(t)
        fdm['fcs/elevator-cmd-norm'] = c['el']; fdm['fcs/aileron-cmd-norm'] = c['ai']; fdm['fcs/rudder-cmd-norm'] = c['ru']; fdm['fcs/speedbrake-cmd-norm'] = c['sb']
        for i in range(nE): fdm[f'fcs/throttle-cmd-norm[{i}]'] = c['thr']
        xv = xin(t)
        for k in XK: fdm[k] = xv[k]
        st = [fdm[k] for k in cols_state + pre_extra]
        fdm.run()
        w.writerow([t, c['el'], c['ai'], c['ru'], c['thr'], c['sb'], flap, gear, nE] + [xv[k] for k in XK] + st + [fdm[k] for k in cols_out] + [fdm[k] for k in extra])
print('ok', ac, scen, out)
