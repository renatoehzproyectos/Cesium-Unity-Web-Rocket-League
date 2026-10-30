#!/usr/bin/env python3
"""Generates Assets/Resources/Flight/jsb/heli.xml: a single-main-rotor helicopter in plain JSBSim format (no FGRotor): the rotor is written as
FCS functions + external reactions so that the stock JSBSim (checked with pip jsbsim) and the C# port (JsbFdm) fly the same model.
Rotor: collective -> blade-element thrust with momentum-theory inflow (dynamic inflow lag), forward-flight flap-back, hub-offset stiffness,
rotor-speed dynamics (engine droop governor + rotor inertia -> autorotation), ground effect, profile drag / H-force, tail rotor, attitude-hold SAS."""
import sys
def v(x): return f"<value>{x}</value>"
def p(n): return f"<property>{n}</property>"
def a(x): return v(x) if isinstance(x,(int,float)) else (x if x.startswith("<") else p(x))
def op(n,*xs): return f"<{n}>"+"".join(a(x) for x in xs)+f"</{n}>"
def fn(name,expr,out=None,clip=None):
    o=f"<output>{out}</output>" if out else ""
    c=f"<clipto><min>{clip[0]}</min><max>{clip[1]}</max></clipto>" if clip else ""
    return f'      <fcs_function name="{name}">\n        <function>{expr}</function>\n        {o}{c}\n      </fcs_function>\n'
def lag(name,inp,tau,out=None):
    return f'      <lag_filter name="{name}">\n        <input>{inp}</input>\n        <c1>{1.0/tau}</c1>\n      </lag_filter>\n'
def pg(name,inp,gain,clip=None,out=None):
    c=f"<clipto><min>{clip[0]}</min><max>{clip[1]}</max></clipto>" if clip else ""
    o=f"<output>{out}</output>" if out else ""
    return f'      <pure_gain name="{name}">\n        <input>{inp}</input>\n        <gain>{gain}</gain>\n        {c}{o}\n      </pure_gain>\n'

R=21.0; OM0=33.5; SIGMA=0.068; LA=5.73; K=SIGMA*LA/4; CD0=0.011; IR=1500.0; PMAX=550000.0; TTRMAX=1000.0
HUBZ=96   # in above CG
fc=""
fc+='      <summer name="Collective Total">\n        <input>fcs/collective-cmd-norm</input>\n        <input>fcs/collective-sas</input>\n        <clipto><min>0</min><max>1</max></clipto>\n      </summer>\n'
# --- rotor kinematics
fc+=fn("rotor/omr",op("product","rotor/omega-rad_sec",R))
fc+=fn("rotor/mu-x",op("quotient","velocities/u-aero-fps","rotor/omr"))
fc+=fn("rotor/mu-y",op("quotient","velocities/v-aero-fps","rotor/omr"))
fc+=fn("rotor/mu",op("sqrt",op("sum",op("product","rotor/mu-x","rotor/mu-x"),op("product","rotor/mu-y","rotor/mu-y"))))
fc+=fn("rotor/lamc",op("quotient",op("product",-1,"velocities/w-aero-fps"),"rotor/omr"))
fc+=fn("rotor/theta0",op("product",op("sum",-0.0174533,op("product",0.2443,"fcs/collective-total")),1))   # -1 deg .. +13 deg
fc+=fn("rotor/A",op("difference",op("product",op("product",0.66667,"rotor/theta0"),op("sum",1,op("product",1.5,op("pow","rotor/mu",2)))),"rotor/lamc"))
fc+=fn("rotor/W",op("max",0.02,op("sqrt",op("sum",op("pow","rotor/mu",2),op("pow",op("sum","rotor/lamc","rotor/lami"),2)))))
fc+=fn("rotor/lami-target",op("max",-0.03,op("quotient",op("product",K,"rotor/A"),op("sum",op("product",2,"rotor/W"),K))))
fc+=lag("rotor/lami","rotor/lami-target",0.18)
# ground effect (Cheeseman-Bennett style, on induced inflow): hub height above ground
fc+=fn("rotor/hub-agl",op("max",op("product",0.5,R),op("sum","position/h-agl-ft",HUBZ/12.0)))
fc+=fn("rotor/ge",op("quotient",1,op("difference",1,op("min",0.3,op("pow",op("quotient",R,op("product",4,"rotor/hub-agl")),2)))))
fc+=fn("rotor/lami-eff",op("quotient","rotor/lami","rotor/ge"))
fc+=fn("rotor/ct",op("max",0,op("product",K,op("difference","rotor/A","rotor/lami-eff"))))
fc+=fn("rotor/qh",op("product","atmosphere/rho-slugs_ft3",3.14159*R*R,op("pow","rotor/omr",2)))
fc+=fn("rotor/thrust-lbs",op("product","rotor/ct","rotor/qh"))
# --- pilot / SAS (JSBSim convention: elevator cmd + = stick forward, aileron cmd + = right, rudder cmd + = nose left)
# velocity-command / attitude-hold: stick = ground speed command (100 ft/s fwd, 50 ft/s sideways), pitch / roll angle follow with rate damping
fc+=fn("rotor/theta-cmd",op("max",-0.3,op("min",0.3,op("product",-0.012,op("difference",op("product",100,"fcs/elevator-cmd-norm"),"velocities/u-fps")))))
fc+=fn("rotor/phi-cmd",op("max",-0.4,op("min",0.4,op("difference",op("product",0.012,op("difference",op("product",50,"fcs/aileron-cmd-norm"),"velocities/v-fps")),op("quotient","rotor/tail-thrust","inertia/weight-lbs")))))
fc+=fn("rotor/lon-cyc",op("sum",op("product",2.2,op("difference","attitude/theta-rad","rotor/theta-cmd")),op("product",0.55,"velocities/q-rad_sec")),clip=(-1,1))
fc+=fn("rotor/lat-cyc",op("sum",op("product",-2.2,op("difference","attitude/phi-rad","rotor/phi-cmd")),op("product",-0.55,"velocities/p-rad_sec")),clip=(-1,1))
# --- flapping: tilt of the thrust vector relative to the body (+pitch = forward, +roll = right)
fc+=fn("rotor/ffk",op("quotient",op("difference",op("product",2.6667,"rotor/theta0"),op("product",2,op("sum","rotor/lamc","rotor/lami-eff"))),op("difference",1,op("product",0.5,op("pow","rotor/mu",2)))))
fc+=fn("rotor/tilt-pitch-eq",op("sum",op("product",0.22,"rotor/lon-cyc"),op("product",-1,"rotor/mu-x","rotor/ffk"),op("product",op("quotient",3.2,"rotor/omega-rad_sec"),"velocities/q-rad_sec")))
fc+=fn("rotor/tilt-roll-eq",op("sum",op("product",0.22,"rotor/lat-cyc"),op("product",-1,"rotor/mu-y","rotor/ffk"),op("product",op("quotient",-3.2,"rotor/omega-rad_sec"),"velocities/p-rad_sec")))
fc+=lag("rotor/tilt-pitch","rotor/tilt-pitch-eq",0.10)
fc+=lag("rotor/tilt-roll","rotor/tilt-roll-eq",0.10)
# --- forces at the hub (body axes: x fwd, y right, z down)
fc+=fn("rotor/fx",op("sum",op("product","rotor/thrust-lbs",op("sin","rotor/tilt-pitch")),op("product",-1,"rotor/qh",SIGMA*CD0/4,"rotor/mu-x")))
fc+=fn("rotor/fy",op("sum",op("product","rotor/thrust-lbs",op("sin","rotor/tilt-roll")),op("product",-1,"rotor/qh",SIGMA*CD0/4,"rotor/mu-y")))
fc+=fn("rotor/fz",op("product",-1,"rotor/thrust-lbs",op("cos","rotor/tilt-pitch"),op("cos","rotor/tilt-roll")))
# --- power, torque, rotor speed
fc+=fn("rotor/p-induced",op("product","rotor/thrust-lbs",op("sum","rotor/lamc","rotor/lami-eff"),"rotor/omr"))
fc+=fn("rotor/p-profile",op("product","rotor/qh","rotor/omr",SIGMA*CD0/8,op("sum",1,op("product",4.6,op("pow","rotor/mu",2)))))
fc+=fn("rotor/p-rotor",op("product",1.08,op("sum","rotor/p-induced","rotor/p-profile")))
fc+=fn("rotor/q-main",op("quotient","rotor/p-rotor","rotor/omega-rad_sec"))
fc+=fn("rotor/gov",op("product","propulsion/engine-on",op("max",0,op("min",1,op("sum",0.5,op("product",14,op("quotient",op("difference",OM0,"rotor/omega-rad_sec"),OM0)))))))
fc+=lag("rotor/gov-lag","rotor/gov",0.8)
fc+=fn("rotor/p-eng",op("product",PMAX,"rotor/gov-lag"))
fc+=fn("rotor/omega-rad_sec",op("max",8,op("min",42,op("sum","rotor/omega-rad_sec",op("product","simulation/dt",op("quotient",op("difference","rotor/p-eng","rotor/p-rotor"),op("product",IR,"rotor/omega-rad_sec")))))),out="rotor/omega-rad_sec")
# hub stiffness (hinge offset): moment ~ 0.15 T R per radian of tilt
fc+=fn("rotor/m-pitch",op("product",-0.15*R,"rotor/thrust-lbs","rotor/tilt-pitch"))
fc+=fn("rotor/m-roll",op("product",0.15*R,"rotor/thrust-lbs","rotor/tilt-roll"))
# --- tail rotor: yaw-rate hold; thrust to the right pushes the tail right = nose left
fc+=fn("rotor/yaw-cmd",op("product",-0.8,"fcs/rudder-cmd-norm"))
fc+='      <summer name="Yaw Error">\n        <input>rotor/yaw-cmd</input>\n        <input>-velocities/r-rad_sec</input>\n      </summer>\n'
fc+='      <pid name="Yaw PI">\n        <input>fcs/yaw-error</input>\n        <kp>0.9</kp>\n        <ki type="trap">0.5</ki>\n        <clipto><min>-1</min><max>1</max></clipto>\n      </pid>\n'
fc+=fn("rotor/tail-cmd",op("product",-1,"fcs/yaw-pi"),clip=(-1,1))
fc+=fn("rotor/tail-thrust",op("product",TTRMAX,"rotor/tail-cmd",op("pow",op("quotient","rotor/omega-rad_sec",OM0),2)))
# --- collective SAS: vertical speed hold around the collective lever (vert cmd = +-15 ft/s)
fc+=fn("fcs/hdot-cmd",op("product",15,"fcs/vert-cmd-norm"))
fc+='      <summer name="Hdot Error">\n        <input>fcs/hdot-cmd</input>\n        <input>-velocities/h-dot-fps</input>\n      </summer>\n'
fc+='      <pid name="Collective SAS PI">\n        <input>fcs/hdot-error</input>\n        <kp>0.012</kp>\n        <ki type="trap">0.006</ki>\n        <clipto><min>-0.3</min><max>0.3</max></clipto>\n      </pid>\n'
fc+=fn("fcs/collective-sas",op("product","fcs/collective-sas-pi","fcs/hold-on"))
fc=fc.replace('<fcs_function name="rotor/omr">','<fcs_function name="rotor/omr">')

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
ext=force("rotor_x",(0,0,HUBZ),(1,0,0),p("rotor/fx"))+force("rotor_y",(0,0,HUBZ),(0,1,0),p("rotor/fy"))+force("rotor_z",(0,0,HUBZ),(0,0,1),p("rotor/fz"))
ext+=force("tail",(327,12,30),(0,1,0),p("rotor/tail-thrust"))
ext+=moment("rotor_torque",(0,0,1),p("rotor/q-main"))+moment("hub_pitch",(0,1,0),p("rotor/m-pitch"))+moment("hub_roll",(1,0,0),p("rotor/m-roll"))

def gear(n,x,y,z,k=30000,dmp=6000):
    return f'''    <contact type="BOGEY" name="{n}">
      <location unit="IN"><x> {x} </x><y> {y} </y><z> {z} </z></location>
      <static_friction> 0.7 </static_friction><dynamic_friction> 0.5 </dynamic_friction><rolling_friction> 0.30 </rolling_friction>
      <spring_coeff unit="LBS/FT"> {k} </spring_coeff><damping_coeff unit="LBS/FT/SEC"> {dmp} </damping_coeff>
      <max_steer unit="DEG"> 0 </max_steer><brake_group> NONE </brake_group><retractable>0</retractable>
    </contact>
'''
cg=gear("SKID_FL",-95,-56,-100)+gear("SKID_FR",-95,56,-100)+gear("SKID_RL",51,-56,-100)+gear("SKID_RR",51,56,-100)

xml=f'''<?xml version="1.0"?>
<!--
  Utility / light attack helicopter for RocketWorld, authored for JSBSim 1.x. Single main rotor (R = {R} ft, 2 blades, solidity {SIGMA}, tip speed {OM0*R:.0f} ft/s) + tail rotor;
  6,200 lb, 1,000 shp class engine. NOT FGRotor: the rotor is written with FCS functions and external reactions (see tools/flight2/gen_heli.py):
  blade-element thrust with momentum-theory inflow + dynamic-inflow lag, forward-flight flap-back, rate-coupled flapping, hinge-offset hub moment, profile drag /
  H-force, ground effect, rotor-speed dynamics (droop governor, rotor inertia -> autorotation), tail rotor with yaw-rate hold, attitude-hold SAS.
  Generated file: edit gen_heli.py and re-run it.
-->
<fdm_config name="Helicopter" version="2.0" release="BETA">
  <fileheader><author>RocketWorld</author><filecreationdate>2026-10-29</filecreationdate><version>1.0</version><description>Single rotor helicopter</description></fileheader>
  <metrics>
    <wingarea unit="FT2"> 100 </wingarea><wingspan unit="FT"> 10 </wingspan><chord unit="FT"> 10 </chord>
    <location name="AERORP" unit="IN"><x> 0 </x><y> 0 </y><z> 0 </z></location>
    <location name="EYEPOINT" unit="IN"><x> -90 </x><y> 0 </y><z> 30 </z></location>
    <location name="VRP" unit="IN"><x> 0 </x><y> 0 </y><z> 0 </z></location>
  </metrics>
  <mass_balance>
    <ixx unit="SLUG*FT2"> 1900 </ixx><iyy unit="SLUG*FT2"> 6500 </iyy><izz unit="SLUG*FT2"> 5200 </izz><ixz unit="SLUG*FT2"> 0 </ixz>
    <emptywt unit="LBS"> 4800 </emptywt>
    <location name="CG" unit="IN"><x> 0 </x><y> 0 </y><z> 0 </z></location>
  </mass_balance>
  <ground_reactions>
{cg}  </ground_reactions>
  <external_reactions>
{ext}  </external_reactions>
  <propulsion>
    <tank type="FUEL" number="0"><location unit="IN"><x> 20 </x><y> 0 </y><z> -10 </z></location><capacity unit="LBS"> 1800 </capacity><contents unit="LBS"> 1400 </contents></tank>
  </propulsion>
  <flight_control name="Helicopter">
    <property value="33.5"> rotor/omega-rad_sec </property>
    <property value="1"> propulsion/engine-on </property>\n    <property value="0"> rotor/tail-thrust </property>
    <property value="0.5"> fcs/collective-cmd-norm </property>
    <property value="0"> fcs/vert-cmd-norm </property>
    <property value="1"> fcs/hold-on </property>
    <channel name="Rotor">
{fc}    </channel>
  </flight_control>
  <aerodynamics>
    <axis name="DRAG"><function name="aero/coefficient/fuselage-drag"><description>Equivalent flat-plate area 22 ft2 (drag area, whole airframe)</description><product><property>aero/qbar-psf</property><value>22</value></product></function></axis>
    <axis name="SIDE"><function name="aero/coefficient/fuselage-side"><product><property>aero/qbar-psf</property><property>aero/beta-rad</property><value>-45</value></product></function></axis>
    <axis name="LIFT"><function name="aero/coefficient/fuselage-lift"><product><property>aero/qbar-psf</property><property>aero/alpha-rad</property><value>6</value></product></function></axis>
    <axis name="PITCH"><function name="aero/coefficient/stab-pitch"><description>Fuselage + horizontal stabiliser: weakly stable pitching moment, pitch damping</description><sum>
      <product><property>aero/qbar-psf</property><property>aero/alpha-rad</property><value>-90</value></product>
      <product><property>aero/qbar-psf</property><property>velocities/q-aero-rad_sec</property><value>-14</value></product></sum></function></axis>
    <axis name="YAW"><function name="aero/coefficient/fin-yaw"><description>Vertical fin: weathercock stability and yaw damping</description><sum>
      <product><property>aero/qbar-psf</property><property>aero/beta-rad</property><value>150</value></product>
      <product><property>aero/qbar-psf</property><property>velocities/r-aero-rad_sec</property><value>-30</value></product></sum></function></axis>
    <axis name="ROLL"><function name="aero/coefficient/roll-damp"><product><property>aero/qbar-psf</property><property>velocities/p-aero-rad_sec</property><value>-10</value></product></function></axis>
  </aerodynamics>
</fdm_config>
'''
open(sys.argv[1] if len(sys.argv)>1 else "heli.xml","w").write(xml)
print("wrote",len(xml),"bytes")
