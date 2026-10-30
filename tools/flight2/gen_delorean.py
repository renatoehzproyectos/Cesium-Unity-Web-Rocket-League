#!/usr/bin/env python3
"""Generates Assets/Resources/Flight/jsb/delorean.xml: DeLorean DMC-12 that drives on four wheels (JSBSim BOGEY contacts, driven by an external force with a
power-limited traction curve) and flies in hover mode (wheels fold, four lift thrusters at the wheel stations with attitude PD, vertical-speed PI, yaw-rate hold)."""
import sys
def v(x): return f"<value>{x}</value>"
def p(n): return f"<property>{n}</property>"
def a(x): return v(x) if isinstance(x,(int,float)) else (x if x.startswith("<") else p(x))
def op(n,*xs): return f"<{n}>"+"".join(a(x) for x in xs)+f"</{n}>"
def fn(name,expr,out=None,clip=None):
    o=f"<output>{out}</output>" if out else ""
    c=f"<clipto><min>{clip[0]}</min><max>{clip[1]}</max></clipto>" if clip else ""
    return f'      <fcs_function name="{name}">\n        <function>{expr}</function>\n        {o}{c}\n      </fcs_function>\n'
W=2710.0; M=W/32.174
fc=""
fc+='''      <kinematic name="Hover Transition">
        <input>fcs/hover-cmd-norm</input>
        <traverse><setting><position> 0 </position><time> 0 </time></setting><setting><position> 1 </position><time> 1.4 </time></setting></traverse>
        <output>fcs/hover-pos-norm</output>
      </kinematic>
      <summer name="Gear Pos">
        <input>-fcs/hover-pos-norm</input><bias>1</bias>
        <output>gear/gear-pos-norm</output>
      </summer>
      <lag_filter name="Lift Spool"><input>fcs/hover-cmd-norm</input><c1>3</c1></lag_filter>
'''
# --- hover: vertical speed PI around the weight, attitude PD at the four corners
fc+=fn("fcs/hdot-cmd",op("product",18,"fcs/vert-cmd-norm"))
fc+='''      <summer name="Hdot Error"><input>fcs/hdot-cmd</input><input>-velocities/h-dot-fps</input></summer>
      <pid name="Hdot PI"><input>fcs/hdot-error</input><kp>0.05</kp><ki type="trap">0.08</ki><clipto><min>-0.6</min><max>0.8</max></clipto></pid>
'''
fc+=fn("fcs/lift-total",op("product","fcs/lift-spool","inertia/weight-lbs",op("sum",1,"fcs/hdot-pi")))
fc+=fn("fcs/pitch-diff",op("sum",op("product",3.0,op("difference",op("product",-0.12,"fcs/elevator-cmd-norm"),"attitude/theta-rad")),op("product",-1.6,"velocities/q-rad_sec")),clip=(-0.6,0.6))
fc+=fn("fcs/roll-diff",op("sum",op("product",1.5,op("difference",op("product",0.18,"fcs/steer-cmd-norm"),"attitude/phi-rad")),op("product",-0.5,"velocities/p-rad_sec")),clip=(-0.6,0.6))
for nme,sp,sr in (("fl",1,-1),("fr",1,1),("rl",-1,-1),("rr",-1,1)):
    # front corners (+pitch-diff), left corners get +roll-diff (left up = roll right)
    fc+=fn(f"fcs/thrust-{nme}",op("product",0.25,"fcs/lift-total",op("sum",1,op("product",sp,"fcs/pitch-diff"),op("product",-sr,"fcs/roll-diff"))),clip=(0,6000))
# --- drive (wheels) and forward thrust (hover): power-limited
fc+=fn("fcs/drive-force",op("product","fcs/throttle-cmd-norm",op("min",1700,op("quotient",70000,op("max",14,op("abs","velocities/u-fps")))),"gear/gear-pos-norm"))
fc+=fn("fcs/hover-thrust",op("product","fcs/throttle-cmd-norm",op("min",3600,op("quotient",190000,op("max",14,op("abs","velocities/u-fps")))),"fcs/hover-pos-norm"))
fc+=fn("fcs/hover-side",op("product",-1.2,"inertia/mass-slugs","velocities/v-fps","fcs/hover-pos-norm"))
fc+=fn("fcs/hover-yaw",op("product","fcs/hover-pos-norm",op("max",-3500,op("min",3500,op("product",3500,op("difference",op("product",0.9,"fcs/steer-cmd-norm"),"velocities/r-rad_sec"))))))

def force(name,loc,d,expr): return f'''    <force name="{name}" frame="BODY" unit="LBS">
      <function>{expr}</function>
      <location unit="IN"><x> {loc[0]} </x><y> {loc[1]} </y><z> {loc[2]} </z></location>
      <direction><x> {d[0]} </x><y> {d[1]} </y><z> {d[2]} </z></direction>
    </force>
'''
def moment(name,d,expr): return f'''    <moment name="{name}" frame="BODY" unit="LBSFT">
      <function>{expr}</function>
      <direction><x> {d[0]} </x><y> {d[1]} </y><z> {d[2]} </z></direction>
    </moment>
'''
# structural frame: x aft from the nose (in), y right, z up from the CG. CG x = 100 in (wheelbase 94.8 in: front axle x=52, rear axle x=147)
ext=""
for nme,x,y in (("fl",52,-33),("fr",52,33),("rl",147,-33),("rr",147,33)):
    ext+=force("lift_"+nme,(x,y,-8),(0,0,-1),p("fcs/thrust-"+nme))
ext+=force("drive",(147,0,-14),(1,0,0),p("fcs/drive-force"))
ext+=force("hover_thrust",(100,0,0),(1,0,0),p("fcs/hover-thrust"))
ext+=force("hover_side",(100,0,0),(0,1,0),p("fcs/hover-side"))
ext+=moment("hover_yaw",(0,0,1),p("fcs/hover-yaw"))
def gear(n,x,y,steer,brake):
    return f'''    <contact type="BOGEY" name="{n}">
      <location unit="IN"><x> {x} </x><y> {y} </y><z> -20 </z></location>
      <static_friction> 1.0 </static_friction><dynamic_friction> 0.8 </dynamic_friction><rolling_friction> 0.015 </rolling_friction>
      <spring_coeff unit="LBS/FT"> 9500 </spring_coeff><damping_coeff unit="LBS/FT/SEC"> 950 </damping_coeff><damping_coeff_rebound unit="LBS/FT/SEC"> 1500 </damping_coeff_rebound>
      <max_steer unit="DEG"> {steer} </max_steer><brake_group> {brake} </brake_group><retractable>1</retractable>
    </contact>
'''
cg=gear("FRONT_LEFT",52,-33,30,"LEFT")+gear("FRONT_RIGHT",52,33,30,"RIGHT")+gear("REAR_LEFT",147,-33,0,"LEFT")+gear("REAR_RIGHT",147,33,0,"RIGHT")
xml=f'''<?xml version="1.0"?>
<!--
  DeLorean DMC-12 time machine for RocketWorld, authored for JSBSim 1.x (generated by tools/flight2/gen_delorean.py - edit that and re-run it).
  2,710 lb, wheelbase 94.8 in, track 66 in. On the road: four BOGEY contacts (spring / damper, tyre side force, rolling / brake friction), drive force
  from a power-limited traction curve (1,700 lbf traction limit, 70,000 ft-lbf/s = 127 hp), aerodynamic drag (Cd 0.35, A 1.9 m2). Hover mode: wheels fold (gear retracts in 1.4 s), four
  lift thrusters at the wheel stations (collective = weight * (1 + vertical-speed PI), differential for pitch / roll attitude hold), forward thrust, side-slip
  damping and a yaw-rate command. Not a real vehicle: figures are estimates.
-->
<fdm_config name="DeLorean" version="2.0" release="BETA">
  <fileheader><author>RocketWorld</author><filecreationdate>2026-10-29</filecreationdate><version>1.0</version><description>DeLorean DMC-12 hover car</description></fileheader>
  <metrics>
    <wingarea unit="FT2"> 20 </wingarea><wingspan unit="FT"> 6 </wingspan><chord unit="FT"> 3.3 </chord>
    <location name="AERORP" unit="IN"><x> 100 </x><y> 0 </y><z> 0 </z></location>
    <location name="EYEPOINT" unit="IN"><x> 75 </x><y> -15 </y><z> 25 </z></location>
    <location name="VRP" unit="IN"><x> 0 </x><y> 0 </y><z> 0 </z></location>
  </metrics>
  <mass_balance>
    <ixx unit="SLUG*FT2"> 330 </ixx><iyy unit="SLUG*FT2"> 1250 </iyy><izz unit="SLUG*FT2"> 1400 </izz><ixz unit="SLUG*FT2"> 0 </ixz>
    <emptywt unit="LBS"> {W:.0f} </emptywt>
    <location name="CG" unit="IN"><x> 100 </x><y> 0 </y><z> 0 </z></location>
  </mass_balance>
  <ground_reactions>
{cg}  </ground_reactions>
  <external_reactions>
{ext}  </external_reactions>
  <propulsion></propulsion>
  <flight_control name="DeLorean">
    <property value="0"> fcs/hover-cmd-norm </property>
    <property value="0"> fcs/vert-cmd-norm </property>
    <property value="0"> fcs/throttle-cmd-norm </property>
    <channel name="Drive">
{fc}    </channel>
  </flight_control>
  <aerodynamics>
    <axis name="DRAG"><function name="aero/coefficient/drag"><description>Cd 0.35 x 1.9 m2 = 7.2 ft2 drag area; roughly doubles in hover (open wheel wells / fan wash)</description><product><property>aero/qbar-psf</property><sum><value>7.2</value><product><value>4</value><property>fcs/hover-pos-norm</property></product></sum></product></function></axis>
    <axis name="SIDE"><function name="aero/coefficient/side"><product><property>aero/qbar-psf</property><property>aero/beta-rad</property><value>-24</value></product></function></axis>
    <axis name="LIFT"><function name="aero/coefficient/lift"><product><property>aero/qbar-psf</property><property>aero/alpha-rad</property><value>8</value></product></function></axis>
    <axis name="PITCH"><function name="aero/coefficient/pitch"><product><property>aero/qbar-psf</property><property>aero/alpha-rad</property><value>-25</value></product></function></axis>
    <axis name="YAW"><function name="aero/coefficient/yaw"><product><property>aero/qbar-psf</property><property>aero/beta-rad</property><value>40</value></product></function></axis>
  </aerodynamics>
</fdm_config>
'''
open(sys.argv[1],"w").write(xml); print("wrote",len(xml))
