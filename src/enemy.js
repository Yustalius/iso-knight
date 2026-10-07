import * as THREE from 'three';
import { createSoldier } from './soldier-model.js';
import { createRig } from './soldier-rig.js';
import { createRagdoll } from './ragdoll.js';

// An OPFOR soldier on the range: a target with hit points. He stands at his post with the rifle
// at the ready and turns to face the shooter; hits make him flinch and stagger (the same procedural
// rig as the player, driven by a hit spring and a shove velocity). When killed he becomes a
// ragdoll that takes the bullet's push, drops the rifle, and later is replaced by a fresh one.

const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
const wrapA = a => Math.atan2(Math.sin(a), Math.cos(a));
const dampA = (a, b, k, dt) => a + wrapA(b - a)*(1 - Math.exp(-k*dt));

const DAMAGE = { head: [100, 120], torso: [30, 42], arm: [14, 22], leg: [18, 26], shove: [6, 10] };
const KICK = { head: 3.6, torso: 2.8, arm: 2.2, leg: 2.6, shove: 3.2 };   // m/s given to the hit joint on the killing blow
const STAGGER = { head: .6, torso: 1.0, arm: .45, leg: .6, shove: 1.9 };
// hit volumes: [zone, bone, offset, bone, offset, radius]; equal ends make a sphere
const CAPS = [
  ['head', 'Head', [0,.1,.01], 'Head', [0,.1,.01], .135],
  ['torso', 'Hips', [0,.06,0], 'Neck', [0,-.03,0], .2],
  ['torso', 'Thigh_L', [0,.02,0], 'Thigh_R', [0,.02,0], .12],
  ['arm', 'UpperArm_L', [0,0,0], 'Forearm_L', [0,0,0], .075], ['arm', 'Forearm_L', [0,0,0], 'Hand_L', [0,-.08,0], .06],
  ['arm', 'UpperArm_R', [0,0,0], 'Forearm_R', [0,0,0], .075], ['arm', 'Forearm_R', [0,0,0], 'Hand_R', [0,-.08,0], .06],
  ['leg', 'Thigh_L', [0,0,0], 'Shin_L', [0,0,0], .095], ['leg', 'Shin_L', [0,0,0], 'Foot_L', [0,0,0], .075], ['leg', 'Foot_L', [0,-.04,0], 'Foot_L', [0,-.05,.17], .06],
  ['leg', 'Thigh_R', [0,0,0], 'Shin_R', [0,0,0], .095], ['leg', 'Shin_R', [0,0,0], 'Foot_R', [0,0,0], .075], ['leg', 'Foot_R', [0,-.04,0], 'Foot_R', [0,-.05,.17], .06]
];

// a pool of blood that spreads under a body: soft irregular disc
function poolMesh() {
  const c = document.createElement('canvas'); c.width = c.height = 128;
  const x = c.getContext('2d');
  for (let i = 0; i < 9; i++) {
    const a = i/9*Math.PI*2, r = 26 + (i % 3)*7, px = 64 + Math.cos(a)*14, py = 64 + Math.sin(a)*14;
    const g = x.createRadialGradient(px, py, 0, px, py, r);
    g.addColorStop(0, 'rgba(74,10,8,.95)'); g.addColorStop(.75, 'rgba(70,10,8,.85)'); g.addColorStop(1, 'rgba(70,10,8,0)');
    x.fillStyle = g; x.fillRect(0, 0, 128, 128);
  }
  const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace;
  const m = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), new THREE.MeshStandardMaterial({ map: t, transparent: true, depthWrite: false, roughness: .25, metalness: .1, polygonOffset: true, polygonOffsetFactor: -3, polygonOffsetUnits: -3 }));
  m.rotation.x = -Math.PI/2; m.visible = false; m.receiveShadow = true;
  return m;
}

export function createEnemy(scene, world, spawn, hooks) {
  const soldier = createSoldier({ camo: 'khaki', opfor: true });
  scene.add(soldier.root, soldier.sling);
  const rag = createRagdoll(soldier, world);      // reads the bind pose, so it comes before any posing
  const rig = createRig(soldier);
  const { root } = soldier;
  const bone = n => soldier.bones.find(b => b.name === n);
  const caps = CAPS.map(([zone, b0, o0, b1, o1, r]) => ({ zone, b0: bone(b0), o0: V(...o0), b1: bone(b1), o1: V(...o1), r, a: V(), b: V() }));
  const mats = [...soldier.skin.material, soldier.sling.material];
  const pool = poolMesh(); scene.add(pool);
  const st = { pos: V(), vel: V(), prevVel: V(), yaw: 0, yawT: 0, prevYaw: 0, hp: 100, dead: false, deadT: 0, fade: 1, flinch: null, prev: null, cur: null, dt: 1/60, poolT: 0 };

  function reset() {
    st.pos.set(spawn.x, 0, spawn.z); st.vel.set(0, 0, 0); st.prevVel.set(0, 0, 0);
    st.yaw = st.yawT = st.prevYaw = spawn.yaw; st.hp = 100; st.dead = false; st.deadT = 0; st.fade = 0; st.prev = st.cur = null;
    rag.stop(); pool.visible = false;
    root.position.copy(st.pos); root.rotation.y = st.yaw;
  }
  reset(); st.fade = 1; updateCaps();

  function updateCaps() {
    root.updateMatrixWorld(true);
    for (const c of caps) { c.b0.localToWorld(c.a.copy(c.o0)); c.b1.localToWorld(c.b.copy(c.o1)); }
  }
  // nearest entry of a ray into the capsules: closest approach between the ray and each axis
  const _w = V(), _ax = V();
  function test(o, d) {
    if (st.fade < .5) return null;
    let best = null;
    for (const c of caps) {
      _ax.subVectors(c.b, c.a); const L = _ax.length();
      let tc = 0, sc;
      if (L < 1e-5) sc = Math.max(0, _w.subVectors(c.a, o).dot(d));
      else {
        _ax.divideScalar(L); _w.subVectors(o, c.a);
        const b = d.dot(_ax), dd = d.dot(_w), e = _ax.dot(_w), den = 1 - b*b;
        tc = THREE.MathUtils.clamp(den > 1e-6 ? (e - b*dd)/den : e, 0, L);
        sc = Math.max(0, d.dot(V().copy(c.a).addScaledVector(_ax, tc).sub(o)));
      }
      const q = V().copy(c.a).addScaledVector(_ax, L < 1e-5 ? 0 : tc), pc = o.clone().addScaledVector(d, sc), dist = pc.distanceTo(q);
      if (dist > c.r) continue;
      const t = Math.max(0, sc - Math.sqrt(c.r*c.r - dist*dist));
      if (!best || t < best.t) {
        const point = o.clone().addScaledVector(d, t), normal = point.clone().sub(q);
        best = { t, point, normal: normal.lengthSq() > 1e-8 ? normal.normalize() : d.clone().negate(), zone: c.zone };
      }
    }
    return best;
  }

  function die(point, dir, zone) {
    st.dead = true; st.deadT = 0; st.poolT = 0;
    // joint velocities of the living body from the last two frames, then the bullet's push
    const vel = {};
    if (st.prev && st.cur) for (const k in st.cur) vel[k] = st.cur[k].clone().sub(st.prev[k]).divideScalar(st.dt);
    const push = V(dir.x, Math.max(dir.y, -.15), dir.z).normalize();
    const side = V(-push.z, 0, push.x).multiplyScalar(Math.random() < .5 ? -1 : 1);
    rag.start([
      { point, dir: push, speed: KICK[zone], spread: .3 },
      // a little off-axis shove at one shoulder, so the body twists as it goes
      { point: V(0, 1.42, 0).applyMatrix4(root.matrixWorld).addScaledVector(side, .2), dir: side, speed: .7 + Math.random()*.6, spread: 0 }
    ], vel);
    st.vel.set(0, 0, 0);
  }

  function hit(r, dir, power = 1) {
    const zone = r.zone || 'shove';
    if (st.dead) {   // shots and shoves still move a corpse
      if (rag.active) { rag.kick(r.point, V(dir.x, dir.y + .35, dir.z).normalize(), (zone === 'shove' ? 2.6 : 2.4)*power, .15); rag.state.sleep = 0; }
      hooks.blood && zone !== 'shove' && hooks.blood(r.point, dir, .5);
      return { dmg: 0, corpse: true, zone };
    }
    const [lo, hi] = DAMAGE[zone], dmg = Math.round(lo + Math.random()*(hi - lo));
    st.hp -= dmg;
    if (zone !== 'shove' && hooks.blood) hooks.blood(r.point, dir, zone === 'head' ? 1.6 : 1);
    if (st.hp <= 0) { die(r.point, dir, zone); return { dmg, killed: true, zone }; }
    const inv = root.matrixWorld.clone().invert();
    st.flinch = { dir: dir.clone().transformDirection(inv), at: r.point.clone().applyMatrix4(inv), power: zone === 'shove' ? 1.5 : 1 };
    st.vel.addScaledVector(V(dir.x, 0, dir.z).normalize(), STAGGER[zone]*power);
    return { dmg, killed: false, zone };
  }

  function update(dt) {
    if (dt <= 0) return;
    const ctx = hooks.context();
    if (!st.dead) {
      // turn to face the shooter when he is well off to the side; staggers die out
      const want = Math.atan2(ctx.player.x - st.pos.x, ctx.player.z - st.pos.z);
      if (Math.abs(wrapA(want - st.yawT)) > .35) st.yawT = want;
      st.prevYaw = st.yaw; st.yaw = dampA(st.yaw, st.yawT, 2.2, dt);
      st.prevVel.copy(st.vel);
      st.vel.multiplyScalar(Math.exp(-4.5*dt));
      st.pos.addScaledVector(st.vel, dt);
      root.position.copy(st.pos); root.rotation.y = st.yaw; root.updateMatrixWorld(true);
      const c = Math.cos(st.yaw), s = Math.sin(st.yaw), toLocal = v => V(v.x*c - v.z*s, 0, v.x*s + v.z*c);
      rig.update(dt, {
        velLocal: toLocal(st.vel), accLocal: toLocal(st.vel.clone().sub(st.prevVel).divideScalar(dt)), yawRate: wrapA(st.yaw - st.prevYaw)/dt,
        aim: false, aimLocal: null, crouch: false, shot: false, melee: null, newMelee: false, impact: false, reload: null,
        flinch: st.flinch, viewDir: ctx.viewDir
      });
      st.flinch = null; rig.state.events.length = 0;
      st.prev = st.cur; st.cur = rag.sample(); st.dt = dt;
      if (st.fade < 1) st.fade = Math.min(1, st.fade + dt/.6);
    } else {
      st.deadT += dt;
      if (rag.state.sleep < 1.5) rag.step(dt);
      rig.sling(dt, ctx.viewDir);
      for (const ev of rag.state.events.splice(0)) if (hooks.thump && ev.speed > 2) hooks.thump(ev.pos, ev.speed, ev.name || 'rifle');
      // blood spreads under the chest once the body has come to rest
      const sp = rag.particles.find(p => p.name === 'spine').p;
      if (st.deadT > .9) {
        if (!pool.visible) { pool.visible = true; pool.position.set(sp.x, .012, sp.z); }
        st.poolT += dt; pool.scale.setScalar(.15 + 1.05*(1 - Math.exp(-st.poolT/3.5)));
      }
      if (st.deadT > 9) { st.fade = Math.max(0, 1 - (st.deadT - 9)/.8); pool.material.opacity = st.fade; }
      if (st.deadT > 9.9) { reset(); pool.material.opacity = 1; }
    }
    for (const m of mats) { m.opacity = st.fade; m.transparent = st.fade < .999; m.depthWrite = st.fade > .5; }
    updateCaps();
  }

  return {
    type: 'enemy', material: 'flesh', group: root, meshes: [soldier.skin], x: spawn.x, z: spawn.z, decals: [],
    get dead() { return st.dead; }, get hp() { return st.hp; }, soldier, ragdoll: rag,
    center() { return bone('Chest').getWorldPosition(V()); },
    test, hit, update, reset
  };
}
