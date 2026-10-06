import * as THREE from 'three';
import { SOLE } from './knight-model.js';

// Procedural, speed-driven animation for the knight. Nothing is a canned clip:
// the gait is derived from the actual local velocity every frame, so feet stay
// planted at any speed, while strafing, backing up and turning on the spot.

const clamp = THREE.MathUtils.clamp, lerp = THREE.MathUtils.lerp;
const smooth = x => { x = clamp(x, 0, 1); return x*x*(3-2*x); };
const damp = (a, b, k, dt) => a + (b-a)*(1-Math.exp(-k*dt));
const frac = x => x - Math.floor(x);
const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
const hermite = (p0, m0, p1, m1, u) => (2*u**3-3*u*u+1)*p0 + (u**3-2*u*u+u)*m0 + (-2*u**3+3*u*u)*p1 + (u**3-u*u)*m1;

// Non-uniform Hermite through keys [t, ...values]; momentum carries through
// interior keys, only the first and last keys come to rest.
function curve(keys, time) {
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

class Spring {
  constructor(k, c) { this.k = k; this.c = c; this.x = 0; this.v = 0; }
  step(target, dt, force = 0) { this.v += (this.k*(target-this.x) - this.c*this.v + force)*dt; this.x += this.v*dt; return this.x; }
}

const _q = new THREE.Quaternion(), _q2 = new THREE.Quaternion(), _v = V(), _v2 = V();
function worldRotation(bone, desired) {
  bone.parent.getWorldQuaternion(_q);
  bone.quaternion.copy(_q.invert().multiply(desired));
  bone.updateWorldMatrix(false, true);
}
// Two-bone analytic IK (shared with the reference rig): the bend plane passes through `pole`.
function solveLimb(upper, lower, end, target, pole) {
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
function footTarget(x, z, pitch, lift = 0, yaw = 0) {
  const q = new THREE.Quaternion().setFromEuler(new THREE.Euler(pitch, yaw, 0, 'YXZ'));
  const pivot = pitch < 0 ? SOLE.heel : SOLE.toe;
  const c = V(0, SOLE.bottom, pivot).applyQuaternion(q);
  return { pos: V(x, -c.y + lift, z + pivot - c.z), q };
}

// Attacks. Wrist and blade paths are authored in character space (+Z forward, sword hand at -X).
// body: [t, hipsX, hipsDY, hipsZ, pelvisYaw, chestTwist, lean]; feet: [t, shieldFootZ, swordHeelPitch]
export const ATTACKS = {
  slash: {
    name: 'Нисходящий диагональный', dur: .78, hit: .345, chain: .42, reach: 1.8, arc: 1.15, swing: [1, -.4, .35], normal: [-1, -.78, .14],
    wrist: [[0,-.36,1.065,.25],[.07,-.43,1.15,.18],[.19,-.43,1.49,.055],[.25,-.40,1.55,.10],[.32,-.20,1.33,.55],[.38,.025,1.12,.43],[.46,.08,1.065,.30],[.56,-.01,1.03,.28],[.78,-.36,1.065,.25]],
    blade: [[0,-.18,.48,.86],[.07,-.28,.73,.62],[.19,-.16,.95,-.28],[.25,-.12,.98,-.17],[.32,.35,.14,.93],[.38,.70,-.56,.43],[.46,.72,-.64,.26],[.56,.20,-.25,.95],[.78,-.18,.48,.86]],
    body: [[0,0,0,0,0,0,.015],[.07,-.024,-.016,-.025,-.12,-.12,-.025],[.19,-.036,-.023,-.037,-.23,-.25,-.05],[.25,-.022,-.028,-.01,-.12,-.2,-.025],[.32,.023,-.032,.065,.18,.22,.105],[.40,.038,-.024,.08,.3,.23,.095],[.5,.025,-.016,.045,.22,.17,.065],[.78,0,0,0,0,0,.015]],
    feet: [[0,0,0],[.1,0,0],[.3,.18,0],[.38,.18,.06],[.55,.18,.06],[.78,0,0]]
  },
  backhand: {
    name: 'Обратный горизонтальный', dur: .66, hit: .265, chain: .36, reach: 1.85, arc: 1.35, swing: [-1, .05, .3], normal: [0, 1, 0],
    wrist: [[0,.08,1.065,.30],[.1,.22,1.13,.16],[.2,.12,1.2,.5],[.27,-.18,1.25,.6],[.34,-.45,1.28,.38],[.44,-.5,1.3,.12],[.66,-.36,1.065,.25]],
    blade: [[0,.72,-.64,.26],[.1,.5,.12,-.85],[.2,.95,.12,.3],[.27,0,.12,1],[.34,-.95,.15,.25],[.44,-.7,.3,-.6],[.66,-.18,.48,.86]],
    body: [[0,.03,-.02,.06,.26,.2,.08],[.1,.03,-.03,.05,.32,.3,.06],[.2,0,-.035,.06,.1,.05,.08],[.27,-.02,-.035,.07,-.15,-.2,.09],[.34,-.035,-.03,.06,-.3,-.32,.08],[.44,-.03,-.02,.04,-.3,-.3,.05],[.66,0,0,0,0,0,.015]],
    feet: [[0,.18,.06],[.2,.18,.06],[.34,.08,0],[.66,0,0]]
  }
};

export function createRig(knight) {
  const { root, bones } = knight;
  const { hips, spine, chest, neck, head, arms, legs, shield, flaps } = knight.j;
  const rest = bones.map(b => [b, b.position.clone(), b.quaternion.clone()]);
  const rootQ = new THREE.Quaternion();
  const toW = v => root.localToWorld(v.clone());
  const dirW = v => v.clone().applyQuaternion(rootQ);

  const s = {
    phase: 0, moveW: 0, runW: 0, block: 0, acc: V(), time: 0,
    footYaw: { L: .14, R: -.1 }, stanceDrive: 0,
    flapL: new Spring(150, 11), flapR: new Spring(150, 11), flapB: new Spring(110, 8),
    shX: new Spring(170, 10), shZ: new Spring(170, 10), impact: new Spring(260, 18),
    wristPrev: null, wristVel: V(),
    xf: 1, snap: null, last: null, events: [], wasStance: { L: true, R: true }
  };
  const IDLE_FEET = { L: [.125, .06, .14], R: [-.13, -.035, -.1] };

  function sampleAttack(def, t) {
    const lag = 1 - smooth((t - (def.dur - .25))/.2);
    const [wx, wy, wz] = curve(def.wrist, t);
    const [bx, by, bz] = curve(def.blade, Math.max(0, t - .022*lag)); // the blade trails the fist: whip
    const [hx, hdy, hz, yaw, tw, lean] = curve(def.body, t);
    const [fz, rp] = curve(def.feet, t);
    const [fz2] = curve(def.feet, Math.max(0, t - .03));
    return { wrist: V(wx, wy, wz), blade: V(bx, by, bz).normalize(), normal: V(...def.normal), body: [hx, hdy, hz, yaw, tw, lean], fz, rp, fv: Math.abs(fz - fz2)/.03 };
  }

  // input: velLocal (V, m/s), accLocal (V), yawRate, block (bool), attack {def,t} | null, newAttack (bool), impact (bool)
  function update(dt, input) {
    s.time += dt;
    const vl = input.velLocal, v = Math.hypot(vl.x, vl.z);
    s.acc.x = damp(s.acc.x, clamp(input.accLocal.x, -12, 12), 10, dt);
    s.acc.z = damp(s.acc.z, clamp(input.accLocal.z, -12, 12), 10, dt);
    s.block = damp(s.block, input.block ? 1 : 0, 14, dt);

    // ---------- gait parameters from the real velocity ----------
    const turn = smooth((Math.abs(input.yawRate) - .9)/2.2) * .55;   // turning on the spot shuffles the feet
    const drive = Math.max(v, turn);
    s.moveW = damp(s.moveW, clamp(drive/.3, 0, 1), 9, dt);
    s.runW = damp(s.runW, smooth((v - 1.6)/1.1), 6, dt);
    const cadence = lerp(.78 + .52*clamp(drive/1.4, 0, 1), 1.48, s.runW);   // gait cycles per second
    s.phase += cadence*dt;
    const duty = lerp(.6, .37, s.runW);
    const travel = Math.min(.78, v/cadence*duty);                         // stance foot slides back exactly at body speed
    const u = v > 1e-3 ? V(vl.x/v, 0, vl.z/v) : V(0, 0, 1);
    const fwd = u.z;                                                     // heel–toe roll only when going forward
    const lift = lerp(.1, .17, s.runW) * clamp(drive/.55, .35, 1);
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
      if (stance && s.moveW > .2) s.footYaw[side] = clamp(s.footYaw[side] - input.yawRate*dt, baseYaw - .45, baseYaw + .45);
      else s.footYaw[side] = damp(s.footYaw[side], s.moveW > .2 ? baseYaw : IDLE_FEET[side][2], 10, dt);
      const bx = side === 'L' ? .122 : -.122;
      const strike = stance && !s.wasStance[side] && s.moveW > .5; s.wasStance[side] = stance;
      gait[side] = { strike, x: bx + u.x*o, z: u.z*o, lift: h, pitch: pitch*fwd*clamp(v/.6, 0, 1), o, p, stance, bell: stance ? Math.sin(Math.PI*p/duty) : 0 };
    }

    // ---------- attack layer ----------
    let atk = null, atkW = 0;
    if (input.attack) {
      const { def, t } = input.attack;
      atk = sampleAttack(def, t);
      atkW = smooth(t/.05) * (1 - smooth((t - (def.dur - .1))/.1));
      if (input.newAttack && s.last) { s.snap = s.last; s.xf = 0; }
    }
    s.xf = Math.min(1, s.xf + dt/.08);
    if (input.impact) s.impact.v += 9;
    const imp = s.impact.step(0, dt);

    // ---------- body ----------
    const mw = s.moveW, rw = s.runW, bl = s.block;
    const breath = Math.sin(s.time*2*Math.PI/2.4);
    const bob = lerp(.014, .032, rw)*Math.cos(4*Math.PI*(gait.R.p - .18*rw))*mw;
    let hx = .02*(gait.L.bell - gait.R.bell)*mw*(1 - rw*.6);
    let hy = lerp(.85 + .003*breath, lerp(.855, .83, rw), mw) - bob - .035*bl;
    let hz = 0;
    let pelvisYaw = -.12*(gait.L.z - gait.R.z)*mw;
    let twist = -1.7*pelvisYaw;
    let lean = lerp(.012 + .008*breath, lerp(.035, .15, rw), mw) + .05*bl + clamp(s.acc.z*.018, -.12, .14);
    const bank = clamp(-s.acc.x*.02, -.14, .14);
    let roll = .025*(gait.L.bell - gait.R.bell)*mw + bank;

    // ---------- arms (character space) ----------
    const swingR = -lerp(.2, .45, rw)*gait.R.o*fwd*mw, swingL = -lerp(.08, .3, rw)*gait.L.o*fwd*mw;
    let rWrist = V(-.36, 1.065 + .004*breath, .25)
      .lerp(V(-.31, 1.0, .17), rw*mw).add(V(0, -.01*Math.abs(swingR), swingR));
    let blade = V(-.18, .48, .86).lerp(V(-.2, .78, .5), rw*mw).normalize();
    let lWrist = V(.34, 1.105 + .003*breath, .225).lerp(V(.3, 1.06, .18), rw*mw).add(V(0, 0, swingL));
    rWrist.lerp(V(-.40, 1.21, .03), bl); blade.lerp(V(-.22, .9, .35), bl).normalize();
    lWrist.lerp(V(.1, 1.27, .36), bl);
    let normal = V(-1, -.78, .14);

    if (atk) {
      let [ax, ady, az, ayaw, atw, alean] = atk.body;
      rWrist.lerp(atk.wrist, atkW); blade.lerp(atk.blade, atkW).normalize(); normal.lerp(atk.normal, atkW);
      const still = 1 - mw*.8;                       // when walking, the legs keep the hips
      hx += ax*atkW*still; hy += ady*atkW; hz += az*atkW*still;
      pelvisYaw += ayaw*atkW; twist += atw*atkW; lean += (alean - .015)*atkW;
      const r = smooth((input.attack.t - .04)/.14)*(1 - smooth((input.attack.t - (input.attack.def.dur - .24))/.22));
      lWrist.lerp(V(.395, 1.08, .095), r*(1 - bl));
    }
    if (imp) rWrist.addScaledVector(atk ? V(...input.attack.def.swing).normalize() : blade, -imp*.08);

    // crossfade from the previous frame's result when a combo cuts an attack short
    const out = { rWrist, blade, lWrist, b: [hx, hy, hz, pelvisYaw, twist, lean] };
    if (s.snap && s.xf < 1) {
      const w = smooth(s.xf);
      rWrist.lerpVectors(s.snap.rWrist, rWrist, w); lWrist.lerpVectors(s.snap.lWrist, lWrist, w);
      blade.lerpVectors(s.snap.blade, blade, w).normalize();
      out.b = out.b.map((x, i) => lerp(s.snap.b[i], x, w));
      [hx, hy, hz, pelvisYaw, twist, lean] = out.b;
    }
    s.last = { rWrist: rWrist.clone(), lWrist: lWrist.clone(), blade: blade.clone(), b: out.b.slice() };

    // ---------- apply ----------
    for (const [b, p, q] of rest) { b.position.copy(p); b.quaternion.copy(q); }
    hips.position.set(hx, hy, hz);
    hips.rotation.set(0, pelvisYaw, roll);
    spine.rotation.set(lean*.35, 0, -roll*.5);
    chest.rotation.set(lean*.65, twist, -roll*.3);
    neck.rotation.set(0, -(pelvisYaw + twist)*.75, 0);
    head.rotation.set(-lean*.5 + .03*bl, 0, 0);
    root.updateMatrixWorld(true);
    root.getWorldQuaternion(rootQ);

    // legs: blend gait → idle stance → attack footwork, then IK
    const thighFwd = {};
    for (const side of ['L', 'R']) {
      const g = gait[side], id = IDLE_FEET[side];
      let x = lerp(id[0], g.x, mw), z = lerp(id[1], g.z, mw), lift = g.lift*mw, pitch = g.pitch*mw;
      if (atk) {
        const w = atkW*(1 - mw);
        if (side === 'L') { z += atk.fz*w; lift += Math.min(.06, atk.fv*.12)*w; }
        else pitch = lerp(pitch, atk.rp, w);
      }
      if (bl > 0) x += (side === 'L' ? .03 : -.03)*bl*(1 - mw);
      const ft = footTarget(x, z, pitch, lift, s.footYaw[side]);
      const leg = legs[side];
      solveLimb(leg.thigh, leg.shin, leg.foot, toW(ft.pos), toW(V(side === 'R' ? -.15 : .15, .5, 1.2)));
      worldRotation(leg.foot, _q2.copy(rootQ).multiply(ft.q));
      // thigh swing relative to the pelvis, used by the tabard collision
      const kneeL = hips.worldToLocal(leg.shin.getWorldPosition(_v)), hipL = leg.thigh.position;
      thighFwd[side] = Math.atan2(kneeL.z - hipL.z, -(kneeL.y - hipL.y));
      if (gait[side].strike) s.events.push({ side, speed: v, dir: V(-u.x, 0, -u.z), pos: leg.foot.getWorldPosition(V()).setY(.04) });
    }

    // right arm + sword: the wrist roll closest to the forearm, nudged toward the cutting plane
    {
      const arm = arms.R, target = toW(rWrist);
      solveLimb(arm.upper, arm.fore, arm.hand, target, toW(V(-.73, 1.08, -.2)));
      const z = dirW(blade);
      const y = arm.fore.getWorldPosition(V()).sub(target);
      y.addScaledVector(z, -y.dot(z)).normalize();
      const n = dirW(normal); n.addScaledVector(z, -n.dot(z)).normalize();
      const al = n.dot(y); if (al < 0) n.negate();
      y.lerp(n, .3*smooth(Math.abs(al)/.6)).normalize();
      const x = y.clone().cross(z).normalize(); y.copy(z).cross(x).normalize();
      worldRotation(arm.hand, _q2.setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, y, z)));
    }
    // left arm + shield
    {
      const arm = arms.L, target = toW(lWrist);
      solveLimb(arm.upper, arm.fore, arm.hand, target, toW(V(.73, 1.08, -.2)));
      const e = new THREE.Euler(lerp(0, -.12, bl), lerp(.1, -.55, bl), lerp(0, .1, bl));
      worldRotation(arm.hand, _q2.copy(rootQ).multiply(new THREE.Quaternion().setFromEuler(e)));
      // shield inertia from the wrist's acceleration
      const wp = target;
      if (s.wristPrev && dt > 0) {
        const vel = wp.clone().sub(s.wristPrev).divideScalar(dt);
        const acc = vel.clone().sub(s.wristVel).divideScalar(dt).applyQuaternion(_q.copy(rootQ).invert());
        s.wristVel.copy(vel);
        acc.clampScalar(-35, 35);
        s.shX.step(0, dt, -acc.z*2.4 + acc.y*1.4); s.shZ.step(0, dt, acc.x*2.4);
      }
      s.wristPrev = wp;
      shield.rotation.set(-.08 + clamp(s.shX.x, -.35, .35), .16, .08 + clamp(s.shZ.x, -.35, .35));
    }

    // tabard panels: springs with the thighs as colliders
    const accF = s.acc.z;
    for (const side of ['L', 'R']) {
      const sp = side === 'L' ? s.flapL : s.flapR, limit = -Math.max(0, thighFwd[side])*.72 - .02;
      sp.step(Math.min(limit, .04 + .05*rw), dt, accF*5);
      if (sp.x > limit) { sp.x = limit; if (sp.v > 0) sp.v = 0; }
      flaps[side].rotation.x = sp.x;
    }
    {
      const back = Math.max(0, -thighFwd.L, -thighFwd.R)*.6 + .05;
      const wind = .18*clamp(v/2.8, 0, 1)*fwd + Math.sin(s.time*15)*.05*rw*mw;
      s.flapB.step(Math.max(back, .06 + wind), dt, accF*4);
      if (s.flapB.x < back) { s.flapB.x = back; if (s.flapB.v < 0) s.flapB.v = 0; }
      flaps.B.rotation.x = s.flapB.x;
    }
    root.updateMatrixWorld(true);
  }

  return { update, state: s };
}
