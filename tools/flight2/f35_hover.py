import jsbsim, math, sys
fdm=jsbsim.FGFDMExec('/home/claude/jsbtest',None); fdm.set_debug_level(0)
fdm.load_model('f35b'); fdm.set_dt(1/120)
fdm['ic/h-sl-ft']=1500; fdm['ic/vt-kts']=90; fdm['ic/psi-true-deg']=90; fdm['ic/gamma-deg']=0
fdm.run_ic(); fdm['propulsion/set-running']=-1
fdm['gear/gear-cmd-norm']=0; fdm['gear/gear-pos-norm']=0
def run(T,el=0,ai=0,ru=0,lever=0.0,vert=0,stovl=1):
    for i in range(int(T*120)):
        fdm['fcs/elevator-cmd-norm']=el; fdm['fcs/aileron-cmd-norm']=ai; fdm['fcs/rudder-cmd-norm']=ru; fdm['fcs/lever-norm']=lever
        fdm['fcs/vert-cmd-norm']=vert; fdm['fcs/stovl-cmd-norm']=stovl; fdm['fcs/throttle-cmd-norm[0]']=0; fdm.run()
def show(tag):
    print(tag,'alt',round(fdm['position/h-sl-ft']),'kt',round(fdm['velocities/vt-fps']/1.68781),'hdot',round(fdm['velocities/h-dot-fps'],1),'th',round(fdm['attitude/theta-deg'],1),'phi',round(fdm['attitude/phi-deg'],1),'r',round(fdm['velocities/r-rad_sec'],3),'noz',round(fdm['fcs/nozzle-pos-deg']),'fan',round(fdm['propulsion/engine[1]/thrust-lbs']),'main',round(fdm['propulsion/engine[0]/thrust-lbs']),'thr',round(fdm['fcs/throttle-pos-norm'],2),'rp',round(fdm['fcs/roll-post-left-lbs']))
for k in range(12): run(2); show(f'{2*k+2:3d}s')
print('climb'); run(4,vert=1); show('up'); run(4); show('hold')
print('lever .5 (accelerate fwd)'); run(8,lever=0.5); show('acc')
