import jsbsim, math, sys
fdm=jsbsim.FGFDMExec('/home/claude/jsbtest',None); fdm.set_debug_level(0)
fdm.load_model('f35b'); fdm.set_dt(1/120)
fdm['ic/h-sl-ft']=10000; fdm['ic/vt-kts']=float(sys.argv[1]) if len(sys.argv)>1 else 350; fdm['ic/psi-true-deg']=90; fdm['ic/gamma-deg']=0
fdm.run_ic(); fdm['propulsion/set-running']=-1
fdm['gear/gear-cmd-norm']=0; fdm['gear/gear-pos-norm']=0
def run(T,el=0,ai=0,ru=0,lever=0.55):
    for i in range(int(T*120)):
        t=i/120
        fdm['fcs/elevator-cmd-norm']=el(t) if callable(el) else el
        fdm['fcs/aileron-cmd-norm']=ai(t) if callable(ai) else ai
        fdm['fcs/rudder-cmd-norm']=ru; fdm['fcs/lever-norm']=lever
        for n in (0,): fdm[f'fcs/throttle-cmd-norm[{n}]']=0
        fdm.run()
def show(tag):
    print(tag,'alt',round(fdm['position/h-sl-ft']),'kt',round(fdm['velocities/vc-kts']),'M',round(fdm['velocities/mach'],2),'aoa',round(fdm['aero/alpha-deg'],1),'th',round(fdm['attitude/theta-deg'],1),'phi',round(fdm['attitude/phi-deg'],1),'q',round(fdm['velocities/q-rad_sec'],3),'elev',round(fdm['fcs/elevator-pos-deg'],1),'T',round(fdm['propulsion/engine/thrust-lbs']),'Nz',round(fdm['accelerations/Nz'],2))
for k in range(6): run(2); show(f'{2*k+2:3d}s')
print('pull 0.5 for 3s'); run(3, el=0.5); show('pull'); run(3,el=0); show('rel ')
print('roll 1.0 for 1s'); run(1, ai=1.0); show('roll'); run(2); show('rel ')
print('push -0.5'); run(2, el=-0.5); show('push'); run(3); show('rel ')
