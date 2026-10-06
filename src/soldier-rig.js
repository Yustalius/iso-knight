import * as THREE from 'three';
import { SOLE, GUN } from './soldier-model.js';
import { clamp, lerp, smooth, damp, V, curve, Spring, worldRotation, solveLimb, footTarget as footTargetFor, gaitState, stepGait } from './rig-core.js';

// Procedural animation for the rifleman. The legs reuse the knight's speed-driven gait; the upper
// body is driven by the rifle: its pose is chosen (ready / port arms / shouldered / reload / shove),
// placed in chest space, then both hands are solved onto its grips with two-bone IK.

const _q = new THREE.Quaternion(), _q2 = new THREE.Quaternion();
const footTarget = (...a) => footTargetFor(SOLE, ...a);
const wrapA = a => Math.atan2(Math.sin(a), Math.cos(a));
const basisQ = (x, y, z) => new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, y, z));
// hand frames in rifle space: -Y runs wrist→knuckles, X is the palm normal (right hand) or its opposite (left)
function handFrame(fingers, palm, left) {
  const p = palm.clone().normalize(), f = fingers.clone().normalize();
  f.addScaledVector(p, -f.dot(p)).normalize();
  const x = left ? p.clone().negate() : p, y = f.clone().negate(), z = x.clone().cross(y).normalize();
  return basisQ(x, y, z);
}
// rifle orientation from a forward direction and an "up" hint (+Z forward, +Y top of the rifle)
function rifleQ(dir, up) {
  const z = dir.clone().normalize(), x = up.clone().cross(z).normalize(), y = z.clone().cross(x);
  return basisQ(x, y, z);
}

// Grips, in rifle space: wrist positions and hand frames.
const GRIP_R = { pos: V(-.032, -.054, -.09), q: handFrame(V(0, -.39, .92), V(1, 0, 0), false) };
const GRIP_L = { pos: V(.034, -.02, .15), q: handFrame(V(-.3, .25, .92), V(-.35, .94, 0), true) };
// support-hand frames for the reload: rifle-relative except 'pouch', which is body-relative
const LFRAME = {
  grip: { q: GRIP_L.q, rifle: true },
  mag: { q: handFrame(V(0, .2, 1), V(-.3, 1, 0), true), rifle: true },      // cupping a magazine's floorplate
  bolt: { q: handFrame(V(0, .55, .8), V(-1, 0, 0), true), rifle: true },    // palm on the receiver's left side
  pouch: { q: handFrame(V(0, -1, .25), V(0, -.2, -1), true), rifle: false } // fingers down into the belt pouch
};

// Rifle carry poses in chest space: butt position, muzzle direction, rifle-top hint.
const POSE = {
  ready: { butt: V(-.13, .055, .1), dir: V(.55, -.55, .63), up: V(-.2, 1, .2) },
  port: { butt: V(-.17, -.29, .14), dir: V(.585, .66, .48), up: V(-.25, -.1, 1) },
  reload: { butt: V(-.2, -.06, -.06), dir: V(.4, .06, .92), up: V(-.55, .84, 0) },
  pocket: V(-.102, .17, .065)   // shoulder pocket for the shouldered rifle
};

// Close-quarters shove with the rifle held crosswise, Project Zomboid's push. Keys are
// [t, buttX, buttY, buttZ, dirX, dirY, dirZ] in chest space; body: [t, hipsZ, lean, twist]; feet: [t, leftFootZ].
export const MELEE = {
  shove: {
    name: 'Толчок винтовкой', dur: .58, hit: .19, chain: .3, reach: 1.25, arc: 1.15,
    rifle: [[0,-.13,.055,.1,.55,-.55,.63],[.09,-.4,-.06,.13,1,.04,.12],[.19,-.42,-.03,.42,1,.02,-.04],[.27,-.41,-.03,.39,1,.02,-.02],[.58,-.13,.055,.1,.55,-.55,.63]],
    up: V(-.1, .35, 1),
    body: [[0,0,0,0],[.09,-.03,-.04,.12],[.19,.08,.17,-.05],[.3,.06,.12,-.04],[.58,0,0,0]],
    feet: [[0,0],[.08,0],[.2,.17],[.36,.17],[.58,0]]
  }
};

// Magazine change. Times in seconds; the support hand walks a chain of anchors that are
// re-evaluated every frame (rifle-relative or belt-relative), so it follows the moving rifle.
export const RELOAD = {
  name: 'Перезарядка', magOut: .17, grab: .48, seat: 1.02, bolt: 1.37,
  // [t, anchor, hand frame]
  full: { dur: 1.72, keys: [[0,'guard','grip'],[.17,'drop','grip'],[.36,'pouch','pouch'],[.5,'pouch','pouch'],[.76,'below','mag'],[.92,'below','mag'],[1.02,'well','mag'],[1.1,'slap','mag'],[1.18,'well','mag'],[1.31,'bolt','bolt'],[1.4,'boltIn','bolt'],[1.72,'guard','grip']] },
  tactical: { dur: 1.38, keys: [[0,'guard','grip'],[.17,'drop','grip'],[.36,'pouch','pouch'],[.5,'pouch','pouch'],[.76,'below','mag'],[.92,'below','mag'],[1.02,'well','mag'],[1.1,'slap','mag'],[1.18,'well','mag'],[1.38,'guard','grip']] }
};

export function createRig(soldier) {
  const { root, bones, sling, slingN } = soldier;
  const { hips, spine, chest, neck, head, arms, legs, straps, canteen, buttpack, rifle, mag } = soldier.j;
  const rest = bones.map(b => [b, b.position.clone(), b.quaternion.clone(), b.scale.clone()]);
  const rootQ = new THREE.Quaternion(), rootInv = new THREE.Matrix4(), chestM = new THREE.Matrix4(), hipsM = new THREE.Matrix4();
  const toW = v => root.localToWorld(v.clone());

  const s = {
    ...gaitState(), acc: V(), time: 0,
    aim: 0, crouch: 0, aimYaw: 0, aimPitch: 0, reloadW: 0, reloadT: 0, pocket: V(-.14, 1.36, 0),
    kickZ: new Spring(420, 30), kickP: new Spring(300, 17), kickY: new Spring(300, 20), shoulder: new Spring(200, 16),
    canteenX: new Spring(90, 5), canteenZ: new Spring(90, 5), strapX: new Spring(60, 3.2), strapZ: new Spring(60, 3.2),
    impact: new Spring(260, 18), headPrev: null, headVel: V(),
    events: [], out: { muzzle: V(), aimDir: V(0, 0, 1), barrel: V(0, 0, 1), eject: V(), ejectDir: V(), magVisible: true }
  };
  const IDLE_FEET = { L: [.125, .05, .16], R: [-.13, -.04, -.12] };
  const AIM_FEET = { L: [.12, .15, -.12], R: [-.15, -.12, -.62] };

  // ---------- sling: verlet strap pinned to both swivels ----------
  const slingPts = Array.from({ length: slingN }, () => ({ p: V(), q: V() }));
  let slingInit = false;
  function stepSling(dt, a, b, side) {
    if (!slingInit) { slingPts.forEach((n, i) => { n.p.lerpVectors(a, b, i/(slingN - 1)); n.p.y -= Math.sin(Math.PI*i/(slingN - 1))*.18; n.q.copy(n.p); }); slingInit = true; }
    const seg = .92/(slingN - 1);
    const sub = 3, h = dt/sub;
    for (let k = 0; k < sub; k++) {
      for (const n of slingPts) {
        const vx = (n.p.x - n.q.x)*.985, vy = (n.p.y - n.q.y)*.985, vz = (n.p.z - n.q.z)*.985;
        n.q.copy(n.p); n.p.x += vx; n.p.y += vy - 9.8*h*h; n.p.z += vz;
        if (n.p.y < .015) n.p.y = .015;
      }
      slingPts[0].p.copy(a); slingPts[slingN - 1].p.copy(b);
      for (let it = 0; it < 10; it++) for (let i = 0; i < slingN - 1; i++) {
        const p = slingPts[i].p, q = slingPts[i + 1].p, d = q.clone().sub(p), L = d.length() || 1e-6;
        if (L <= seg) continue;                                     // a strap only resists stretching
        const c = d.multiplyScalar((L - seg)/L), pinP = i === 0, pinQ = i + 1 === slingN - 1;
        if (pinP) q.sub(c); else if (pinQ) p.add(c); else { p.addScaledVector(c, .5); q.addScaledVector(c, -.5); }
      }
    }
    // the strap is drawn as a ribbon that keeps its flat side toward the camera
    const pos = sling.geometry.attributes.position, w = V();
    slingPts.forEach((n, i) => {
      const a = slingPts[Math.max(0, i - 1)].p, b = slingPts[Math.min(slingN - 1, i + 1)].p;
      w.subVectors(b, a).cross(side).normalize().multiplyScalar(.016);
      pos.setXYZ(i*2, n.p.x - w.x, n.p.y - w.y, n.p.z - w.z); pos.setXYZ(i*2 + 1, n.p.x + w.x, n.p.y + w.y, n.p.z + w.z);
    });
    pos.needsUpdate = true; sling.geometry.computeVertexNormals(); sling.geometry.computeBoundingSphere();
  }

  function chestPoint(v) { return v.clone().applyMatrix4(chestM); }
  function chestDir(v) { return v.clone().transformDirection(chestM); }
  function poseFromChest(p) {
    const butt = chestPoint(p.butt), q = rifleQ(chestDir(p.dir), chestDir(p.up));
    return { butt, q };
  }
  function blendPose(a, b, w) { return { butt: a.butt.clone().lerp(b.butt, w), q: a.q.clone().slerp(b.q, w) }; }

  // input: velLocal, accLocal, yawRate, aim (bool), aimLocal (V|null, character space), crouch (bool),
  // shot (bool), melee {def,t}|null, newMelee, impact, reload {t, def, empty}|null
  function update(dt, input) {
    s.time += dt;
    const vl = input.velLocal;
    s.acc.x = damp(s.acc.x, clamp(input.accLocal.x, -12, 12), 10, dt);
    s.acc.z = damp(s.acc.z, clamp(input.accLocal.z, -12, 12), 10, dt);
    const busy = input.reload || input.melee;
    s.aim = damp(s.aim, input.aim && !busy ? 1 : 0, input.aim && !busy ? 11 : 9, dt);
    s.crouch = damp(s.crouch, input.crouch ? 1 : 0, 7, dt);
    s.reloadW = damp(s.reloadW, input.reload ? 1 : 0, 12, dt);
    const cr = s.crouch, aw = smooth(s.aim);

    // standing still while aiming, the feet settle into a bladed stance
    const sw = aw*(1 - s.moveW), FEET = {};
    for (const k of ['L', 'R']) FEET[k] = IDLE_FEET[k].map((x, i) => lerp(x, AIM_FEET[k][i], sw));
    const { gait, v, u, fwd } = stepGait(s, dt, vl, input.yawRate, FEET, { crouch: cr, width: .115 + .03*cr });
    const mw = s.moveW, rw = s.runW*(1 - aw);

    // aim angles, measured from where the butt sat last frame; the rifle can swing ±60° before the body must turn
    let yawT = 0, pitchT = -.03;
    if (input.aimLocal) {
      const p = input.aimLocal, sx = p.x - s.pocket.x, sy = p.y - s.pocket.y, sz = p.z - s.pocket.z;
      yawT = Math.atan2(sx, sz); pitchT = clamp(Math.atan2(sy, Math.hypot(sx, sz)), -.85, .6);
    }
    s.aimYaw += wrapA(clamp(yawT, -1.05, 1.05) - s.aimYaw)*(1 - Math.exp(-22*dt));
    s.aimPitch = damp(s.aimPitch, pitchT, 22, dt);

    // ---------- recoil and impacts ----------
    if (input.shot) {   // spring units: metres for kickZ, radians for the pitch and yaw kicks
      s.kickZ.v -= 1 + Math.random()*.25; s.kickP.v += 1.7 + Math.random()*.7; s.kickY.v += (Math.random() - .5)*.7;
      s.shoulder.v += 1.5;
    }
    const kz = s.kickZ.step(0, dt), kp = s.kickP.step(0, dt), ky = s.kickY.step(0, dt), ksh = s.shoulder.step(0, dt);
    if (input.impact) s.impact.v += 8;
    const imp = s.impact.step(0, dt);

    // ---------- melee layer ----------
    let mel = null, mlw = 0;
    if (input.melee) {
      const { def, t } = input.melee;
      mlw = smooth(t/.06)*(1 - smooth((t - (def.dur - .12))/.12));
      const r = curve(def.rifle, t), b = curve(def.body, t), f = curve(def.feet, t);
      mel = { butt: V(r[0], r[1], r[2]), dir: V(r[3], r[4], r[5]), up: def.up, hz: b[0], lean: b[1], twist: b[2], fz: f[0] };
    }

    // ---------- body ----------
    const breath = Math.sin(s.time*2*Math.PI/3.1);
    const bob = lerp(.014, .032, s.runW)*Math.cos(4*Math.PI*(gait.R.p - .18*s.runW))*mw*(1 - .35*aw);
    let hx = .018*(gait.L.bell - gait.R.bell)*mw*(1 - s.runW*.6);
    let hy = lerp(.85 + .003*breath, lerp(.855, .83, s.runW), mw) - bob - .025*aw - .26*cr;
    let hz = -.04*cr;
    // a right-handed shooter blades the body: hips and chest turn right of the line of fire
    let pelvisYaw = -.12*(gait.L.z - gait.R.z)*mw*(1 - aw) + aw*(s.aimYaw*.45 - .42);
    let twist = -1.7*(-.12*(gait.L.z - gait.R.z)*mw)*(1 - aw) + aw*(s.aimYaw*.55 + .02) - .12*(1 - aw)*(1 - rw);
    let lean = lerp(.02 + .006*breath, lerp(.04, .2, s.runW), mw) + .15*aw + .2*cr + clamp(s.acc.z*.018, -.12, .14) - ksh*.25;
    const bank = clamp(-s.acc.x*.02, -.14, .14);
    let roll = .022*(gait.L.bell - gait.R.bell)*mw + bank;
    if (mel) { hz += mel.hz*mlw*(1 - mw*.8); lean += mel.lean*mlw; twist += mel.twist*mlw; }
    if (input.reload) lean += .04*s.reloadW;

    for (const [b, p, q, sc] of rest) { b.position.copy(p); b.quaternion.copy(q); b.scale.copy(sc); }
    hips.position.set(hx, hy, hz);
    hips.rotation.set(0, pelvisYaw, roll);
    spine.rotation.set(lean*.35, 0, -roll*.5);
    chest.rotation.set(lean*.65, twist, -roll*.3);
    root.updateMatrixWorld(true);
    root.getWorldQuaternion(rootQ);
    rootInv.copy(root.matrixWorld).invert();
    chestM.multiplyMatrices(rootInv, chest.matrixWorld);
    hipsM.multiplyMatrices(rootInv, hips.matrixWorld);

    // ---------- legs ----------
    for (const side of ['L', 'R']) {
      const g = gait[side], id = FEET[side];
      let x = lerp(id[0], g.x, mw), z = lerp(id[1], g.z, mw);
      let lift = g.lift*mw, pitch = g.pitch*mw;
      x += (side === 'L' ? .035 : -.035)*cr*(1 - mw);
      if (mel && side === 'L') { const w = mlw*(1 - mw); z += mel.fz*w; lift += Math.max(0, Math.sin(Math.PI*clamp(input.melee.t/.22, 0, 1)))*.04*w; }
      if (side === 'R' && cr > .01) z -= .06*cr*(1 - mw);
      const ft = footTarget(x, z, pitch, lift, s.footYaw[side]);
      const leg = legs[side];
      solveLimb(leg.thigh, leg.shin, leg.foot, toW(ft.pos), toW(V(side === 'R' ? -.15 : .15, .5, 1.2).add(V(0, 0, .4*cr))));
      worldRotation(leg.foot, _q2.copy(rootQ).multiply(ft.q));
      if (g.strike) s.events.push({ kind: 'step', side, speed: v, dir: V(-u.x, 0, -u.z), pos: leg.foot.getWorldPosition(V()).setY(.04) });
    }

    // ---------- rifle ----------
    const ready = blendPose(poseFromChest(POSE.ready), poseFromChest(POSE.port), smooth(rw*1.2));
    // swing and breathing sway of the carried rifle
    ready.q.multiply(_q.setFromEuler(new THREE.Euler(.03*breath*(1 - mw) + .05*bob*20, .06*Math.sin(s.phase*Math.PI*2)*mw, 0)));
    const aimDir = V(Math.sin(s.aimYaw)*Math.cos(s.aimPitch), Math.sin(s.aimPitch), Math.cos(s.aimYaw)*Math.cos(s.aimPitch));
    const aimPose = { butt: chestPoint(POSE.pocket), q: rifleQ(aimDir, V(0, 1, 0)) };
    s.pocket.copy(aimPose.butt).add(V(0, .04, 0).applyQuaternion(aimPose.q));   // the bore line at the butt
    let pose = blendPose(ready, aimPose, aw);
    if (input.reload) pose = blendPose(pose, poseFromChest(POSE.reload), smooth(s.reloadW));
    if (mel) pose = blendPose(pose, poseFromChest({ butt: mel.butt, dir: mel.dir, up: mel.up }), mlw);
    s.out.aimDir.copy(V(0, 0, 1).applyQuaternion(pose.q)).transformDirection(root.matrixWorld);
    // recoil pivots about the butt: muzzle climb, a little yaw, and the rifle driving back into the shoulder
    const qk = pose.q.clone().multiply(_q.setFromEuler(new THREE.Euler(-kp, ky, 0)));
    const butt = pose.butt.clone().add(V(0, 0, kz - imp*.04).applyQuaternion(pose.q));
    rifle.quaternion.copy(qk);
    rifle.position.copy(butt).sub(GUN.butt.clone().applyQuaternion(qk));
    rifle.updateMatrixWorld(true);
    const rp = v => v.clone().applyMatrix4(rifle.matrix);        // rifle space → character space

    // ---------- hands ----------
    // right hand never leaves the pistol grip
    {
      const arm = arms.R, target = toW(rp(GRIP_R.pos));
      const pole = toW(V(-.75, lerp(.55, 1.25, aw), -.25));
      solveLimb(arm.upper, arm.fore, arm.hand, target, pole);
      worldRotation(arm.hand, _q2.copy(rootQ).multiply(qk).multiply(GRIP_R.q));
    }
    // left hand: the handguard, or the reload path through the belt pouch and the magazine well
    let magState = 'rifle', magHand = 0;
    {
      const arm = arms.L;
      let target = rp(GRIP_L.pos), hq = qk.clone().multiply(GRIP_L.q);
      if (input.reload) {
        const { t, def, empty } = input.reload, seq = empty ? def.full : def.tactical;
        const A = {
          guard: rp(GRIP_L.pos),
          drop: rp(V(.07, -.12, .12)),
          pouch: V(.092, .1, .15).applyMatrix4(hipsM),
          below: rp(V(.012, -.3, .095)),
          well: rp(V(.012, -.235, .095)),
          slap: rp(V(.012, -.205, .095)),
          bolt: rp(V(.085, -.01, -.03)),
          boltIn: rp(V(.05, -.01, -.03))
        };
        const keys = seq.keys.map(([k, n]) => [k, A[n].x, A[n].y, A[n].z]);
        const [x, y, z] = curve(keys, t);
        const w = smooth(t/.06)*(1 - smooth((t - (seq.dur - .1))/.1));
        target = target.lerp(V(x, y, z), w);
        const holding = t > def.grab && t < def.seat + .02;
        // hand orientation eases between the frames of the surrounding keys
        let i = 0; while (i < seq.keys.length - 2 && t > seq.keys[i + 1][0]) i++;
        const [ta, , fa] = seq.keys[i], [tb, , fb] = seq.keys[i + 1];
        const frame = f => LFRAME[f].rifle ? qk.clone().multiply(LFRAME[f].q) : LFRAME[f].q.clone();
        hq = frame(fa).slerp(frame(fb), smooth((t - ta)/(tb - ta)));
        // the rifle jolts when the magazine is slapped home and when the bolt slams forward
        if (t < s.reloadT) s.reloadT = 0;   // a new reload started
        for (const ev of [def.seat + .06, empty ? def.bolt : -1]) if (s.reloadT < ev && t >= ev) { s.kickP.v += .9; s.kickZ.v += .25; }
        s.reloadT = t;
        magState = t < def.magOut ? 'rifle' : holding ? 'hand' : t >= def.seat ? 'rifle' : 'none';
        magHand = holding ? 1 : 0;
      }
      const pole = toW(V(.55, lerp(.35, .7, aw), .2));
      solveLimb(arm.upper, arm.fore, arm.hand, toW(target), pole);
      worldRotation(arm.hand, _q2.copy(rootQ).multiply(hq));
    }
    // magazine: seated, in the support hand, or gone (dropped)
    if (magState === 'rifle') {
      mag.position.copy(rp(GUN.magWell)); mag.quaternion.copy(qk);
    } else if (magState === 'hand') {
      // held by the floorplate: the same hand→magazine offset as when it is seated from the 'well' anchor
      const local = arms.L.hand.getWorldPosition(V()).applyMatrix4(rootInv);
      mag.position.copy(local).add(V(-.012, .203, -.045).applyQuaternion(qk));
      mag.quaternion.copy(qk);
    } else { mag.position.set(0, -5, 0); mag.scale.setScalar(.001); }
    s.out.magVisible = magState !== 'none';

    // ---------- head: look along the line of fire, cheek welded to the stock ----------
    {
      neck.position.z += .025*aw;
      neck.rotation.set(.4*aw + .1*cr, -(pelvisYaw + twist)*.55*(1 - aw), 0);
      neck.updateMatrixWorld(true);
      const lookYaw = s.aimYaw*aw + (input.reload ? .35*s.reloadW : 0);
      const lookPitch = s.aimPitch*aw + (input.reload ? -.55*s.reloadW : -.04) - .3*aw - .08*lean;
      const roll = .42*aw;
      const want = _q2.copy(rootQ).multiply(_q.setFromEuler(new THREE.Euler(-lookPitch, lookYaw, roll, 'YXZ')));
      const cur = head.getWorldQuaternion(new THREE.Quaternion());
      worldRotation(head, cur.slerp(want, clamp(aw + s.reloadW*.6 + .35, 0, 1)));
    }

    // ---------- secondary motion: canteen, butt pack, chin straps, sling ----------
    {
      const hp = head.getWorldPosition(V());
      if (s.headPrev && dt > 0) {
        const hv = hp.clone().sub(s.headPrev).divideScalar(dt);
        const ha = hv.clone().sub(s.headVel).divideScalar(dt).applyQuaternion(_q.copy(rootQ).invert());
        s.headVel.copy(hv); ha.clampScalar(-40, 40);
        s.strapX.step(0, dt, ha.z*1.6 - input.yawRate*0); s.strapZ.step(0, dt, -ha.x*1.6 + input.yawRate*2);
        s.canteenX.step(0, dt, ha.z*1.1 + ha.y*.6); s.canteenZ.step(0, dt, -ha.x*1.1);
      }
      s.headPrev = hp;
      for (const side of ['L', 'R']) straps[side].rotation.set(clamp(s.strapX.x, -.9, .9) + .1, 0, clamp(s.strapZ.x, -.7, .7));
      const thighL = legs.L.thigh.quaternion, sw = 2*Math.atan2(thighL.x, thighL.w);
      canteen.rotation.set(clamp(s.canteenX.x, -.6, .6) + Math.max(0, -sw)*.3, .95, clamp(s.canteenZ.x, -.5, .5));
      buttpack.rotation.set(clamp(s.canteenX.x, -.5, .5)*.4 - lean*.2, 0, 0);
    }
    root.updateMatrixWorld(true);

    // ---------- outputs for the gameplay layer ----------
    const rw2 = rifle.matrixWorld;
    s.out.muzzle.copy(GUN.muzzle).applyMatrix4(rw2);
    s.out.barrel.set(0, 0, 1).transformDirection(rw2);
    s.out.eject.copy(GUN.eject).applyMatrix4(rw2);
    s.out.ejectDir.copy(GUN.ejectDir).transformDirection(rw2);
    s.out.rifleQ = rifle.getWorldQuaternion(new THREE.Quaternion());
    s.out.magMatrix = mag.matrixWorld.clone();
    stepSling(dt, GUN.swivelF.clone().applyMatrix4(rw2), GUN.swivelR.clone().applyMatrix4(rw2), input.viewDir || V(0, -.5, -.87));
  }

  return { update, state: s };
}
