import * as THREE from 'three';

// Shared building blocks of the procedural rigs (knight, soldier): Hermite key curves,
// damped springs, analytic two-bone IK, heel/toe foot placement and the speed-driven gait.

export const clamp = THREE.MathUtils.clamp, lerp = THREE.MathUtils.lerp;
export const smooth = x => { x = clamp(x, 0, 1); return x*x*(3-2*x); };
export const damp = (a, b, k, dt) => a + (b-a)*(1-Math.exp(-k*dt));
export const frac = x => x - Math.floor(x);
export const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
export const hermite = (p0, m0, p1, m1, u) => (2*u**3-3*u*u+1)*p0 + (u**3-2*u*u+u)*m0 + (-2*u**3+3*u*u)*p1 + (u**3-u*u)*m1;

// Non-uniform Hermite through keys [t, ...values]; momentum carries through
// interior keys, only the first and last keys come to rest.
export function curve(keys, time) {
  const t = clamp(time, keys[0][0], keys[keys.length-1][0]);
  let i = 0; while (i < keys.length-2 && t > keys[i+1][0]) i++;
  const a = keys[i], b = keys[i+1], prev = keys[Math.max(0, i-1)], next = keys[Math.min(keys.length-1, i+2)];
  const span = b[0]-a[0], u = (t-a[0])/span, out = [];
  for (let c = 1; c < a.length; c++) {
    const m0 = i === 0 ? 0 : (b[c]-prev[c])/(b[0]-prev[0]);
    const m1 = i === keys.length-2 ? 0 : (next[c]-a[c])/(next[0]-a[0]);
    out.push(hermite(a[c], span*m0, b[c], span*m1, u));
  }
  return out;
}

export class Spring {
  constructor(k, c) { this.k = k; this.c = c; this.x = 0; this.v = 0; }
  step(target, dt, force = 0) { this.v += (this.k*(target-this.x) - this.c*this.v + force)*dt; this.x += this.v*dt; return this.x; }
}

const _q = new THREE.Quaternion();
export function worldRotation(bone, desired) {
  bone.parent.getWorldQuaternion(_q);
  bone.quaternion.copy(_q.invert().multiply(desired));
  bone.updateWorldMatrix(false, true);
}
// Two-bone analytic IK (shared with the reference rig): the bend plane passes through `pole`.
export function solveLimb(upper, lower, end, target, pole) {
  upper.updateWorldMatrix(true, true);
  const origin = upper.getWorldPosition(V());
  const upperAxis = lower.position.clone(), lowerAxis = end.position.clone();
  const a = upperAxis.length(), b = lowerAxis.length();
  const dir = target.clone().sub(origin);
  const dist = clamp(dir.length(), Math.abs(a-b) + .001, a + b - .001);
  dir.normalize();
  const bend = pole.clone().sub(origin);
  bend.addScaledVector(dir, -bend.dot(dir)).normalize();
  const along = (a*a - b*b + dist*dist)/(2*dist), h = Math.sqrt(Math.max(0, a*a - along*along));
  const elbow = origin.clone().addScaledVector(dir, along).addScaledVector(bend, h);
  bone_q(upper, upperAxis.normalize(), elbow.clone().sub(origin).normalize());
  const actual = lower.getWorldPosition(V());
  bone_q(lower, lowerAxis.normalize(), origin.addScaledVector(dir, dist).sub(actual).normalize());
}
function bone_q(bone, restAxis, worldDir) {
  // rest axis is expressed in the bone's parent frame at rest; rotate it onto worldDir
  worldRotation(bone, new THREE.Quaternion().setFromUnitVectors(restAxis, worldDir));
}
// Ankle placement that pivots the sole about heel (pitch<0) or toe (pitch>0) so the contact stays on the ground.
export function footTarget(sole, x, z, pitch, lift = 0, yaw = 0) {
  const q = new THREE.Quaternion().setFromEuler(new THREE.Euler(pitch, yaw, 0, 'YXZ'));
  const pivot = pitch < 0 ? sole.heel : sole.toe;
  const c = V(0, sole.bottom, pivot).applyQuaternion(q);
  return { pos: V(x, -c.y + lift, z + pivot - c.z), q };
}

export function gaitState() {
  return { phase: 0, moveW: 0, runW: 0, footYaw: { L: .14, R: -.1 }, wasStance: { L: true, R: true } };
}

// Gait from the real local velocity: cadence, stride and duty factor follow speed, the stance
// foot slides back exactly at body speed (no skating), turning on the spot shuffles the feet.
// Mutates s (phase, moveW, runW, footYaw, wasStance). o.crouch shortens and slows the stride.
export function stepGait(s, dt, vl, yawRate, idleFeet, o = {}) {
  const crouch = o.crouch || 0, width = o.width || .122;
  const v = Math.hypot(vl.x, vl.z);
  const turn = smooth((Math.abs(yawRate) - .9)/2.2) * .55;   // turning on the spot shuffles the feet
  const drive = Math.max(v, turn);
  s.moveW = damp(s.moveW, clamp(drive/.3, 0, 1), 9, dt);
  s.runW = damp(s.runW, smooth((v - 1.6)/1.1), 6, dt);
  const cadence = lerp(.78 + .52*clamp(drive/1.4, 0, 1), 1.48, s.runW) * (1 - .22*crouch);   // gait cycles per second
  s.phase += cadence*dt;
  const duty = lerp(.6, .37, s.runW) + .08*crouch;
  const travel = Math.min(.78 - .2*crouch, v/cadence*duty);          // stance foot slides back exactly at body speed
  const u = v > 1e-3 ? V(vl.x/v, 0, vl.z/v) : V(0, 0, 1);
  const fwd = u.z;                                                     // heel–toe roll only when going forward
  const lift = lerp(.1, .17, s.runW) * clamp(drive/.55, .35, 1) * (1 - .3*crouch);
  const swingTan = -travel*(1-duty)/duty;

  const gait = {};
  for (const side of ['L', 'R']) {
    const p = frac(s.phase + (side === 'L' ? .5 : 0));
    let o, h = 0, pitch, stance = p < duty;
    if (stance) {
      const a = p/duty; o = travel/2 - travel*a;
      pitch = -.2*(1 - smooth(a/.17)) + .3*smooth((a - .73)/.27);
    } else {
      const a = (p - duty)/(1 - duty);
      o = hermite(-travel/2, swingTan, travel/2, swingTan, a);
      h = lift*Math.sin(Math.PI*a)**1.25;
      pitch = curve([[0,.3],[.35,-.1],[.72,-.24],[1,-.2]], a)[0];
    }
    // a planted foot keeps its world heading while the body turns; it re-aligns in the air
    const baseYaw = side === 'L' ? .08 : -.08;
    if (stance && s.moveW > .2) s.footYaw[side] = clamp(s.footYaw[side] - yawRate*dt, baseYaw - .45, baseYaw + .45);
    else s.footYaw[side] = damp(s.footYaw[side], s.moveW > .2 ? baseYaw : idleFeet[side][2], 10, dt);
    const bx = side === 'L' ? width : -width;
    const strike = stance && !s.wasStance[side] && s.moveW > .5; s.wasStance[side] = stance;
    gait[side] = { strike, x: bx + u.x*o, z: u.z*o, lift: h, pitch: pitch*fwd*clamp(v/.6, 0, 1), o, p, stance, bell: stance ? Math.sin(Math.PI*p/duty) : 0 };
  }
  return { gait, v, u, fwd };
}
