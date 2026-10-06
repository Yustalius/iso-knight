import * as THREE from 'three';
import { createKnight } from './knight-model.js';
import { createRig, ATTACKS } from './knight-rig.js';
import { buildWorld, BG } from './world.js';

const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
const clamp = THREE.MathUtils.clamp;
const damp = (a, b, k, dt) => a + (b-a)*(1-Math.exp(-k*dt));
const wrapA = a => Math.atan2(Math.sin(a), Math.cos(a));
const dampA = (a, b, k, dt) => a + wrapA(b-a)*(1-Math.exp(-k*dt));
const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;

// ---------- renderer / camera ----------
const stage = document.getElementById('stage');
const renderer = new THREE.WebGLRenderer({ antialias: false, powerPreference: 'high-performance', preserveDrawingBuffer: true });
renderer.setPixelRatio(1);
renderer.shadowMap.enabled = true; renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping; renderer.toneMappingExposure = 1.25;
stage.appendChild(renderer.domElement);
renderer.domElement.tabIndex = 0;

const scene = new THREE.Scene();
scene.background = new THREE.Color(BG);
const cam = new THREE.OrthographicCamera(-1, 1, 1, -1, .1, 200);
let pix = 2, zoom = 6, viewH = 6, texel = .01;
let camYaw = Math.PI/4, camYawT = Math.PI/4;
const focus = V(0, .9, 1.5);
const ELEV = Math.tan(Math.PI/6);   // 30°: the 2:1 tile isometry of Zomboid-style games

function resize() {
  const w = innerWidth, h = innerHeight;
  const rw = Math.max(1, Math.round(w/pix)), rh = Math.max(1, Math.round(h/pix));
  renderer.setSize(rw, rh, false);
  const aspect = rw/rh;
  viewH = zoom*(aspect < 1 ? 1.4 : 1);
  Object.assign(cam, { left: -viewH*aspect/2, right: viewH*aspect/2, top: viewH/2, bottom: -viewH/2 });
  cam.updateProjectionMatrix();
  texel = viewH/rh;
}
addEventListener('resize', resize);
const camOffset = () => V(Math.sin(camYaw)*20, 20*ELEV, Math.cos(camYaw)*20);
const _qi = new THREE.Quaternion();
function placeCamera(f) {
  const off = camOffset();
  cam.position.copy(f).add(off); cam.lookAt(f);
  // snap to the texel grid so the pixel art does not shimmer while following
  _qi.copy(cam.quaternion).invert();
  const l = f.clone().applyQuaternion(_qi);
  l.x = Math.round(l.x/texel)*texel; l.y = Math.round(l.y/texel)*texel;
  cam.position.copy(l.applyQuaternion(cam.quaternion)).add(off);
}

// ---------- lights (same rig as the character study) ----------
scene.add(new THREE.HemisphereLight(0xdfe7d5, 0x4d4639, 1.9));
const sun = new THREE.DirectionalLight(0xffe8bf, 3.0);
sun.castShadow = true; sun.shadow.mapSize.set(2048, 2048);
Object.assign(sun.shadow.camera, { left: -9, right: 9, top: 9, bottom: -9, near: .5, far: 50 });
sun.shadow.normalBias = .018; sun.shadow.bias = -.0002;
scene.add(sun, sun.target);
const rim = new THREE.DirectionalLight(0xacc3ca, 1.3); scene.add(rim, rim.target);

// ---------- actors ----------
const knight = createKnight();
scene.add(knight.root);
const rig = createRig(knight);
const world = buildWorld(scene, knight);

// ---------- particles ----------
const parts = [];
const partMats = {
  spark: new THREE.MeshBasicMaterial({ color: 0xffe2a0 }),
  straw: world.M.straw, dust: new THREE.MeshStandardMaterial({ color: 0x8a8068, roughness: 1, transparent: true, opacity: .55 })
};
const geoSliver = new THREE.BoxGeometry(.012, .07, .012), geoCube = new THREE.BoxGeometry(.04, .04, .04), geoPuff = new THREE.IcosahedronGeometry(.06, 0);
for (let i = 0; i < 140; i++) { const m = new THREE.Mesh(geoCube, partMats.spark); m.visible = false; scene.add(m); parts.push({ m, v: V(), w: V(), life: 0, max: 1, drag: 0, g: 9.8, grow: 0 }); }
let pi = 0;
function emit(kind, p, dir, n) {
  for (let i = 0; i < n; i++) {
    const q = parts[pi++ % parts.length], m = q.m;
    m.visible = true; m.position.copy(p); m.rotation.set(Math.random()*6, Math.random()*6, 0); m.scale.setScalar(1);
    if (kind === 'spark') { m.geometry = geoCube; m.material = partMats.spark; q.life = q.max = .25 + Math.random()*.2; q.drag = 1; q.g = 9.8; q.grow = 0;
      q.v.set(dir.x*3 + (Math.random()-.5)*3, 1 + Math.random()*2.5, dir.z*3 + (Math.random()-.5)*3); }
    else if (kind === 'straw') { m.geometry = geoSliver; m.material = partMats.straw; q.life = q.max = .9 + Math.random()*.8; q.drag = 3; q.g = 4;
      q.v.set(dir.x*2.4 + (Math.random()-.5)*2.6, .8 + Math.random()*2.2, dir.z*2.4 + (Math.random()-.5)*2.6); q.grow = 0;
      q.w.set((Math.random()-.5)*20, (Math.random()-.5)*20, (Math.random()-.5)*20); }
    else { m.geometry = geoPuff; m.material = partMats.dust; q.life = q.max = .45 + Math.random()*.25; q.drag = 4; q.g = -.4; q.grow = 2.2;
      q.v.set((Math.random()-.5)*.7 + dir.x*.5, .2 + Math.random()*.3, (Math.random()-.5)*.7 + dir.z*.5); m.scale.setScalar(.5); }
    if (kind !== 'straw') q.w.set(0, 0, 0);
  }
}
function updateParts(dt) {
  for (const q of parts) {
    if (!q.m.visible) continue;
    q.life -= dt; if (q.life <= 0) { q.m.visible = false; continue; }
    q.v.y -= q.g*dt; q.v.multiplyScalar(Math.exp(-q.drag*dt));
    q.m.position.addScaledVector(q.v, dt);
    q.m.rotation.x += q.w.x*dt; q.m.rotation.y += q.w.y*dt; q.m.rotation.z += q.w.z*dt;
    if (q.m.position.y < .02) { q.m.position.y = .02; q.v.y *= -.25; q.v.x *= .5; q.v.z *= .5; q.w.multiplyScalar(.5); }
    const k = q.life/q.max;
    if (q.grow) q.m.scale.setScalar(.5 + (1 - k)*q.grow); else if (k < .3) q.m.scale.setScalar(k/.3);
  }
}
const fx = document.getElementById('fx'), pops = [];
function popup(text, wp, crit) {
  const el = document.createElement('div'); el.className = 'dmg' + (crit ? ' crit' : ''); el.textContent = text;
  fx.appendChild(el); pops.push({ el, wp: wp.clone(), t: 0, dx: (Math.random() - .5)*.5 });
}

// ---------- player state & input ----------
const SPEED = { walk: 1.45, run: 2.9, guard: .85 };
const st = { pos: V(.3, 0, 1.4), vel: V(), prevVel: V(), yaw: Math.PI/4, yawT: Math.PI/4, prevYaw: Math.PI/4,
  atk: null, queued: false, lastAtk: null, lastEnd: -9, hitDone: false, target: null };
let time = 0, shake = 0, hitstop = 0, slowmo = false, snap8 = false, paused = false;
let aimHeld = false, blockKey = false, touchBlock = false, newAttack = false, impact = false;
const keys = {};
const mouse = { x: 0, y: 0, inside: false };
const cursor = V(); let cursorOK = false;
const ray = new THREE.Raycaster(), ndc = new THREE.Vector2(), plane = new THREE.Plane(V(0, 1, 0), 0);
function pick(cx, cy, out) {
  const r = renderer.domElement.getBoundingClientRect();
  ndc.set((cx - r.left)/r.width*2 - 1, -(cy - r.top)/r.height*2 + 1);
  ray.setFromCamera(ndc, cam); return ray.ray.intersectPlane(plane, out);
}

function startAttack(aim) {
  if (st.atk) { if (st.atk.t > st.atk.def.hit - .12) st.queued = { aim }; return; }
  const chain = st.lastAtk === ATTACKS.slash && time - st.lastEnd < .3;
  begin(chain ? ATTACKS.backhand : ATTACKS.slash, aim);
}
function begin(def, aim) {
  st.atk = { def, t: 0 }; st.hitDone = false; st.queued = false; st.lastAtk = def; newAttack = true;
  if (aim && cursorOK) st.yawT = st.yaw = Math.atan2(cursor.x - st.pos.x, cursor.z - st.pos.z);
}
function doHit() {
  const def = st.atk.def, s = def.swing, c = Math.cos(st.yaw), sn = Math.sin(st.yaw);
  const swingW = V(s[0]*c + s[2]*sn, 0, -s[0]*sn + s[2]*c).normalize();
  let any = false;
  for (const d of world.dummies) {
    const dx = d.x - st.pos.x, dz = d.z - st.pos.z, dist = Math.hypot(dx, dz);
    if (dist > def.reach || Math.abs(wrapA(Math.atan2(dx, dz) - st.yaw)) > def.arc) continue;
    const crit = Math.random() < .18, dmg = ((9 + Math.random()*8) | 0)*(crit ? 2 : 1);
    const p = world.hit(d, swingW, st.pos, crit ? 1.5 : 1);
    emit('straw', p, swingW, 16); emit('spark', p, swingW, crit ? 8 : 3);
    popup('−' + dmg + (crit ? '!' : ''), V(d.x, 1.85, d.z), crit);
    any = true;
  }
  if (any) { hitstop = .075; impact = true; shake = reduced ? 0 : .07; }
}

const isForm = e => e.target && /INPUT|BUTTON|SUMMARY/.test(e.target.tagName);
addEventListener('keydown', e => {
  if (isForm(e) && !/^Key|Space|Shift/.test(e.code)) return;
  keys[e.code] = true;
  if (e.code === 'Space') { e.preventDefault(); if (!e.repeat) startAttack(false); }
  if (e.code === 'KeyF') blockKey = true;
  if (!e.repeat && e.code === 'KeyQ') camYawT -= Math.PI/2;
  if (!e.repeat && e.code === 'KeyE') camYawT += Math.PI/2;
  if (!e.repeat && e.code === 'KeyT') setSlow(!slowmo);
  if (/^Arrow/.test(e.code)) e.preventDefault();
});
addEventListener('keyup', e => { keys[e.code] = false; if (e.code === 'KeyF') blockKey = false; });
addEventListener('blur', () => { for (const k in keys) keys[k] = false; aimHeld = blockKey = touchBlock = false; });

const cv = renderer.domElement;
let touchId = null;
cv.addEventListener('contextmenu', e => e.preventDefault());
cv.addEventListener('pointerdown', e => {
  cv.focus({ preventScroll: true });
  if (e.pointerType === 'mouse') {
    mouse.x = e.clientX; mouse.y = e.clientY; mouse.inside = true;
    if (e.button === 0) startAttack(true);
    if (e.button === 2) aimHeld = true;
  } else {
    touchId = e.pointerId; cv.setPointerCapture(e.pointerId);
    const p = V(); if (pick(e.clientX, e.clientY, p)) st.target = p;
  }
});
cv.addEventListener('pointermove', e => {
  if (e.pointerType === 'mouse') { mouse.x = e.clientX; mouse.y = e.clientY; mouse.inside = true; }
  else if (e.pointerId === touchId) { const p = V(); if (pick(e.clientX, e.clientY, p)) st.target = p; }
});
cv.addEventListener('pointerleave', e => { if (e.pointerType === 'mouse') mouse.inside = false; });
addEventListener('pointerup', e => { if (e.pointerType === 'mouse' && e.button === 2) aimHeld = false; if (e.pointerId === touchId) touchId = null; });
cv.addEventListener('wheel', e => { e.preventDefault(); zoom = clamp(zoom*(e.deltaY > 0 ? 1.12 : 1/1.12), 3, 13); resize(); }, { passive: false });

const bAtk = document.getElementById('bAtk'), bBlk = document.getElementById('bBlk');
bAtk.addEventListener('pointerdown', e => { e.preventDefault(); startAttack(false); });
bBlk.addEventListener('pointerdown', e => { e.preventDefault(); touchBlock = true; bBlk.classList.add('on'); bBlk.setPointerCapture(e.pointerId); });
for (const ev of ['pointerup', 'pointercancel']) bBlk.addEventListener(ev, () => { touchBlock = false; bBlk.classList.remove('on'); });

const pixIn = document.getElementById('pix'), pixOut = document.getElementById('pixOut');
pixIn.addEventListener('input', () => { pix = +pixIn.value; pixOut.textContent = pix + '×'; resize(); });
pixIn.addEventListener('change', () => pixIn.blur());
document.getElementById('sw').addEventListener('click', e => {
  const b = e.target.closest('button'); if (!b) return;
  for (const x of b.parentNode.children) x.setAttribute('aria-pressed', x === b);
  knight.dye('#' + b.dataset.c); b.blur();
});
const snapIn = document.getElementById('snap8'), slowIn = document.getElementById('slow');
snapIn.addEventListener('change', () => { snap8 = snapIn.checked; snapIn.blur(); });
function setSlow(on) { slowmo = on; slowIn.checked = on; }
slowIn.addEventListener('change', () => { setSlow(slowIn.checked); slowIn.blur(); });
if (innerWidth < 640) document.getElementById('keys').open = false;
const stateEl = document.getElementById('state');

// ---------- simulation ----------
function collide(p) {
  for (const c of world.colliders) {
    const abx = c.bx - c.ax, abz = c.bz - c.az, L2 = abx*abx + abz*abz;
    const t = L2 ? clamp(((p.x - c.ax)*abx + (p.z - c.az)*abz)/L2, 0, 1) : 0;
    const qx = c.ax + abx*t, qz = c.az + abz*t; let dx = p.x - qx, dz = p.z - qz;
    const d = Math.hypot(dx, dz), r = c.r + .3;
    if (d < r) { if (d < 1e-5) { dx = 1; dz = 0; } else { dx /= d; dz /= d; } p.x = qx + dx*r; p.z = qz + dz*r; }
  }
  p.x = clamp(p.x, -15, 15); p.z = clamp(p.z, -15, 15);
}

const debugInput = { on: false, mx: 0, mz: 0, run: false, block: false };
function update(rawDt) {
  hitstop = Math.max(0, hitstop - rawDt);
  const dt = rawDt*(slowmo ? .25 : 1)*(hitstop > 0 ? .06 : 1);
  time += dt;
  camYaw = damp(camYaw, camYawT, 8, rawDt);
  if (mouse.inside) cursorOK = !!pick(mouse.x, mouse.y, cursor);

  let mx = 0, mz = 0, run = keys.ShiftLeft || keys.ShiftRight, blocking = blockKey || aimHeld || touchBlock;
  if (keys.KeyD || keys.ArrowRight) mx++; if (keys.KeyA || keys.ArrowLeft) mx--;
  if (keys.KeyW || keys.ArrowUp) mz++; if (keys.KeyS || keys.ArrowDown) mz--;
  if (debugInput.on) ({ mx, mz, run, block: blocking } = debugInput);
  const right = V(Math.cos(camYaw), 0, -Math.sin(camYaw)), up = V(-Math.sin(camYaw), 0, -Math.cos(camYaw));
  const dir = V().addScaledVector(right, mx).addScaledVector(up, mz);
  if (dir.lengthSq() > 0) { st.target = null; dir.normalize(); }
  else if (st.target) {
    dir.subVectors(st.target, st.pos); dir.y = 0; const L = dir.length();
    if (L < .15) { st.target = null; dir.set(0, 0, 0); } else dir.divideScalar(L);
  }
  const moving = dir.lengthSq() > 0;
  let spd = moving ? (blocking ? SPEED.guard : run ? SPEED.run : SPEED.walk) : 0;
  if (st.atk) spd *= .35;
  st.prevVel.copy(st.vel);
  st.vel.lerp(dir.multiplyScalar(spd), 1 - Math.exp(-(moving ? 7 : 10)*dt));
  st.pos.addScaledVector(st.vel, dt);
  collide(st.pos);

  // facing: aim with the shield up, otherwise turn into the direction of travel
  if (aimHeld && cursorOK) st.yawT = Math.atan2(cursor.x - st.pos.x, cursor.z - st.pos.z);
  else if (!st.atk && moving && !(blocking && !aimHeld)) st.yawT = Math.atan2(st.vel.x, st.vel.z);
  st.prevYaw = st.yaw;
  st.yaw = dampA(st.yaw, st.yawT, st.vel.length() > 2 ? 9 : 12, dt);
  const yawRate = dt > 0 ? wrapA(st.yaw - st.prevYaw)/dt : 0;

  // attack clock
  if (st.atk) {
    st.atk.t += dt;
    if (!st.hitDone && st.atk.t >= st.atk.def.hit) { st.hitDone = true; doHit(); }
    if (st.queued && st.atk.t >= st.atk.def.chain) begin(st.atk.def === ATTACKS.slash ? ATTACKS.backhand : ATTACKS.slash, st.queued.aim);
    else if (st.atk.t >= st.atk.def.dur) { st.atk = null; st.lastEnd = time; }
  }

  // animation: hand the rig local velocity / acceleration
  const shownYaw = snap8 ? Math.round(st.yaw/(Math.PI/4))*(Math.PI/4) : st.yaw;
  knight.root.position.copy(st.pos);
  knight.root.rotation.y = shownYaw;
  const c = Math.cos(st.yaw), s = Math.sin(st.yaw);
  const toLocal = v => V(v.x*c - v.z*s, 0, v.x*s + v.z*c);
  const acc = dt > 0 ? st.vel.clone().sub(st.prevVel).divideScalar(dt) : V();
  rig.update(dt, {
    velLocal: toLocal(st.vel), accLocal: toLocal(acc), yawRate,
    block: blocking && !st.atk, attack: st.atk, newAttack, impact
  });
  newAttack = impact = false;
  for (const ev of rig.state.events.splice(0)) if (ev.speed > 2.2) emit('dust', ev.pos, toWorldDir(ev.dir), 3);

  world.update(dt, time);
  updateParts(dt);

  // see-through for anything between the camera and the hero
  const sy = Math.sin(camYaw), cy = Math.cos(camYaw);
  for (const f of world.fadeables) {
    const ox = f.x - st.pos.x, oz = f.z - st.pos.z;
    const front = ox*sy + oz*cy, lat = Math.abs(ox*cy - oz*sy);
    const want = front > .1 && front < 9 && lat < f.r + .5 ? .25 : 1;
    f.op = damp(f.op, want, 8, rawDt);
    for (const m of f.mats) { m.opacity = f.op; m.transparent = f.op < .999; m.depthWrite = f.op > .6; }
  }

  // camera follow, shake, light follow
  focus.x = damp(focus.x, st.pos.x + st.vel.x*.25, 5, rawDt); focus.z = damp(focus.z, st.pos.z + st.vel.z*.25, 5, rawDt);
  shake = Math.max(0, shake - rawDt*.5);
  const f2 = focus.clone(); if (shake > 0) { f2.x += (Math.random() - .5)*shake; f2.z += (Math.random() - .5)*shake; }
  placeCamera(f2);
  sun.position.set(focus.x - 6, 10, focus.z + 3); sun.target.position.set(focus.x, 0, focus.z); sun.target.updateMatrixWorld();
  rim.position.set(focus.x + 4, 5, focus.z - 6); rim.target.position.set(focus.x, 0, focus.z); rim.target.updateMatrixWorld();

  // floating numbers
  const W = innerWidth, H = innerHeight;
  for (let i = pops.length - 1; i >= 0; i--) {
    const p = pops[i]; p.t += rawDt*(slowmo ? .4 : 1);
    if (p.t > 1) { p.el.remove(); pops.splice(i, 1); continue; }
    const v = p.wp.clone(); v.y += p.t*.8; v.x += p.dx*p.t; v.project(cam);
    p.el.style.transform = `translate(${(v.x + 1)/2*W}px,${(1 - v.y)/2*H}px) translate(-50%,-50%) scale(${1 + Math.max(0, .22 - p.t)*2.2})`;
    p.el.style.opacity = String(1 - Math.max(0, (p.t - .55)/.45));
  }

  const sp = Math.hypot(st.vel.x, st.vel.z);
  const act = st.atk ? st.atk.def.name : blocking ? (sp > .2 ? 'Шаг под щитом' : 'Щит') : sp > 2.2 ? 'Бег' : sp > .2 ? 'Шаг' : Math.abs(yawRate) > 1 ? 'Разворот' : 'Стойка';
  stateEl.textContent = `${act} · ${sp.toFixed(2)} м/с${slowmo ? ' · ¼×' : ''}`;
}
function toWorldDir(d) { const c = Math.cos(st.yaw), s = Math.sin(st.yaw); return V(d.x*c + d.z*s, 0, -d.x*s + d.z*c); }

resize();
placeCamera(focus);
let last = performance.now();
function loop(now) {
  requestAnimationFrame(loop);
  const dt = Math.min((now - last)/1000, .05); last = now;
  if (paused) return;
  update(dt);
  renderer.render(scene, cam);
}
requestAnimationFrame(loop);

// Hooks for the automated screenshot checks in tools/shots.mjs
window.__knight = {
  pause(on = true) { paused = on; },
  input(o) { Object.assign(debugInput, { on: true }, o); },
  attack() { startAttack(false); },
  step(seconds, fps = 60) { for (let i = 0; i < Math.round(seconds*fps); i++) update(1/fps); renderer.render(scene, cam); },
  view(yawDeg, z = zoom) { camYaw = camYawT = yawDeg*Math.PI/180; zoom = z; resize(); placeCamera(focus); renderer.render(scene, cam); },
  place(x, z, yawDeg) { st.pos.set(x, 0, z); st.vel.set(0, 0, 0); st.yaw = st.yawT = yawDeg*Math.PI/180; focus.set(x, .9, z); },
  render() { renderer.render(scene, cam); },
  get state() { return { pos: st.pos.toArray(), yaw: st.yaw, atk: st.atk && st.atk.def.name, t: st.atk && st.atk.t }; },
  stats: { triangles: knight.triangles }
};
