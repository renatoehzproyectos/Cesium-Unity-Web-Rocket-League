import jsbsim, math, sys
fdm=jsbsim.FGFDMExec('/home/claude/jsbtest',None); fdm.set_debug_level(0)
fdm.load_model('heli'); fdm.set_dt(1/120)
fdm['ic/h-sl-ft']=500; fdm['ic/vt-kts']=0; fdm['ic/psi-true-deg']=90; fdm.run_ic()
def run(T,coll=0.5,el=0,ai=0,ru=0,vert=0):
    for i in range(int(T*120)):
        fdm['fcs/collective-cmd-norm']=coll; fdm['fcs/elevator-cmd-norm']=el; fdm['fcs/aileron-cmd-norm']=ai; fdm['fcs/rudder-cmd-norm']=ru; fdm['fcs/vert-cmd-norm']=vert
        fdm.run()
def show(t):
    g=lambda k: fdm[k]
    print(f'{t:22s} alt {g("position/h-sl-ft"):6.0f} hdot {g("velocities/h-dot-fps"):5.1f} u {g("velocities/u-fps"):5.1f} v {g("velocities/v-fps"):5.1f} th {g("attitude/theta-deg"):5.1f} phi {g("attitude/phi-deg"):5.1f} r {g("velocities/r-rad_sec"):6.3f} om {g("rotor/omega-rad_sec"):5.2f} T {g("rotor/thrust-lbs"):6.0f} Q {g("rotor/q-main"):6.0f} tail {g("rotor/tail-thrust"):5.0f} lami {g("rotor/lami"):.3f} P {g("rotor/p-rotor")/550:5.0f}hp coll {g("fcs/collective-total"):.2f}')
for k in range(6): run(3); show(f'hover {3*k+3}s')
print('--- collective +0.1'); run(3,coll=0.6); show('coll .6')
run(4,coll=0.5); show('coll .5')
print('--- vert hold +1'); run(5,vert=1); show('vert +1'); run(5,vert=0); show('vert 0')
print('--- stick fwd 0.5'); run(8,el=0.5); show('fwd .5'); run(8,el=0.5); show('fwd .5 more')
print('--- rudder'); run(3,el=0.5,ru=-0.6); show('ped nose right'); run(4,el=0.5); show('ped 0')
print('--- roll'); run(3,el=0.5,ai=0.5); show('ail .5'); run(4,el=0.5); show('ail 0')
print('--- release'); run(10); show('stick 0')
