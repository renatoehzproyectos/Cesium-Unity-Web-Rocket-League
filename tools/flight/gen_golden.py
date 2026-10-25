#!/usr/bin/env python3
"""Regenerate the golden files from NATIVE JSBSim (needs a built JSBSim executable and the JSBSim source tree for aircraft/ engine/ dirs).
usage: JSBSIM=/path/JSBSim ROOT=/path/jsbsim-master python3 gen_golden.py
The aircraft files are taken from Assets/Resources/Flight. Each scenario runs 2.5 s at 120 Hz; the throttle is 0 for the first second (the
propeller windmills up from 0 rpm, which JSBSim cannot be told otherwise) and then set. The C# port is checked frame by frame against these rows."""
import os, shutil, subprocess, sys, csv
HERE=os.path.dirname(os.path.abspath(__file__)); RES=os.path.join(HERE,"../../Assets/Resources/Flight")
ROOT=os.environ.get("ROOT","/tmp/js/jsbsim-master"); EXE=os.environ.get("JSBSIM","/tmp/js/JSBSim")
os.makedirs(f"{ROOT}/aircraft/boxplane",exist_ok=True)
shutil.copy(f"{RES}/boxplane.xml",f"{ROOT}/aircraft/boxplane/boxplane.xml")
for f in ("eng_electric160.xml","prop_75in2f.xml"): shutil.copy(f"{RES}/{f}",f"{ROOT}/engine/{f}")
PROPS=["velocities/u-fps","velocities/v-fps","velocities/w-fps","velocities/p-rad_sec","velocities/q-rad_sec","velocities/r-rad_sec","position/h-sl-ft",
 "fcs/elevator-pos-rad","fcs/aileron-pos-rad","fcs/rudder-pos-rad",
 "aero/alpha-rad","aero/beta-rad","aero/qbar-psf","atmosphere/rho-slugs_ft3","atmosphere/a-fps",
 "forces/fbx-aero-lbs","forces/fby-aero-lbs","forces/fbz-aero-lbs","moments/l-aero-lbsft","moments/m-aero-lbsft","moments/n-aero-lbsft",
 "forces/fbx-prop-lbs","forces/fby-prop-lbs","forces/fbz-prop-lbs","moments/l-prop-lbsft","moments/m-prop-lbsft","moments/n-prop-lbsft","propulsion/engine[0]/propeller-rpm"]
SC=[ # name, alt ft, u, v, w (ft/s), phi, theta deg, p,q,r, elevator, aileron, rudder, throttle (after 1 s)
 ("cruise",     3000, 150, 0, 8,   0, 2,   0,0,0,        -0.05, 0.0, 0.0, 0.7),
 ("climb_roll", 5000, 140, 3,12,  10, 5,   0.05,0.02,-0.03,-0.2, 0.4, 0.1, 1.0),
 ("slow_flare",  800,  95, 0,14,   0, 8,   0,0,0,        -0.5, 0.0, 0.0, 0.2),
 ("sideslip",   9000, 160,18, 6,  -8, 0,   0,0,0,         0.0,-0.3,-0.6, 0.6),
 ("high_alpha", 2000,  70, 0,30,   0,12,   0,0.1,0,      -1.0, 0.0, 0.0, 0.9),
 ("fast_dive",  12000,250, 0,-10,  0,-8,   0,-0.05,0.02, 0.3, 0.0, 0.0, 0.5),
]
for name,alt,u,v,w,phi,th,p,q,r,de,da,dr,thr in SC:
    open(f"{ROOT}/aircraft/boxplane/g_ic.xml","w").write(f'''<?xml version="1.0"?><initialize name="g"><latitude unit="DEG">0</latitude><longitude unit="DEG">0</longitude><altitude unit="FT">{alt}</altitude>
<ubody unit="FT/SEC">{u}</ubody><vbody unit="FT/SEC">{v}</vbody><wbody unit="FT/SEC">{w}</wbody><phi unit="DEG">{phi}</phi><theta unit="DEG">{th}</theta><psi unit="DEG">0</psi></initialize>''')
    open(f"{ROOT}/scripts/g.xml","w").write(f'''<?xml version="1.0"?><runscript name="g"><use aircraft="boxplane" initialize="g_ic"/><run start="0" end="2.5" dt="0.00833333333">
<event name="ctl"><condition>simulation/sim-time-sec ge 0</condition><set name="velocities/p-rad_sec" value="{p}"/><set name="velocities/q-rad_sec" value="{q}"/><set name="velocities/r-rad_sec" value="{r}"/>
<set name="fcs/elevator-cmd-norm" value="{de}"/><set name="fcs/aileron-cmd-norm" value="{da}"/><set name="fcs/rudder-cmd-norm" value="{dr}"/></event>
<event name="thr"><condition>simulation/sim-time-sec ge 1.0</condition><set name="fcs/throttle-cmd-norm[0]" value="{thr}"/></event></run></runscript>''')
    open("/tmp/g_out.xml","w").write('<?xml version="1.0"?><output name="/tmp/g.csv" type="CSV" rate="120">'+"".join(f"<property>{x}</property>" for x in PROPS)+"</output>")
    r=subprocess.run([EXE,f"--root={ROOT}","--script=scripts/g.xml","--logdirectivefile=/tmp/g_out.xml","--nohighlight"],capture_output=True,text=True,cwd=ROOT)
    rows=list(csv.reader(open("/tmp/g.csv")))
    with open(f"{HERE}/golden/{name}.csv","w",newline="") as f:
        w_=csv.writer(f); w_.writerow(["# alt,thr_set=",alt,thr]); w_.writerow(["time"]+[x.split("/")[-1] for x in PROPS]); [w_.writerow(x) for x in rows[1:]]
    print(name,len(rows)-1,"frames")
