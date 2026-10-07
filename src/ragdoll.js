import * as THREE from 'three';
import { GUN } from './soldier-model.js';
import { worldRotation } from './rig-core.js';

// Position-based ragdoll for the soldier skeleton. Joints are verlet particles, bones are rigid
// distance constraints, joint limits are distance ranges plus hinge/back-bend projections, the
// ground and the walk colliders push particles out with friction. A short-lived "muscle tone"
// holds the upper body's shape relative to the pelvis while the legs give out first, so a body
// crumples and topples along the hit instead of dropping like a plank (the tone is a set of extra
// distance constraints over the death pose, whose stiffness decays). The bones of the skinned
// mesh are then posed from the particles. The rifle falls separately as a rigid three-point body.
// Everything that acts between particles is a central, mass-weighted correction, so the body can
// only gain momentum from the bullet, gravity and the ground.

const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
const G = 9.8, SUB = 4, ITER = 8;

// [name, bone, offset in bone space, radius, mass kg]
const PARTS = [
  ['hipL', 'Thigh_L', [0,0,0], .1, 10], ['hipR', 'Thigh_R', [0,0,0], .1, 10],
  ['spine', 'Spine', [0,.02,.03], .14, 14],
  ['shL', 'UpperArm_L', [0,0,0], .085, 6], ['shR', 'UpperArm_R', [0,0,0], .085, 6],
  ['neck', 'Neck', [0,0,0], .07, 4], ['head', 'Head', [0,.1,.01], .13, 5],
  ['elL', 'Forearm_L', [0,0,0], .055, 2], ['haL', 'Hand_L', [0,-.06,0], .05, 1.2],
  ['elR', 'Forearm_R', [0,0,0], .055, 2], ['haR', 'Hand_R', [0,-.06,0], .05, 1.2],
  ['knL', 'Shin_L', [0,0,0], .07, 4], ['anL', 'Foot_L', [0,0,0], .06, 2], ['toL', 'Foot_L', [0,-.07,.19], .045, .8],
  ['knR', 'Shin_R', [0,0,0], .07, 4], ['anR', 'Foot_R', [0,0,0], .06, 2], ['toR', 'Foot_R', [0,-.07,.19], .045, .8]
];

export function createRagdoll(soldier, world) {
  const { root } = soldier, J = soldier.j;
  const byName = name => soldier.bones.find(b => b.name === name);
  const P = PARTS.map(([name, bone, off, r, m]) => ({ name, bone: byName(bone), off: V(...off), r, w: 1/m, p: V(), q: V(), rest: V() }));
  const I = Object.fromEntries(P.map((x, i) => [x.name, i]));
  const at = n => P[I[n]];

  // canonical layout from the bind pose (called before any rig has posed the bones)
  root.updateMatrixWorld(true);
  const rootInv = root.matrixWorld.clone().invert();
  for (const x of P) x.rest.copy(x.bone.localToWorld(x.off.clone())).applyMatrix4(rootInv);
  const restD = (a, b) => at(a).rest.distanceTo(at(b).rest);

  // ---------- constraints ----------
  const C = [];
  const rigid = (...n) => { for (let i = 0; i < n.length; i++) for (let j = i + 1; j < n.length; j++) { const L = restD(n[i], n[j]); C.push({ a: I[n[i]], b: I[n[j]], min: L, max: L }); } };
  const range = (a, b, lo, hi) => { const L = restD(a, b); C.push({ a: I[a], b: I[b], min: L*lo, max: L*hi }); };
  const atLeast = (a, b, d) => C.push({ a: I[a], b: I[b], min: d, max: Infinity });
  rigid('hipL', 'hipR', 'spine'); rigid('shL', 'shR', 'neck', 'spine'); rigid('neck', 'head');
  for (const s of ['L', 'R']) {
    const o = s === 'L' ? 'R' : 'L';
    rigid('sh' + s, 'el' + s); rigid('el' + s, 'ha' + s);
    rigid('hip' + s, 'kn' + s); rigid('kn' + s, 'an' + s); rigid('an' + s, 'to' + s);
    range('neck', 'hip' + s, .8, 1.03);                  // spine flexion and side bend
    range('sh' + s, 'hip' + s, .78, 1.05); range('sh' + s, 'hip' + o, .84, 1.08);   // and twist
    range('head', 'sh' + s, .8, 1.14);
    range('to' + s, 'kn' + s, .86, 1.06);                // ankle
    atLeast('ha' + s, 'sh' + s, .12); atLeast('ha' + s, 'spine', .12); atLeast('el' + s, 'spine', .14);
    atLeast('ha' + s, 'sh' + o, .16); atLeast('kn' + s, 'hip' + o, .16); atLeast('kn' + s, 'spine', .3);
    atLeast('an' + s, 'hip' + s, .26);                   // knee fully flexed
  }
  range('head', 'spine', .82, 1.04);
  atLeast('knL', 'knR', .13); atLeast('anL', 'anR', .1); atLeast('toL', 'toR', .08);

  // rest bases (root space) of the pelvis triangle and the chest quad
  const basis = (xa, xb, ya, yb, key = 'p') => {
    const x = at(xa)[key].clone().sub(at(xb)[key]).normalize();
    const y = (yb === 'mid' ? at('spine')[key].clone().sub(at('hipL')[key].clone().add(at('hipR')[key]).multiplyScalar(.5)) : at(ya)[key].clone().sub(at(yb)[key]));
    y.addScaledVector(x, -y.dot(x)).normalize();
    const z = x.clone().cross(y);
    return { x, y, z, q: new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, y, z)) };
  };
  const pelvisRest = basis('hipL', 'hipR', null, 'mid', 'rest').q.invert();
  const chestRest = basis('shL', 'shR', 'neck', 'spine', 'rest').q.invert();
  const restDir = (a, b) => at(b).rest.clone().sub(at(a).rest).normalize();
  const hipsOffset = J.hips.position.clone().sub(at('hipL').rest.clone().add(at('hipR').rest).multiplyScalar(.5));

  // rifle: butt, muzzle and rear sight form a rigid triangle
  const RP = [GUN.butt, GUN.muzzle, GUN.sight].map((g, i) => ({ g: g.clone(), r: [.035, .02, .03][i], w: 1, p: V(), q: V() }));
  const rifleRest = (() => { const z = RP[1].g.clone().sub(RP[0].g).normalize(), y = RP[2].g.clone().sub(RP[0].g); y.addScaledVector(z, -y.dot(z)).normalize(); const x = y.clone().cross(z); return new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, y, z)).invert(); })();
  const RC = [[0, 1], [1, 2], [0, 2]].map(([a, b]) => ({ a, b, L: RP[a].g.distanceTo(RP[b].g) }));

  const s = { active: false, t: 0, hPrev: 1/240, events: [], sleep: 0 };
  const ALL = [...P, ...RP];
  // muscle tone: every pair inside the trunk keeps its death-pose distance; arms and legs only get
  // a few weak braces, so they go limp first and flail as the body falls
  const TONE_UP = ['hipL', 'hipR', 'spine', 'shL', 'shR', 'neck', 'head'];
  const TONE_LEG = [['hipL', 'anL'], ['hipR', 'anR'], ['knL', 'spine'], ['knR', 'spine'], ['knL', 'hipR'], ['knR', 'hipL'], ['anL', 'spine'], ['anR', 'spine'],
    ['elL', 'spine'], ['elR', 'spine'], ['haL', 'shL'], ['haR', 'shR']];
  const tone = [];

  // ---------- start: particles from the current pose, velocities from the last two poses ----------
  function start(impulses = [], prevWorld = null) {
    root.updateMatrixWorld(true);
    for (const x of P) {
      x.bone.localToWorld(x.p.copy(x.off));
      x.q.copy(x.p);
    }
    if (prevWorld) for (const x of P) { const v = prevWorld[x.name]; if (v) x.q.copy(x.p).sub(v.clone().multiplyScalar(s.hPrev)); }
    // the pose the muscles try to keep
    tone.length = 0;
    for (let i = 0; i < TONE_UP.length; i++) for (let j = i + 1; j < TONE_UP.length; j++) tone.push({ a: I[TONE_UP[i]], b: I[TONE_UP[j]], leg: false });
    for (const [a, b] of TONE_LEG) tone.push({ a: I[a], b: I[b], leg: true });
    for (const c of tone) c.L = P[c.a].p.distanceTo(P[c.b].p);
    for (const im of impulses) kick(im.point, im.dir, im.speed, im.spread);
    // the rifle leaves the hands with the hands' velocity
    J.rifle.updateMatrixWorld(true);
    const hv = at('haR').p.clone().sub(at('haR').q).divideScalar(s.hPrev);
    for (const r of RP) { r.p.copy(r.g).applyMatrix4(J.rifle.matrixWorld); r.q.copy(r.p).addScaledVector(hv, -s.hPrev*.8); }
    s.active = true; s.t = 0; s.sleep = 0;
  }
  // a velocity change at a world point: the nearest particles take most of it
  function kick(point, dir, speed, spread = .25) {
    const d = P.map(x => x.p.distanceTo(point)), near = d.indexOf(Math.min(...d));
    for (let i = 0; i < P.length; i++) {
      const k = i === near ? 1 : Math.max(spread, Math.exp(-d[i]/.25)*.7);
      P[i].q.addScaledVector(dir, -speed*k*s.hPrev);
    }
  }

  const tmp = V(), tmp2 = V();
  // lateral (x), up (y) and forward (z) axes of a body frame, allocation-free for the solver loop
  const pAx = { x: V(), y: V(), z: V() }, cAx = { x: V(), y: V(), z: V() }, _mid = V();
  function axes(o, xa, xb, ya, yb) {
    o.x.subVectors(xa, xb).normalize(); o.y.subVectors(ya, yb); o.y.addScaledVector(o.x, -o.y.dot(o.x)).normalize(); o.z.crossVectors(o.x, o.y);
  }
  function solveDistance(c, stiff = 1) {
    const A = P[c.a], B = P[c.b]; tmp.subVectors(B.p, A.p);
    const L = tmp.length(); if (L < 1e-6) return;
    const target = c.L !== undefined ? c.L : L < c.min ? c.min : L > c.max ? c.max : -1; if (target < 0) return;
    const k = stiff*(L - target)/(L*(A.w + B.w));
    A.p.addScaledVector(tmp, k*A.w); B.p.addScaledVector(tmp, -k*B.w);
  }
  // Joint-limit projections move both sides by inverse mass, like the distance constraints, so they
  // never add momentum (a one-sided push here would spin the whole body on its own).
  function push(movers, counter, dir, amount) {
    const wa = movers.reduce((s, n) => s + 1/at(n).w, 0), wb = counter.reduce((s, n) => s + 1/at(n).w, 0);
    const ka = amount*wb/(wa + wb), kb = amount*wa/(wa + wb);
    for (const n of movers) at(n).p.addScaledVector(dir, ka);
    for (const n of counter) at(n).p.addScaledVector(dir, -kb);
  }
  // keep the middle joint of a limb on one side of its root–tip line (knees forward, elbows back)
  const _off = V(), _u = V();
  function hinge(r, m, t, axis, sign) {
    const R = at(r).p, M = at(m).p, T = at(t).p;
    const a = tmp.subVectors(T, R), aL = a.lengthSq(); if (aL < 1e-6) return;
    const bend = tmp2.copy(a).cross(axis); if (bend.lengthSq() < 1e-8) return; bend.normalize();
    const off = _off.subVectors(M, R); off.addScaledVector(a, -off.dot(a)/aL);
    const c = off.dot(bend)*sign;
    if (c < 0) push([m], [r, t], bend, -sign*c*.35);
  }
  // bend limits between two frames: push the tip along `fwd` until its tilt is back inside [lo, hi]
  function tilt(base, tip, fwd, lo, hi, movers, counter) {
    const u = _u.subVectors(at(tip).p, at(base).p), L = u.length(); if (L < 1e-6) return;
    const d = u.dot(fwd)/L, fix = d < lo ? lo - d : d > hi ? hi - d : 0;
    if (fix) push(movers, counter, fwd, fix*L*.3);
  }
  // ground every iteration (inelastic: pushing out never launches); the walk colliders, as low walls,
  // once per substep. Contacts are remembered for friction, with the normal speed they absorbed.
  function collide(x, h, walls) {
    const floor = world.groundAt(x.p.x, x.p.z, x.p.y) + x.r;
    if (x.p.y < floor) {
      const vy = (x.p.y - x.q.y)/h;
      if (vy < -1.6) s.events.push({ name: x.name, speed: -vy, pos: x.p.clone() });
      x.cn = Math.max(x.cn || 0, -vy); x.contact = true;
      x.p.y = floor; if (x.q.y < floor) x.q.y = floor;
    }
    if (walls && x.p.y < .7) for (const c of world.colliders) {
      const abx = c.bx - c.ax, abz = c.bz - c.az, L2 = abx*abx + abz*abz;
      const t = L2 ? THREE.MathUtils.clamp(((x.p.x - c.ax)*abx + (x.p.z - c.az)*abz)/L2, 0, 1) : 0;
      const qx = c.ax + abx*t, qz = c.az + abz*t, dx = x.p.x - qx, dz = x.p.z - qz, d = Math.hypot(dx, dz), r = c.r + x.r*.6;
      if (d < r && d > 1e-5) { x.p.x = qx + dx/d*r; x.p.z = qz + dz/d*r; x.contact = true; }
    }
  }
  // Coulomb friction: sliding slows by μ·(weight + the impact the contact absorbed), never reverses
  const MU = .8;
  function friction(x, h) {
    if (!x.contact) return;
    const vx = (x.p.x - x.q.x)/h, vz = (x.p.z - x.q.z)/h, vt = Math.hypot(vx, vz);
    const k = vt > 1e-6 ? Math.max(0, 1 - MU*(Math.max(0, x.cn) + G*h)/vt) : 0;
    x.q.x = x.p.x - vx*k*h; x.q.z = x.p.z - vz*k*h;
    x.contact = false; x.cn = 0;
  }

  function step(dt) {
    if (!s.active || dt <= 0) return;
    s.t += dt;
    const h = Math.min(dt, 1/30)/SUB;
    // muscle tone: the upper body keeps its shape for a moment, the legs give out almost at once
    const toneUp = .5*Math.exp(-s.t/.3), toneLeg = .25*Math.exp(-s.t/.1);
    let moving = 0;
    for (let k = 0; k < SUB; k++) {
      const scale = h/s.hPrev;
      for (const x of ALL) {
        const vx = (x.p.x - x.q.x)*scale*.998, vy = (x.p.y - x.q.y)*scale*.998, vz = (x.p.z - x.q.z)*scale*.998;
        x.q.copy(x.p); x.p.x += vx; x.p.y += vy - G*h*h; x.p.z += vz;
        moving = Math.max(moving, vx*vx + vy*vy + vz*vz);
      }
      s.hPrev = h;
      for (let it = 0; it < ITER; it++) {
        if (toneUp > .003) for (const c of tone) solveDistance(c, c.leg ? toneLeg : toneUp);
        for (const c of C) solveDistance(c);
        axes(pAx, at('hipL').p, at('hipR').p, at('spine').p, _mid.addVectors(at('hipL').p, at('hipR').p).multiplyScalar(.5));
        axes(cAx, at('shL').p, at('shR').p, at('neck').p, at('spine').p);
        hinge('hipL', 'knL', 'anL', pAx.x, 1); hinge('hipR', 'knR', 'anR', pAx.x, 1);
        hinge('shL', 'elL', 'haL', cAx.x, -1); hinge('shR', 'elR', 'haR', cAx.x, -1);
        tilt('spine', 'neck', pAx.z, -.38, .95, ['neck', 'shL', 'shR'], ['hipL', 'hipR']);   // no breaking backwards
        tilt('neck', 'head', cAx.z, -.55, .8, ['head'], ['shL', 'shR']);
        for (const c of RC) {
          const A = RP[c.a], B = RP[c.b]; tmp.subVectors(B.p, A.p); const L = tmp.length(); if (L < 1e-6) continue;
          const k = (L - c.L)/(2*L); A.p.addScaledVector(tmp, k); B.p.addScaledVector(tmp, -k);
        }
        const last = it === ITER - 1;
        for (const x of P) collide(x, h, last);
        for (const x of RP) collide(x, h, last);
      }
      // no joint may move faster than a body can (guards against constraint fights)
      const vmax = 9*h;
      for (const x of ALL) { friction(x, h); tmp.subVectors(x.p, x.q); const l = tmp.length(); if (l > vmax) x.q.addScaledVector(tmp, 1 - vmax/l); }
    }
    s.sleep = moving < (.03*h)**2 ? s.sleep + dt : 0;   // still for a while: the enemy can stop simulating
    pose();
  }

  // ---------- particles → bones ----------
  const qFrom = (a, b) => new THREE.Quaternion().setFromUnitVectors(a, b);
  const local = (q, v) => v.clone().applyQuaternion(q.clone().invert()).normalize();
  function limb(parentQ, bone, from, to) {
    const cur = at(to).p.clone().sub(at(from).p);
    const q = parentQ.clone().multiply(qFrom(restDir(from, to), local(parentQ, cur)));
    worldRotation(bone, q); return q;
  }
  function pose() {
    root.updateMatrixWorld(true);
    const pf = basis('hipL', 'hipR', null, 'mid'), cf = basis('shL', 'shR', 'neck', 'spine');
    const qHips = pf.q.clone().multiply(pelvisRest), qChest = cf.q.clone().multiply(chestRest);
    const mid = at('hipL').p.clone().add(at('hipR').p).multiplyScalar(.5);
    J.hips.position.copy(root.worldToLocal(mid.add(hipsOffset.clone().applyQuaternion(qHips))));
    worldRotation(J.hips, qHips);
    worldRotation(J.spine, qHips.clone().slerp(qChest, .5));
    worldRotation(J.chest, qChest);
    const headRel = qFrom(restDir('neck', 'head'), local(qChest, at('head').p.clone().sub(at('neck').p)));
    worldRotation(J.neck, qChest.clone().multiply(new THREE.Quaternion().slerp(headRel, .4)));
    worldRotation(J.head, qChest.clone().multiply(headRel));
    for (const sd of ['L', 'R']) {
      const arm = J.arms[sd], leg = J.legs[sd];
      const qU = limb(qChest, arm.upper, 'sh' + sd, 'el' + sd);
      const qF = limb(qU, arm.fore, 'el' + sd, 'ha' + sd);
      worldRotation(arm.hand, qF.clone().multiply(new THREE.Quaternion().setFromAxisAngle(V(1, 0, 0), .35)));   // limp wrist
      const qT = limb(qHips, leg.thigh, 'hip' + sd, 'kn' + sd);
      const qS = limb(qT, leg.shin, 'kn' + sd, 'an' + sd);
      limb(qS, leg.foot, 'an' + sd, 'to' + sd);
    }
    // rifle and its magazine
    const z = RP[1].p.clone().sub(RP[0].p).normalize(), y = RP[2].p.clone().sub(RP[0].p); y.addScaledVector(z, -y.dot(z)).normalize();
    const qR = new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(y.clone().cross(z), y, z)).multiply(rifleRest);
    const rootQi = root.getWorldQuaternion(new THREE.Quaternion()).invert();
    J.rifle.quaternion.copy(rootQi.clone().multiply(qR));
    J.rifle.position.copy(root.worldToLocal(RP[0].p.clone().sub(GUN.butt.clone().applyQuaternion(qR))));
    J.mag.quaternion.copy(J.rifle.quaternion); J.mag.scale.setScalar(1);
    J.mag.position.copy(J.rifle.position).add(GUN.magWell.clone().applyQuaternion(J.rifle.quaternion));
    root.updateMatrixWorld(true);
  }

  // world positions of every particle (to give the ragdoll the living body's velocity)
  function sample() { root.updateMatrixWorld(true); return Object.fromEntries(P.map(x => [x.name, x.bone.localToWorld(x.off.clone())])); }

  return { state: s, start, step, kick, sample, particles: P, rifle: RP, get active() { return s.active; }, stop() { s.active = false; } };
}
