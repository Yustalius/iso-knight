import * as THREE from 'three';

export const DURATIONS = { Idle: 2.4, Walk: 1.16, Attack: 1.8 };
export const WALK_SPEED = .44 / (.6 * DURATIONS.Walk);
const clamp = THREE.MathUtils.clamp;
const smooth = x => { x = clamp(x, 0, 1); return x*x*(3-2*x); };
const vector = values => new THREE.Vector3(...values);

// Nonuniform Hermite tangents preserve momentum through the cut. Only the
// beginning and end of the entire action stop, not every authored pose.
function curve(keys, time) {
  const t = clamp(time, keys[0][0], keys.at(-1)[0]);
  let i = keys.findIndex((key, index) => index < keys.length-1 && t <= keys[index+1][0]);
  if (i < 0) i = keys.length-2;
  const a=keys[i], b=keys[i+1], previous=keys[Math.max(0,i-1)], next=keys[Math.min(keys.length-1,i+2)];
  const span=b[0]-a[0], u=(t-a[0])/span;
  return a.slice(1).map((value,j) => {
    const c=j+1;
    const m0=i===0?0:(b[c]-previous[c])/(b[0]-previous[0]);
    const m1=i===keys.length-2?0:(next[c]-a[c])/(next[0]-a[0]);
    return (2*u**3-3*u*u+1)*value+(u**3-2*u*u+u)*span*m0+(-2*u**3+3*u*u)*b[c]+(u**3-u*u)*span*m1;
  });
}

// A short, bounded underdamped response gives equipment weight without
// stateful simulation: scrubbing and exported clips produce identical poses.
function recoil(time, start, length, frequency) {
  const elapsed=time-start;
  if(elapsed<=0 || elapsed>=length)return 0;
  return Math.sin(elapsed*frequency)*Math.exp(-elapsed*9)*(1-smooth(elapsed/length));
}

function worldRotation(bone, desired) {
  const parent=bone.parent.getWorldQuaternion(new THREE.Quaternion());
  bone.quaternion.copy(parent.invert().multiply(desired));
  bone.updateWorldMatrix(false,true);
}

function solveLimb(upper, lower, end, target, pole) {
  upper.updateWorldMatrix(true,true);
  const origin=upper.getWorldPosition(new THREE.Vector3());
  const upperAxis=lower.position.clone(), lowerAxis=end.position.clone();
  const a=upperAxis.length(), b=lowerAxis.length();
  const direction=target.clone().sub(origin);
  const distance=clamp(direction.length(),Math.abs(a-b)+.001,a+b-.001);
  direction.normalize();
  const bend=pole.clone().sub(origin);
  bend.addScaledVector(direction,-bend.dot(direction)).normalize();
  const along=(a*a-b*b+distance*distance)/(2*distance);
  const height=Math.sqrt(Math.max(0,a*a-along*along));
  const elbow=origin.clone().addScaledVector(direction,along).addScaledVector(bend,height);
  worldRotation(upper,new THREE.Quaternion().setFromUnitVectors(upperAxis.normalize(),elbow.clone().sub(origin).normalize()));
  const actualElbow=lower.getWorldPosition(new THREE.Vector3());
  const reachable=origin.clone().addScaledVector(direction,distance);
  worldRotation(lower,new THREE.Quaternion().setFromUnitVectors(lowerAxis.normalize(),reachable.sub(actualElbow).normalize()));
}

// Sole dimensions are shared with the sabaton mesh in model.js. Raising the
// ankle about the heel/toe pivot keeps the planted sole on the ground.
export const SOLE = { bottom: -.0905, heel: -.0765, toe: .1885, halfWidth: .0715 };
function footTarget(x,z,pitch,lift=0,yaw=0) {
  const q=new THREE.Quaternion().setFromEuler(new THREE.Euler(pitch,yaw,0,'YXZ'));
  const pivot=pitch<0?SOLE.heel:SOLE.toe;
  const contact=new THREE.Vector3(0,SOLE.bottom,pivot).applyQuaternion(q);
  return {position:new THREE.Vector3(x,-contact.y+lift,z+pivot-contact.z),quaternion:q,pitch,lift};
}

function walkingFoot(phase,sign) {
  const p=((phase%1)+1)%1;
  let z,pitch,lift=0;
  const stance=p<.6;
  if(stance) {
    z=.22-.44*p/.6;
    pitch=-.20*(1-smooth(p/.10))+.30*smooth((p-.44)/.16);
  } else {
    const u=(p-.6)/.4;
    // Negative endpoint velocities meet the moving support phase without a snap.
    const tangent=-.44/.6*.4;
    z=(2*u**3-3*u*u+1)*(-.22)+(u**3-2*u*u+u)*tangent+(-2*u**3+3*u*u)*.22+(u**3-u*u)*tangent;
    lift=.112*Math.sin(Math.PI*u)**1.25;
    pitch=curve([[0,.30],[.35,-.10],[.72,-.24],[1,-.20]],u)[0];
  }
  return {...footTarget(sign*.122,z,pitch,lift),phase:p,stance};
}

const wristPath = [
  [0,.36,1.065,.25],[.18,.43,1.15,.18],[.47,.43,1.49,.055],
  [.61,.40,1.55,.10],[.76,.20,1.33,.55],[.88,-.025,1.12,.43],
  [1.03,-.08,1.065,.30],[1.22,.01,1.02,.28],[1.55,.36,1.065,.25],[1.8,.36,1.065,.25]
];
const bladePath = [
  [0,.18,.48,.86],[.18,.28,.73,.62],[.47,.16,.95,-.28],
  [.61,.12,.98,-.17],[.76,-.35,.14,.93],[.88,-.70,-.56,.43],
  [1.03,-.72,-.64,.26],[1.22,-.20,-.25,.95],[1.55,.18,.48,.86],[1.8,.18,.48,.86]
];
// x/y/z pelvis, pelvis yaw, ribcage yaw, forward lean.
const bodyPath = [
  [0,0,.85,0,0,0,.015],[.20,.024,.834,-.025,.12,.12,-.025],
  [.52,.036,.827,-.037,.23,.25,-.05],[.61,.022,.822,-.01,.12,.20,-.025],
  [.76,-.023,.818,.065,-.18,-.22,.105],[.93,-.038,.826,.080,-.30,-.23,.095],
  [1.10,-.025,.834,.045,-.22,-.17,.065],[1.50,0,.85,0,0,0,.015],[1.8,0,.85,0,0,0,.015]
];

export function createAnimationRig({root,bones,hips,spine,chest,neck,head,arms,legs,shield,sword}) {
  const rest=new Map(bones.map(b=>[b,{position:b.position.clone(),quaternion:b.quaternion.clone()}]));
  let lastState;
  function handPose(side,target,bladeDirection,normal,roll=0) {
    const arm=arms[side],sign=side==='R'?1:-1;
    solveLimb(arm.upper,arm.fore,arm.hand,target,new THREE.Vector3(sign*.73,1.08,-.20));
    if(side==='R') {
      const z=bladeDirection.clone().normalize();
      // Choose the closest anatomical wrist roll before aligning the cutting
      // plane. A fixed world up-vector can turn the fist inside out mid-swing.
      const y=arm.fore.getWorldPosition(new THREE.Vector3()).sub(target);
      y.addScaledVector(z,-y.dot(z)).normalize();
      const cuttingNormal=normal.clone().addScaledVector(z,-normal.dot(z)).normalize();
      const alignment=cuttingNormal.dot(y);
      if(alignment<0)cuttingNormal.negate();
      // Fade the roll correction to zero at the sign boundary so crossing it
      // cannot introduce a one-frame pronation jump.
      y.lerp(cuttingNormal,.22*smooth(Math.abs(alignment)/.6)).normalize();
      const x=y.clone().cross(z).normalize();y.copy(z).cross(x).normalize();
      const orientation=new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x,y,z));
      worldRotation(arm.hand,orientation);
    } else worldRotation(arm.hand,new THREE.Quaternion().setFromEuler(new THREE.Euler(0,-.10,roll)));
  }
  function pose(mode,time) {
    const duration=DURATIONS[mode];
    if(duration===undefined)throw new Error('Unknown knight animation: '+mode);
    const t=clamp(time,0,duration);
    for(const b of bones){const r=rest.get(b);b.position.copy(r.position);b.quaternion.copy(r.quaternion);}
    hips.position.set(0,.85,0);
    let feet={R:footTarget(.122,-.025,0),L:footTarget(-.122,.045,0)};
    let rightWrist=new THREE.Vector3(.36,1.065,.25),leftWrist=new THREE.Vector3(-.34,1.105,.225);
    let direction=new THREE.Vector3(.18,.48,.86),bladeNormal=new THREE.Vector3(1,-.78,.14);
    let shieldRoll=0;
    const breath=Math.sin(2*Math.PI*t/DURATIONS.Idle);
    if(mode==='Idle') {
      hips.position.y+=.003*breath;
      spine.rotation.x=.008*breath;chest.rotation.z=.006*breath;
      rightWrist.y+=.004*breath;leftWrist.y+=.003*breath;
    } else if(mode==='Walk') {
      const phase=t/duration,cycle=2*Math.PI*phase;
      const step=((phase*2)%1)*duration/2;
      const impact=recoil(step,0,.30,33);
      hips.position.set(.016*Math.sin(cycle),.851-.014*Math.cos(cycle*2)-.006*impact,0);
      hips.rotation.set(0,.055*Math.cos(cycle),-.024*Math.sin(cycle));
      spine.rotation.x=.033;chest.rotation.y=-.095*Math.cos(cycle);chest.rotation.z=.014*Math.sin(cycle);
      neck.rotation.y=.04*Math.cos(cycle);head.rotation.x=-.025;
      feet={R:walkingFoot(phase,1),L:walkingFoot(phase+.5,-1)};
      rightWrist.set(.36+.009*Math.sin(cycle),1.065+.008*Math.cos(cycle*2),.25-.055*Math.cos(cycle));
      leftWrist.set(-.34,1.105+.007*Math.cos(cycle*2),.225+.022*Math.cos(cycle));
      direction.set(.18+.02*Math.sin(cycle),.48+.025*Math.cos(cycle-.2),.86);
      shieldRoll=.016*impact;shield.rotation.x+=.023*impact;
    } else {
      const [x,y,z,yaw,twist,lean]=curve(bodyPath,t);
      hips.position.set(x,y,z);hips.rotation.set(0,yaw,0);
      spine.rotation.x=lean*.35;chest.rotation.set(lean*.65,twist,-.025*Math.sin(Math.PI*t/duration));
      neck.rotation.y=-(yaw+twist)*.70;head.rotation.x=-lean*.55;
      const step=smooth((t-.27)/.33)*(1-smooth((t-1.16)/.40));
      const lift=.048*Math.sin(Math.PI*clamp((t-.27)/.33,0,1))+.04*Math.sin(Math.PI*clamp((t-1.16)/.40,0,1));
      feet={R:footTarget(.13,-.055,.055*smooth((t-.61)/.22)*(1-smooth((t-1.10)/.30))),L:footTarget(-.142,.045+.18*step,0,lift)};
      const lag=1-smooth((t-1.48)/.28);
      rightWrist=vector(curve(wristPath,Math.max(0,t-.012*lag)));
      direction=vector(curve(bladePath,Math.max(0,t-.025*lag)));
      const follow=recoil(t,.91,.50,22);
      rightWrist.y+=.011*follow;direction.y+=.024*follow;
      const retract=smooth((t-.20)/.32)*(1-smooth((t-1.08)/.45));
      leftWrist.set(-.34-.055*retract,1.105-.025*retract,.225-.13*retract);
      shieldRoll=-.09*retract+.035*follow;shield.rotation.y-=.16*retract;
    }
    root.updateMatrixWorld(true);
    for(const side of ['R','L']) {
      const leg=legs[side],target=feet[side];
      solveLimb(leg.thigh,leg.shin,leg.foot,target.position,new THREE.Vector3(side==='R'?.15:-.15,.5,1));
      worldRotation(leg.foot,target.quaternion);
    }
    handPose('R',rightWrist,direction,bladeNormal);
    handPose('L',leftWrist,null,null,shieldRoll);
    root.updateMatrixWorld(true);
    lastState={feet,rightWrist,leftWrist,direction:direction.normalize()};
  }
  function diagnostics() {
    const point=b=>b.getWorldPosition(new THREE.Vector3()).toArray();
    const feet={};
    for(const side of ['R','L']) {
      const foot=legs[side].foot;
      const corners=[];
      for(const x of [-SOLE.halfWidth,SOLE.halfWidth])for(const z of [SOLE.heel,SOLE.toe])corners.push(new THREE.Vector3(x,SOLE.bottom,z).applyMatrix4(foot.matrixWorld));
      feet[side]={ankle:point(foot),knee:point(legs[side].shin),hip:point(legs[side].thigh),minY:Math.min(...corners.map(p=>p.y)),target:lastState.feet[side].position.toArray(),stance:lastState.feet[side].stance??lastState.feet[side].lift<.0001};
    }
    return {feet,wrist:point(arms.R.hand),wristQuaternion:arms.R.hand.getWorldQuaternion(new THREE.Quaternion()).toArray(),wristTarget:lastState.rightWrist.toArray(),swordTip:new THREE.Vector3(0,-.77,0).applyMatrix4(sword.matrixWorld).toArray(),swordHilt:point(sword),bladeDirection:lastState.direction.toArray()};
  }
  const clips=[];
  for(const [name,duration] of Object.entries(DURATIONS)) {
    const times=[],quaternions=bones.map(()=>[]),positions=[];
    const frames=Math.ceil(duration*60);
    for(let frame=0;frame<=frames;frame++){
      const t=frame*duration/frames;times.push(t);pose(name,t);positions.push(...hips.position.toArray());
      bones.forEach((b,j)=>{
        const values=quaternions[j],q=b.quaternion.clone();
        if(values.length && q.dot(new THREE.Quaternion(...values.slice(-4)))<0)q.set(-q.x,-q.y,-q.z,-q.w);
        values.push(...q.toArray());
      });
    }
    const tracks=bones.map((b,j)=>new THREE.QuaternionKeyframeTrack(b.name+'.quaternion',times,quaternions[j]));
    tracks.push(new THREE.VectorKeyframeTrack('Hips.position',times,positions));
    clips.push(new THREE.AnimationClip(name,duration,tracks));
  }
  pose('Idle',0);
  return {pose,clips,diagnostics};
}
