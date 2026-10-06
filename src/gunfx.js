import * as THREE from 'three';
import { makeRandom } from './textures.js';
import { magazineMesh } from './soldier-model.js';

// Everything a shot leaves behind: muzzle flash and light, smoke, the bullet with its tracer,
// spent brass and dropped magazines as small rigid bodies, bullet holes, and impact debris
// that depends on what was hit. Pixel-art textures are 64×64 like the rest of the scene.

const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
const _q = new THREE.Quaternion(), _v = V(), _m = new THREE.Matrix4();

function spriteTex(draw) {
  const c = document.createElement('canvas'); c.width = c.height = 64;
  draw(c.getContext('2d'));
  const t = new THREE.CanvasTexture(c); t.magFilter = THREE.NearestFilter; t.minFilter = THREE.NearestFilter; t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

export function createFx(scene, world, soldier) {
  const R = Math.random;

  // ---------- particles (one pool, geometry and material swapped per kind) ----------
  const geoCube = new THREE.BoxGeometry(.03, .03, .03), geoSliver = new THREE.BoxGeometry(.012, .06, .012), geoPuff = new THREE.IcosahedronGeometry(.06, 0), geoFlake = new THREE.BoxGeometry(.05, .004, .04);
  const mats = {
    spark: new THREE.MeshBasicMaterial({ color: 0xffe0a0 }),
    smoke: new THREE.MeshStandardMaterial({ color: 0xb9b8ae, roughness: 1, transparent: true, opacity: .32, depthWrite: false }),
    dust: new THREE.MeshStandardMaterial({ color: 0x8c7a5a, roughness: 1, transparent: true, opacity: .55, depthWrite: false }),
    grey: new THREE.MeshStandardMaterial({ color: 0xa09d92, roughness: 1, transparent: true, opacity: .5, depthWrite: false }),
    clod: new THREE.MeshStandardMaterial({ color: 0x5d4a33, roughness: 1, flatShading: true }),
    wood: new THREE.MeshStandardMaterial({ color: 0xbfa477, roughness: 1, flatShading: true }),
    chip: new THREE.MeshStandardMaterial({ color: 0x9c998e, roughness: 1, flatShading: true }),
    leaf: new THREE.MeshStandardMaterial({ color: 0x5d683b, roughness: 1, flatShading: true, side: THREE.DoubleSide }),
    paint: new THREE.MeshStandardMaterial({ color: 0x4f5b31, roughness: 1, flatShading: true }),
    sand: new THREE.MeshStandardMaterial({ color: 0xa39470, roughness: 1, flatShading: true })
  };
  const KIND = {
    spark: { geo: geoCube, mat: 'spark', life: [.12, .3], drag: 1, g: 9.8, spin: 0, grow: 0, sc: .6 },
    smoke: { geo: geoPuff, mat: 'smoke', life: [.7, 1.3], drag: 3.2, g: -.5, spin: 1, grow: 3.2, sc: .5 },
    dust: { geo: geoPuff, mat: 'dust', life: [.45, .8], drag: 4, g: -.3, spin: 1, grow: 2.6, sc: .6 },
    cdust: { geo: geoPuff, mat: 'grey', life: [.4, .7], drag: 4, g: -.3, spin: 1, grow: 2.2, sc: .5 },
    clod: { geo: geoCube, mat: 'clod', life: [.5, .9], drag: 1, g: 9.8, spin: 14, grow: 0, sc: 1 },
    splinter: { geo: geoSliver, mat: 'wood', life: [.6, 1], drag: 2, g: 8, spin: 22, grow: 0, sc: 1 },
    paint: { geo: geoFlake, mat: 'paint', life: [.6, 1.1], drag: 3, g: 5, spin: 16, grow: 0, sc: .6 },
    chip: { geo: geoCube, mat: 'chip', life: [.4, .8], drag: 1, g: 9.8, spin: 14, grow: 0, sc: .7 },
    leaf: { geo: geoFlake, mat: 'leaf', life: [1.2, 2], drag: 5, g: 1.6, spin: 6, grow: 0, sc: 1 },
    fiber: { geo: geoCube, mat: 'sand', life: [.4, .7], drag: 2, g: 6, spin: 10, grow: 0, sc: .5 }
  };
  const parts = [];
  for (let i = 0; i < 360; i++) { const m = new THREE.Mesh(geoCube, mats.spark); m.visible = false; scene.add(m); parts.push({ m, v: V(), w: V(), life: 0, max: 1, k: KIND.spark }); }
  let pi = 0;
  function emit(kind, p, dir, n, speed = 2, spread = 1) {
    const k = KIND[kind];
    for (let i = 0; i < n; i++) {
      const q = parts[pi++ % parts.length], m = q.m;
      q.k = k; m.geometry = k.geo; m.material = mats[k.mat]; m.visible = true;
      m.position.copy(p); m.rotation.set(R()*6, R()*6, R()*6); m.scale.setScalar(k.sc);
      q.life = q.max = k.life[0] + R()*(k.life[1] - k.life[0]);
      q.v.set(dir.x*speed + (R() - .5)*spread*2, dir.y*speed + (R() - .2)*spread*1.5, dir.z*speed + (R() - .5)*spread*2);
      q.w.set((R() - .5)*k.spin, (R() - .5)*k.spin, (R() - .5)*k.spin);
    }
  }
  function updateParts(dt) {
    for (const q of parts) {
      if (!q.m.visible) continue;
      q.life -= dt; if (q.life <= 0) { q.m.visible = false; continue; }
      const k = q.k, m = q.m;
      q.v.y -= k.g*dt; q.v.multiplyScalar(Math.exp(-k.drag*dt));
      m.position.addScaledVector(q.v, dt);
      m.rotation.x += q.w.x*dt; m.rotation.y += q.w.y*dt; m.rotation.z += q.w.z*dt;
      const floor = world.groundAt(m.position.x, m.position.z, m.position.y) + .01;
      if (m.position.y < floor) { m.position.y = floor; q.v.y *= -.25; q.v.x *= .5; q.v.z *= .5; q.w.multiplyScalar(.4); }
      const f = q.life/q.max;
      if (k.grow) m.scale.setScalar(k.sc*(1 + (1 - f)*k.grow)); else if (f < .3) m.scale.setScalar(k.sc*f/.3);
    }
  }

  // ---------- muzzle flash: crossed star sprites + a short-lived light ----------
  const flashTex = spriteTex(ctx => {
    const rnd = makeRandom(7);
    for (let i = 0; i < 9; i++) {
      const a = i/9*Math.PI*2 + rnd()*.3, L = 14 + rnd()*16;
      for (let r = 2; r < L; r++) { const w = Math.max(1, Math.round((1 - r/L)*4)); ctx.fillStyle = r < 7 ? '#fff6d0' : r < 14 ? '#ffc858' : '#e0782a'; ctx.fillRect(32 + Math.cos(a)*r - w/2, 32 + Math.sin(a)*r - w/2, w, w); }
    }
    ctx.fillStyle = '#fffbe8'; ctx.fillRect(27, 27, 10, 10);
  });
  const coneTex = spriteTex(ctx => {
    for (let x = 0; x < 64; x++) { const h = Math.round((1 - x/64)*13 + 2); ctx.fillStyle = x < 16 ? '#fff3c8' : x < 36 ? '#ffbe50' : '#d9702a'; ctx.fillRect(x, 32 - h, 1, h*2); }
  });
  const flashMat = new THREE.MeshBasicMaterial({ map: flashTex, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide, toneMapped: false });
  const coneMat = flashMat.clone(); coneMat.map = coneTex;
  const flash = new THREE.Group(); scene.add(flash); flash.visible = false;
  const star = new THREE.Mesh(new THREE.PlaneGeometry(.2, .2), flashMat); flash.add(star);
  for (const r of [0, Math.PI/2]) { const c = new THREE.Mesh(new THREE.PlaneGeometry(.32, .16).translate(.16, 0, 0), coneMat); c.rotation.set(r, -Math.PI/2, 0, 'YXZ'); flash.add(c); }
  const light = new THREE.PointLight(0xffb060, 0, 6, 2); scene.add(light);
  let flashT = 0;
  function muzzleFlash(p, dir) {
    flash.position.copy(p); flash.quaternion.setFromUnitVectors(V(0, 0, 1), dir);
    star.rotation.z = R()*6; const s = .7 + R()*.6; flash.scale.set(s, s, .8 + R()*.5);
    flash.children[1].rotation.z = flash.children[2].rotation.z = 0;
    flash.visible = true; flashT = .04;
    light.position.copy(p).addScaledVector(dir, .1); light.intensity = 14;
    emit('smoke', p.clone().addScaledVector(dir, .05), dir, 3, .9, .25);
  }

  // ---------- bullets and tracers ----------
  const tracerMat = new THREE.MeshBasicMaterial({ color: 0xffe7a6, transparent: true, opacity: .85, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false });
  const tracerGeo = new THREE.BoxGeometry(.012, .012, 1).translate(0, 0, -.5);
  const bullets = [];
  for (let i = 0; i < 24; i++) { const m = new THREE.Mesh(tracerGeo, tracerMat.clone()); m.visible = false; scene.add(m); bullets.push({ m, live: false }); }
  let bi = 0;
  const SPEED = 420;   // m/s on screen: one or two frames across the range, readable as a streak
  function fireBullet(o, d, onHit) {
    const b = bullets[bi++ % bullets.length];
    Object.assign(b, { live: true, p: o.clone(), d: d.clone(), from: o.clone(), dist: 0, max: 120, onHit, fade: 0 });
    b.m.visible = true; b.m.material.opacity = .85;
  }
  function updateBullets(dt) {
    for (const b of bullets) {
      if (!b.m.visible) continue;
      if (b.live) {
        const step = SPEED*dt, hit = world.trace(b.p, b.d, step);
        if (hit) { b.p.copy(hit.point); b.live = false; b.fade = .06; b.onHit(hit, b.d); }
        else { b.p.addScaledVector(b.d, step); b.dist += step; if (b.dist > b.max) { b.live = false; b.fade = .05; } }
      } else { b.fade -= dt; b.m.material.opacity = Math.max(0, b.fade/.06)*.85; if (b.fade <= 0) { b.m.visible = false; continue; } }
      const len = Math.min(2.2, b.p.distanceTo(b.from));
      b.m.position.copy(b.p); b.m.quaternion.setFromUnitVectors(V(0, 0, 1), b.d); b.m.scale.set(1, 1, Math.max(.01, len));
    }
  }

  // ---------- spent brass and dropped magazines: tiny rigid bodies ----------
  const brassMat = new THREE.MeshStandardMaterial({ color: 0xc9a14a, roughness: .35, metalness: .75, flatShading: true });
  const caseGeo = new THREE.CylinderGeometry(.009, .009, .066, 6);   // ~1.4× real 5.56 brass so it reads at game zoom
  const CASES = 140, cases = new THREE.InstancedMesh(caseGeo, brassMat, CASES);
  cases.count = 0; cases.frustumCulled = false; scene.add(cases);
  const brass = [];
  let ci = 0;
  function eject(p, dir, carrierVel) {
    const i = ci++ % CASES;
    const v = dir.clone().multiplyScalar(1.5 + R()*.7); v.y += .9 + R()*.5; v.add(carrierVel);
    const ax = V(-dir.z, 0, dir.x); if (ax.lengthSq() < 1e-6) ax.set(1, 0, 0);       // casing lies along the barrel
    const q = new THREE.Quaternion().setFromUnitVectors(V(0, 1, 0), ax.normalize());
    brass[i] = { p: p.clone(), v, q, w: V((R() - .5)*50, (R() - .5)*30, (R() - .5)*50), awake: true, bounces: 0, rest: 0 };
    cases.count = Math.min(CASES, Math.max(cases.count, i + 1));
  }
  const magRoot = new THREE.Group(); scene.add(magRoot);
  const mags = [];
  function dropMag(matrix, vel) {
    const g = magazineMesh(soldier.materials); magRoot.add(g);
    matrix.decompose(g.position, g.quaternion, _v);
    mags.push({ g, v: vel.clone().add(V(0, -.6, 0)), w: V((R() - .5)*6, (R() - .5)*3, (R() - .5)*6), awake: true });
    if (mags.length > 8) { const o = mags.shift(); magRoot.remove(o.g); }
  }
  let sfxHook = null;
  // shared integrator: gravity, spin, bounce on the floor with friction, settle flat
  function body(b, dt, half, restitution, onBounce) {
    if (!b.awake) return;
    b.v.y -= 9.8*dt; b.p.addScaledVector(b.v, dt);
    const wl = b.w.length(); if (wl > 1e-4) b.q.premultiply(_q.setFromAxisAngle(_v.copy(b.w).divideScalar(wl), wl*dt));
    const floor = world.groundAt(b.p.x, b.p.z, b.p.y);
    if (b.p.y < floor + half) {
      b.p.y = floor + half;
      if (b.v.y < -.4) { onBounce && onBounce(-b.v.y); b.v.y *= -restitution; } else b.v.y = 0;
      b.v.x *= .6; b.v.z *= .6; b.w.multiplyScalar(.55);
      const ax = V(0, 1, 0).applyQuaternion(b.q), flat = V(ax.x, 0, ax.z);
      if (flat.lengthSq() < 1e-4) flat.set(1, 0, 0);
      b.q.premultiply(_q.setFromUnitVectors(ax, ax.clone().lerp(flat.normalize(), .25).normalize()));
      if (b.v.lengthSq() < .01) { b.rest += dt; if (b.rest > .25) b.awake = false; }
    }
  }
  function updateBodies(dt) {
    for (let i = 0; i < brass.length; i++) {
      const b = brass[i]; if (!b) continue;
      if (b.awake) {
        body(b, dt, .009, .38, s => { if (b.bounces++ < 2 && sfxHook) sfxHook('tink', b.p, Math.min(1, s/3)); });
        _m.compose(b.p, b.q, _v.set(1, 1, 1)); cases.setMatrixAt(i, _m); cases.instanceMatrix.needsUpdate = true;
      }
    }
    for (const mg of mags) {
      if (!mg.awake) continue;
      const b = { p: mg.g.position, v: mg.v, q: mg.g.quaternion, w: mg.w, awake: true, rest: mg.rest || 0 };
      body(b, dt, .016, .25, s => sfxHook && sfxHook('mag', b.p, Math.min(1, s/3)));
      mg.awake = b.awake; mg.rest = b.rest;
    }
  }

  // ---------- bullet holes ----------
  const holeTex = spriteTex(ctx => {
    ctx.fillStyle = 'rgba(205,190,150,.95)';
    for (let i = 0; i < 26; i++) { const a = i/26*Math.PI*2, r = 18 + (i % 3)*5; ctx.fillRect(32 + Math.cos(a)*r - 3, 32 + Math.sin(a)*r - 3, 6, 6); }
    ctx.beginPath(); ctx.arc(32, 32, 19, 0, Math.PI*2); ctx.fill();
    ctx.fillStyle = '#16140f'; ctx.beginPath(); ctx.arc(32, 32, 11, 0, Math.PI*2); ctx.fill();
  });
  const pitTex = spriteTex(ctx => {
    ctx.fillStyle = 'rgba(30,25,18,.55)'; ctx.beginPath(); ctx.arc(32, 32, 26, 0, Math.PI*2); ctx.fill();
    ctx.fillStyle = 'rgba(14,12,9,.9)'; ctx.beginPath(); ctx.arc(32, 32, 13, 0, Math.PI*2); ctx.fill();
  });
  const splatTex = spriteTex(ctx => {
    const rnd = makeRandom(11); ctx.fillStyle = '#5a5c58';
    for (let i = 0; i < 12; i++) { const a = rnd()*Math.PI*2, L = 10 + rnd()*20; for (let r = 0; r < L; r += 2) ctx.fillRect(32 + Math.cos(a)*r - 2, 32 + Math.sin(a)*r - 2, 4, 4); }
    ctx.fillStyle = '#3d3f3c'; ctx.beginPath(); ctx.arc(32, 32, 9, 0, Math.PI*2); ctx.fill();
  });
  const dmat = tex => new THREE.MeshStandardMaterial({ map: tex, transparent: true, alphaTest: .4, depthWrite: false, roughness: 1, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2 });
  const DEC = { hole: dmat(holeTex), pit: dmat(pitTex), splat: dmat(splatTex) };
  const decalGeo = new THREE.PlaneGeometry(1, 1);
  const STATIC_N = 260, statics = {};
  for (const k in DEC) { const im = new THREE.InstancedMesh(decalGeo, DEC[k], STATIC_N); im.count = 0; im.frustumCulled = false; im.receiveShadow = true; scene.add(im); statics[k] = { im, i: 0 }; }
  function decal(hit) {
    const kind = hit.kind, tex = kind === 'steel' || kind === 'metal' ? 'splat' : kind === 'wood' || kind === 'canvas' ? 'hole' : 'pit';
    const size = tex === 'splat' ? .07 : tex === 'hole' ? .035 : kind === 'dirt' || kind === 'gravel' ? .07 : .045;
    if (hit.target) {
      if (!hit.local) return;
      const g = hit.target.group, m = new THREE.Mesh(decalGeo, DEC[tex]);
      m.position.copy(hit.local); if (hit.local.z < 0) m.rotation.y = Math.PI;
      m.scale.setScalar(size); m.rotation.z = R()*6; g.add(m);
      const list = hit.target.decals; list.push(m); if (list.length > 40) g.remove(list.shift());
      return;
    }
    const s = statics[tex], i = s.i++ % STATIC_N;
    _q.setFromUnitVectors(V(0, 0, 1), hit.normal); _q.multiply(new THREE.Quaternion().setFromAxisAngle(V(0, 0, 1), R()*6));
    _m.compose(hit.point.clone().addScaledVector(hit.normal, .004), _q, _v.setScalar(size*(.8 + R()*.4)));
    s.im.setMatrixAt(i, _m); s.im.instanceMatrix.needsUpdate = true; s.im.count = Math.min(STATIC_N, Math.max(s.im.count, i + 1));
  }

  // ---------- impact debris by material ----------
  function impact(hit, dir) {
    const p = hit.point, n = hit.normal, refl = dir.clone().reflect(n).multiplyScalar(.5).add(n).normalize();
    switch (hit.kind) {
      case 'steel': case 'metal': case 'tin': emit('spark', p, refl, 7, 2.6, 1.2); emit('cdust', p, n, 1, .4, .2); break;
      case 'wood': case 'canvas': emit('splinter', p, refl, 6, 1.8, 1); emit('dust', p, n, 1, .3, .2); if (hit.target) emit('paint', p, refl, 3, 1.4, .8); break;
      case 'concrete': case 'asphalt': emit('chip', p, refl, 6, 2.2, 1); emit('cdust', p, n, 3, .5, .3); emit('spark', p, refl, 1, 2, 1); break;
      case 'sand': emit('fiber', p, refl, 4, 1.4, .8); emit('dust', p, n, 3, .5, .4); break;
      case 'leaf': emit('leaf', p, refl, 6, 1, .9); break;
      case 'gravel': emit('chip', p, refl, 5, 2, 1.2); emit('dust', p, n, 3, .7, .3); break;
      default: emit('clod', p, refl, 7, 2.6, 1); emit('dust', p, n, 4, .9, .4);
    }
    decal(hit);
  }

  function update(dt) {
    flashT -= dt;
    if (flashT <= 0) flash.visible = false;
    light.intensity *= Math.exp(-55*dt); if (light.intensity < .05) light.intensity = 0;
    updateBullets(dt); updateBodies(dt); updateParts(dt);
  }
  return { emit, muzzleFlash, fireBullet, eject, dropMag, impact, update, set onSound(f) { sfxHook = f; } };
}
