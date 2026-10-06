import * as THREE from 'three';
import { createSoldier } from './soldier-model.js';
import { createRig, MELEE, RELOAD } from './soldier-rig.js';
import { buildRange, BG, START } from './range-world.js';
import { createFx } from './gunfx.js';
import { createSfx } from './sfx.js';

const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
const clamp = THREE.MathUtils.clamp;
const smooth = x => { x = clamp(x, 0, 1); return x*x*(3-2*x); };
const damp = (a, b, k, dt) => a + (b-a)*(1-Math.exp(-k*dt));
const wrapA = a => Math.atan2(Math.sin(a), Math.cos(a));
const dampA = (a, b, k, dt) => a + wrapA(b-a)*(1-Math.exp(-k*dt));
const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;

// ---------- renderer / camera (the knight's setup: 30° ortho, texel-snapped follow) ----------
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
let pix = 2, zoom = 7, viewH = 7, texel = .01, rw = 1, rh = 1;
let camYaw = Math.PI/4, camYawT = Math.PI/4;
const focus = V(START.x, .9, START.z - 1);
const ELEV = Math.tan(Math.PI/6);
const maskRT = new THREE.WebGLRenderTarget(1, 1, { depthBuffer: true });

function resize() {
  const w = innerWidth, h = innerHeight;
  rw = Math.max(1, Math.round(w/pix)); rh = Math.max(1, Math.round(h/pix));
  renderer.setSize(rw, rh, false);
  maskRT.setSize(rw, rh); outlineMat.uniforms.texel.value.set(1/rw, 1/rh);
  const aspect = rw/rh;
  viewH = zoom*(aspect < 1 ? 1.4 : 1);
  Object.assign(cam, { left: -viewH*aspect/2, right: viewH*aspect/2, top: viewH/2, bottom: -viewH/2 });
  cam.updateProjectionMatrix();
  texel = viewH/rh;
  const dpr = Math.min(2, devicePixelRatio || 1);
  ret.width = Math.round(w*dpr); ret.height = Math.round(h*dpr); rctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}
addEventListener('resize', resize);
const camOffset = () => V(Math.sin(camYaw)*20, 20*ELEV, Math.cos(camYaw)*20);
const _qi = new THREE.Quaternion();
function placeCamera(f) {
  const off = camOffset();
  cam.position.copy(f).add(off); cam.lookAt(f);
  _qi.copy(cam.quaternion).invert();
  const l = f.clone().applyQuaternion(_qi);
  l.x = Math.round(l.x/texel)*texel; l.y = Math.round(l.y/texel)*texel;
  cam.position.copy(l.applyQuaternion(cam.quaternion)).add(off);
}

// ---------- lights ----------
scene.add(new THREE.HemisphereLight(0xdfe7d5, 0x4d4639, 1.9));
const sun = new THREE.DirectionalLight(0xffe8bf, 3.0);
sun.castShadow = true; sun.shadow.mapSize.set(2048, 2048);
Object.assign(sun.shadow.camera, { left: -11, right: 11, top: 11, bottom: -11, near: .5, far: 60 });
sun.shadow.normalBias = .018; sun.shadow.bias = -.0002;
scene.add(sun, sun.target);
const rim = new THREE.DirectionalLight(0xacc3ca, 1.3); scene.add(rim, rim.target);

// ---------- actors ----------
const soldier = createSoldier();
scene.add(soldier.root, soldier.sling);
const rig = createRig(soldier);
const world = buildRange(scene);
const fx = createFx(scene, world, soldier);
const sfx = createSfx();

// ---------- aim outline (Project Zomboid's coloured target outline) ----------
// Outlined targets are drawn flat into a mask at render resolution; a full-screen pass then
// paints the one-pixel ring around each silhouette in the target's hit-chance colour.
const OUTLINE_LAYER = 1;
for (const t of world.targets) { t.maskMat = new THREE.MeshBasicMaterial({ color: 0xffffff }); for (const m of t.meshes) m.layers.enable(OUTLINE_LAYER); }
const outlineMat = new THREE.ShaderMaterial({
  uniforms: { mask: { value: maskRT.texture }, texel: { value: new THREE.Vector2(1, 1) } },
  vertexShader: 'varying vec2 vUv; void main(){ vUv = uv; gl_Position = vec4(position.xy, 0., 1.); }',
  fragmentShader: `uniform sampler2D mask; uniform vec2 texel; varying vec2 vUv;
    void main(){
      if (texture2D(mask, vUv).a > .5) discard;
      vec4 n = texture2D(mask, vUv + vec2(texel.x, 0.)), t;
      t = texture2D(mask, vUv - vec2(texel.x, 0.)); if (t.a > n.a) n = t;
      t = texture2D(mask, vUv + vec2(0., texel.y)); if (t.a > n.a) n = t;
      t = texture2D(mask, vUv - vec2(0., texel.y)); if (t.a > n.a) n = t;
      if (n.a < .5) discard;
      gl_FragColor = vec4(n.rgb, 1.);
      #include <colorspace_fragment>
    }`,
  depthTest: false, depthWrite: false
});
const quadScene = new THREE.Scene();
{ const q = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), outlineMat); q.frustumCulled = false; quadScene.add(q); }
let outlined = [];
function renderOutline() {
  if (!outlined.length) return;
  for (const t of outlined) for (const m of t.meshes) { m.userData.mat0 = m.material; m.material = t.maskMat; }
  const bg = scene.background; scene.background = null;
  cam.layers.set(OUTLINE_LAYER); renderer.shadowMap.autoUpdate = false;
  renderer.setRenderTarget(maskRT); renderer.setClearColor(0x000000, 0); renderer.clear(); renderer.render(scene, cam);
  renderer.setRenderTarget(null); renderer.shadowMap.autoUpdate = true; cam.layers.set(0); scene.background = bg;
  for (const t of outlined) for (const m of t.meshes) m.material = m.userData.mat0;
  renderer.autoClear = false; renderer.render(quadScene, cam); renderer.autoClear = true;
}
function render() { renderer.render(scene, cam); if (opts.outline) renderOutline(); drawReticle(); }

// ---------- floating labels ----------
const fxLayer = document.getElementById('fx'), pops = [];
function popup(text, wp, cls = '') {
  const el = document.createElement('div'); el.className = 'pop ' + cls; el.textContent = text;
  fxLayer.appendChild(el); pops.push({ el, wp: wp.clone(), t: 0, dx: (Math.random() - .5)*.5 });
}

// ---------- weapon ----------
// Spread is a half-angle in radians. Holding the aim steadies it ("focus"), moving, turning and
// each shot widen it; the reticle circle is exactly this cone at the cursor's distance.
const RIFLE = { name: 'M16A2', cap: 30, rpm: 800, base: .0055, raise: .075, move: .04, turn: .03, bloom: .014, range: 120 };
const MODES = [{ key: 'semi', label: 'ОДИН.' }, { key: 'burst', label: 'ОЧЕРЕДЬ' }, { key: 'auto', label: 'АВТ.' }];
const wpn = { mag: RIFLE.cap, chamber: true, mode: 0, cool: 0, pending: 0, held: false, pressed: false, dryDone: false, shots: 0, hits: 0, kills: 0, focus: 0, bloom: 0 };

// ---------- player state & input ----------
const SPEED = { walk: 1.45, run: 3.0, aim: .95, crouch: .85 };
const st = { pos: V(START.x, 0, START.z), vel: V(), prevVel: V(), yaw: START.yaw, yawT: START.yaw, prevYaw: START.yaw,
  melee: null, queued: false, hitDone: false, reload: null, crouch: false, target: null };
let time = 0, shake = 0, hitstop = 0, slowmo = false, snap8 = false, paused = false;
let aimHeld = false, touchAim = false, newMelee = false, impact = false, shotFlag = false, yawRate = 0;
const opts = { outline: true, pan: true };
const keys = {};
const mouse = { x: 0, y: 0, inside: false };
const aimPt = V(), aimInfo = { ok: false, hit: null, chance: 0 };
let debugAim = null;
const ray = new THREE.Raycaster(), ndc = new THREE.Vector2();
function cursorRay(cx, cy) {
  const r = renderer.domElement.getBoundingClientRect();
  ndc.set((cx - r.left)/r.width*2 - 1, -(cy - r.top)/r.height*2 + 1);
  ray.setFromCamera(ndc, cam); return ray.ray;
}
function groundAt(cx, cy, out) { return cursorRay(cx, cy).intersectPlane(new THREE.Plane(V(0, 1, 0), 0), out); }
const isAiming = () => (aimHeld || touchAim || !!debugAim) && !st.reload && !st.melee;
const panOf = p => { const v = p.clone().project(cam); return clamp(v.x*.8, -1, 1); };
const volOf = p => 1/(1 + p.distanceTo(st.pos)*.07);

function startMelee() {
  if (st.reload) return;
  if (st.melee) { if (st.melee.t > st.melee.def.hit - .1) st.queued = true; return; }
  st.melee = { def: MELEE.shove, t: 0 }; st.hitDone = false; st.queued = false; newMelee = true;
  if (aimInfo.ok && mouse.inside) st.yawT = st.yaw = Math.atan2(aimPt.x - st.pos.x, aimPt.z - st.pos.z);
}
function doMeleeHit() {
  const def = st.melee.def, fwd = V(Math.sin(st.yaw), 0, Math.cos(st.yaw));
  let any = false;
  for (const t of world.targets) {
    const c = t.center(), dx = c.x - st.pos.x, dz = c.z - st.pos.z, dist = Math.hypot(dx, dz);
    if (dist > def.reach || Math.abs(wrapA(Math.atan2(dx, dz) - st.yaw)) > def.arc || c.y > 1.9) continue;
    const r = { point: c, normal: fwd.clone().negate(), kind: t.material, target: t };
    const knocked = t.hit(r, fwd, 1.4);
    fx.emit(t.material === 'wood' ? 'splinter' : 'spark', c, fwd, 6, 1.5, .8);
    sfx.impact(t.material === 'wood' ? 'wood' : t.material, panOf(c), 1.4); if (t.type === 'gong') sfx.ding(panOf(c), .8);
    if (knocked && t.type === 'popup') popup('Сбита', c.clone().setY(1.6));
    any = true;
  }
  if (any) { hitstop = .06; impact = true; shake = reduced ? 0 : .05; }
}
function startReload() {
  if (st.reload || st.melee || wpn.mag >= RIFLE.cap) return;
  st.reload = { t: 0, def: RELOAD, empty: !wpn.chamber };
}
function cycleMode() { wpn.mode = (wpn.mode + 1) % MODES.length; sfx.click(0); hud(true); }

// a uniformly distributed direction inside the cone
function coneDir(d, half) {
  const a = Math.abs(d.y) < .9 ? V(0, 1, 0).cross(d).normalize() : V(1, 0, 0).cross(d).normalize(), b = d.clone().cross(a);
  const r = Math.sqrt(Math.random())*Math.tan(half), th = Math.random()*Math.PI*2;
  return d.clone().addScaledVector(a, r*Math.cos(th)).addScaledVector(b, r*Math.sin(th)).normalize();
}
function spread() {
  const sp = Math.hypot(st.vel.x, st.vel.z);
  let s = RIFLE.base + (1 - smooth(wpn.focus))*RIFLE.raise + clamp(sp/1.2, 0, 1)*RIFLE.move + clamp(Math.abs(yawRate)/2.5, 0, 1)*RIFLE.turn + wpn.bloom;
  return s*(st.crouch ? .75 : 1);
}
function fire() {
  const o = rig.state.out;
  wpn.chamber = false; if (wpn.mag > 0) { wpn.mag--; wpn.chamber = true; }
  const d = coneDir(o.aimDir, spread());
  fx.fireBullet(o.muzzle, d, onBulletHit);
  fx.muzzleFlash(o.muzzle, o.barrel);
  fx.eject(o.eject, o.ejectDir, st.vel);
  sfx.shot(panOf(o.muzzle));
  wpn.bloom += RIFLE.bloom; wpn.focus = Math.max(0, wpn.focus - .12); wpn.shots++;
  shake = Math.max(shake, reduced ? 0 : .025); shotFlag = true;
  if (wpn.shots === 1 && !keysTouched) pad('keys').open = false;   // the controls card folds away once you've got the idea
}
function onBulletHit(hit, dir) {
  fx.impact(hit, dir);
  const pan = panOf(hit.point), vol = volOf(hit.point);
  sfx.impact(hit.kind, pan, vol);
  const t = hit.target; if (!t) return;
  wpn.hits++;
  const knocked = t.hit(hit, dir, 1);
  const top = t.center().setY(t.type === 'can' ? t.center().y + .25 : 1.55);
  if (t.type === 'gong') { sfx.ding(pan, vol*1.4); popup('Дзынь', top); }
  else if (t.type === 'can') { sfx.impact('tin', pan, vol*1.4); popup('Банка', top, 'small'); }
  else if (knocked) { wpn.kills++; popup(hit.zone === 'head' ? 'В голову' : 'Поражена', top, hit.zone === 'head' ? 'crit' : ''); }
}

// ---------- DOM input ----------
const isForm = e => e.target && /INPUT|BUTTON|SUMMARY/.test(e.target.tagName);
addEventListener('keydown', e => {
  if (isForm(e) && !/^Key|Space|Shift/.test(e.code)) return;
  sfx.unlock();
  keys[e.code] = true;
  if (e.code === 'Space') { e.preventDefault(); if (!e.repeat) startMelee(); }
  if (!e.repeat && e.code === 'KeyR') startReload();
  if (!e.repeat && (e.code === 'KeyX' || e.code === 'KeyB')) cycleMode();
  if (!e.repeat && e.code === 'KeyC') st.crouch = !st.crouch;
  if (!e.repeat && e.code === 'KeyQ') camYawT -= Math.PI/2;
  if (!e.repeat && e.code === 'KeyE') camYawT += Math.PI/2;
  if (!e.repeat && e.code === 'KeyT') setSlow(!slowmo);
  if (/^Arrow/.test(e.code)) e.preventDefault();
});
addEventListener('keyup', e => { keys[e.code] = false; });
addEventListener('blur', () => { for (const k in keys) keys[k] = false; aimHeld = wpn.held = false; });

const cv = renderer.domElement;
let touchId = null;
cv.addEventListener('contextmenu', e => e.preventDefault());
cv.addEventListener('pointerdown', e => {
  cv.focus({ preventScroll: true }); sfx.unlock();
  if (e.pointerType === 'mouse') {
    mouse.x = e.clientX; mouse.y = e.clientY; mouse.inside = true;
    if (e.button === 2) aimHeld = true;
    if (e.button === 0) { if (aimHeld) { wpn.held = true; wpn.pressed = true; wpn.dryDone = false; } else startMelee(); }
  } else {
    touchId = e.pointerId; cv.setPointerCapture(e.pointerId);
    if (touchAim) { mouse.x = e.clientX; mouse.y = e.clientY; mouse.inside = true; }
    else { const p = V(); if (groundAt(e.clientX, e.clientY, p)) st.target = p; }
  }
});
cv.addEventListener('pointermove', e => {
  if (e.pointerType === 'mouse') { mouse.x = e.clientX; mouse.y = e.clientY; mouse.inside = true; }
  else if (e.pointerId === touchId) {
    if (touchAim) { mouse.x = e.clientX; mouse.y = e.clientY; mouse.inside = true; }
    else { const p = V(); if (groundAt(e.clientX, e.clientY, p)) st.target = p; }
  }
});
cv.addEventListener('pointerleave', e => { if (e.pointerType === 'mouse') mouse.inside = false; });
addEventListener('pointerup', e => {
  if (e.pointerType === 'mouse') { if (e.button === 2) aimHeld = false; if (e.button === 0) wpn.held = false; }
  if (e.pointerId === touchId) touchId = null;
});
cv.addEventListener('wheel', e => { e.preventDefault(); zoom = clamp(zoom*(e.deltaY > 0 ? 1.12 : 1/1.12), 3, 15); resize(); }, { passive: false });

const pad = id => document.getElementById(id);
pad('bAim').addEventListener('pointerdown', e => { e.preventDefault(); sfx.unlock(); touchAim = !touchAim; pad('bAim').classList.toggle('on', touchAim); if (!touchAim) mouse.inside = false; });
pad('bFire').addEventListener('pointerdown', e => { e.preventDefault(); sfx.unlock(); if (!touchAim) { touchAim = true; pad('bAim').classList.add('on'); } wpn.held = true; wpn.pressed = true; wpn.dryDone = false; pad('bFire').setPointerCapture(e.pointerId); });
for (const ev of ['pointerup', 'pointercancel']) pad('bFire').addEventListener(ev, () => { wpn.held = false; });
pad('bReload').addEventListener('pointerdown', e => { e.preventDefault(); startReload(); });
pad('bShove').addEventListener('pointerdown', e => { e.preventDefault(); sfx.unlock(); startMelee(); });

const pixIn = pad('pix'), pixOut = pad('pixOut');
pixIn.addEventListener('input', () => { pix = +pixIn.value; pixOut.textContent = pix + '×'; resize(); });
pixIn.addEventListener('change', () => pixIn.blur());
pad('sw').addEventListener('click', e => {
  const b = e.target.closest('button'); if (!b) return;
  for (const x of b.parentNode.children) x.setAttribute('aria-pressed', x === b);
  soldier.dye(b.dataset.c); b.blur();
});
const bindChk = (id, f) => { const el = pad(id); el.addEventListener('change', () => { f(el.checked); el.blur(); }); return el; };
const slowIn = bindChk('slow', v => setSlow(v));
bindChk('snap8', v => { snap8 = v; });
bindChk('outline', v => { opts.outline = v; });
bindChk('pan', v => { opts.pan = v; });
bindChk('sound', v => { sfx.enabled = v; });
function setSlow(on) { slowmo = on; slowIn.checked = on; }
if (innerWidth < 640) pad('keys').open = false;
let keysTouched = false; pad('keys').addEventListener('toggle', e => { if (e.isTrusted) keysTouched = true; });
const stateEl = pad('state');

// ---------- HUD: ammo, fire mode, score ----------
const pipsEl = pad('pips'), rndEl = pad('rnd'), chEl = pad('chamber'), modeEl = pad('mode'), statEl = pad('stat'), ammoEl = pad('ammo');
for (let i = 0; i < RIFLE.cap; i++) pipsEl.appendChild(document.createElement('i'));
let hudKey = '';
function hud(force) {
  const key = [wpn.mag, wpn.chamber, wpn.mode, wpn.shots, wpn.hits, wpn.kills, !!st.reload].join();
  if (key === hudKey && !force) return; hudKey = key;
  rndEl.textContent = wpn.mag; chEl.textContent = wpn.chamber ? '+1' : '';
  modeEl.textContent = MODES[wpn.mode].label;
  [...pipsEl.children].forEach((p, i) => p.className = i < wpn.mag ? '' : 'spent');
  ammoEl.classList.toggle('low', wpn.mag <= 5); ammoEl.classList.toggle('empty', !wpn.chamber && wpn.mag === 0);
  pad('hint').textContent = st.reload ? 'перезарядка…' : !wpn.chamber && wpn.mag === 0 ? 'пусто — R' : '';
  const acc = wpn.shots ? Math.round(wpn.hits/wpn.shots*100) : 0;
  statEl.textContent = `выстрелов ${wpn.shots} · попаданий ${wpn.hits} (${acc}%) · сбито ${wpn.kills}`;
}

// ---------- reticle ----------
const ret = pad('ret'), rctx = ret.getContext('2d');
const VOGEL = Array.from({ length: 48 }, (_, k) => { const r = Math.sqrt((k + .5)/48), a = k*2.39996; return [r*Math.cos(a), r*Math.sin(a)]; });
const chanceColor = c => c < .5 ? new THREE.Color(0xd8452f).lerp(new THREE.Color(0xe8c547), c*2) : new THREE.Color(0xe8c547).lerp(new THREE.Color(0x6cd25a), (c - .5)*2);
// share of the cone's rays that would land on the target (deterministic Vogel disk)
function hitChance(t, o, d, half) {
  const c = t.center(), to = c.clone().sub(o), dist = to.length();
  if (dist > RIFLE.range || to.normalize().angleTo(d) > half + Math.atan(.7/dist)) return 0;
  const a = Math.abs(d.y) < .9 ? V(0, 1, 0).cross(d).normalize() : V(1, 0, 0).cross(d).normalize(), b = d.clone().cross(a), k = Math.tan(half);
  let n = 0; const dir = V();
  for (const [x, y] of VOGEL) { dir.copy(d).addScaledVector(a, x*k).addScaledVector(b, y*k).normalize(); if (t.test(o, dir)) n++; }
  return n/VOGEL.length;
}
let retState = null;
function drawReticle() {
  const W = innerWidth, H = innerHeight;
  rctx.clearRect(0, 0, W, H);
  if (!retState) return;
  const { center, r, color, chance, rounds } = retState;
  const x = Math.round(center.x), y = Math.round(center.y), rr = Math.max(4, r);
  rctx.lineWidth = 2; rctx.strokeStyle = 'rgba(10,12,9,.55)';
  rctx.beginPath(); rctx.arc(x, y, rr + 1, 0, Math.PI*2); rctx.stroke();
  rctx.strokeStyle = color; rctx.lineWidth = 1.5;
  rctx.beginPath(); rctx.arc(x, y, rr, 0, Math.PI*2); rctx.stroke();
  for (let i = 0; i < 4; i++) {
    const a = i*Math.PI/2, c = Math.cos(a), s = Math.sin(a);
    rctx.fillStyle = 'rgba(10,12,9,.6)'; rctx.fillRect(x + c*(rr + 3) - 2 + (c ? 0 : 0), y + s*(rr + 3) - 2, c ? 8*Math.sign(c) + 2 : 4, s ? 8*Math.sign(s) + 2 : 4);
    rctx.fillStyle = color; rctx.fillRect(x + c*(rr + 4) - 1, y + s*(rr + 4) - 1, c ? 6*Math.sign(c) : 2, s ? 6*Math.sign(s) : 2);
  }
  rctx.fillStyle = color; rctx.fillRect(x - 1, y - 1, 2, 2);
  rctx.font = '500 11px "IBM Plex Mono", ui-monospace, monospace'; rctx.textBaseline = 'top';
  const tx = x + rr + 10, ty = y + 6;
  const label = (chance != null ? Math.round(chance*100) + '%  ' : '') + rounds;
  rctx.fillStyle = 'rgba(10,12,9,.7)'; rctx.fillText(label, tx + 1, ty + 1);
  rctx.fillStyle = color; rctx.fillText(label, tx, ty);
}

// ---------- simulation ----------
function collide(p) {
  for (const c of world.colliders) {
    const abx = c.bx - c.ax, abz = c.bz - c.az, L2 = abx*abx + abz*abz;
    const t = L2 ? clamp(((p.x - c.ax)*abx + (p.z - c.az)*abz)/L2, 0, 1) : 0;
    const qx = c.ax + abx*t, qz = c.az + abz*t; let dx = p.x - qx, dz = p.z - qz;
    const d = Math.hypot(dx, dz), r = c.r + .28;
    if (d < r) { if (d < 1e-5) { dx = 1; dz = 0; } else { dx /= d; dz /= d; } p.x = qx + dx*r; p.z = qz + dz*r; }
  }
  p.x = clamp(p.x, -9, 9); p.z = clamp(p.z, -13, 7.6);
}

const debugInput = { on: false, mx: 0, mz: 0, run: false };
function update(rawDt) {
  hitstop = Math.max(0, hitstop - rawDt);
  const dt = rawDt*(slowmo ? .25 : 1)*(hitstop > 0 ? .06 : 1);
  time += dt;
  camYaw = damp(camYaw, camYawT, 8, rawDt);

  // ----- input → intended motion -----
  let mx = 0, mz = 0, run = keys.ShiftLeft || keys.ShiftRight;
  if (keys.KeyD || keys.ArrowRight) mx++; if (keys.KeyA || keys.ArrowLeft) mx--;
  if (keys.KeyW || keys.ArrowUp) mz++; if (keys.KeyS || keys.ArrowDown) mz--;
  if (debugInput.on) ({ mx, mz, run } = debugInput);
  const aiming = isAiming();
  const right = V(Math.cos(camYaw), 0, -Math.sin(camYaw)), up = V(-Math.sin(camYaw), 0, -Math.cos(camYaw));
  const dir = V().addScaledVector(right, mx).addScaledVector(up, mz);
  if (dir.lengthSq() > 0) { st.target = null; dir.normalize(); }
  else if (st.target) {
    dir.subVectors(st.target, st.pos); dir.y = 0; const L = dir.length();
    if (L < .15) { st.target = null; dir.set(0, 0, 0); } else dir.divideScalar(L);
  }
  const moving = dir.lengthSq() > 0;
  let spd = !moving ? 0 : aiming ? (st.crouch ? SPEED.crouch*.7 : SPEED.aim) : st.crouch ? SPEED.crouch : run && !st.reload ? SPEED.run : SPEED.walk;
  if (st.melee) spd *= .4; if (st.reload) spd = Math.min(spd, SPEED.walk*.8);
  st.prevVel.copy(st.vel);
  st.vel.lerp(dir.multiplyScalar(spd), 1 - Math.exp(-(moving ? 7 : 10)*dt));
  st.pos.addScaledVector(st.vel, dt);
  collide(st.pos);

  // ----- aim point under the cursor: targets, props, or the ground -----
  aimInfo.ok = false; aimInfo.hit = null;
  if (debugAim) { aimPt.copy(debugAim); aimInfo.ok = true; }
  else if (mouse.inside) {
    const r = cursorRay(mouse.x, mouse.y), h = world.trace(r.origin, r.direction, 200, true);
    if (h) { aimPt.copy(h.point); aimInfo.ok = true; aimInfo.hit = h; }
  } else if (touchAim) {
    // touch without a chosen point: the nearest standing target in front, like a gamepad's auto-aim
    let best = null, bd = 1e9;
    for (const t of world.targets) {
      if (t.type === 'popup' && t.state !== 'up') continue;
      const c = t.center(), dx = c.x - st.pos.x, dz = c.z - st.pos.z, d = Math.hypot(dx, dz);
      if (Math.abs(wrapA(Math.atan2(dx, dz) - st.yaw)) < 1 && d < 20 && d < bd) { bd = d; best = c; }
    }
    if (best) { aimPt.copy(best); aimInfo.ok = true; }
  }

  // ----- facing: toward the aim point while aiming, else into the direction of travel -----
  if (aiming && aimInfo.ok) st.yawT = Math.atan2(aimPt.x - st.pos.x, aimPt.z - st.pos.z);
  else if (!st.melee && moving) st.yawT = Math.atan2(st.vel.x, st.vel.z);
  st.prevYaw = st.yaw;
  st.yaw = dampA(st.yaw, st.yawT, aiming ? 7 : st.vel.length() > 2 ? 9 : 12, dt);
  yawRate = dt > 0 ? wrapA(st.yaw - st.prevYaw)/dt : 0;

  // ----- weapon -----
  const raised = rig.state.aim;
  if (aiming) {
    const still = 1 - clamp(Math.hypot(st.vel.x, st.vel.z)/1.2, 0, 1);
    wpn.focus = damp(wpn.focus, raised > .85 ? 1 : 0, (raised > .85 ? .6 + 1.2*still : 3)*(1 - clamp(Math.abs(yawRate)/4, 0, .8)), dt);
  } else { wpn.focus = damp(wpn.focus, 0, 6, dt); wpn.pressed = false; wpn.pending = 0; }
  wpn.bloom *= Math.exp(-4.5*dt);
  wpn.cool = Math.max(-1/60, wpn.cool - dt);
  const ready = aiming && raised > .85;
  if (ready && wpn.pressed) { wpn.pressed = false; wpn.pending = MODES[wpn.mode].key === 'burst' ? 3 : 1; }
  const auto = MODES[wpn.mode].key === 'auto';
  if (ready && wpn.cool <= 0 && (wpn.pending > 0 || (auto && wpn.held))) {
    if (wpn.chamber) { fire(); wpn.cool = Math.max(0, wpn.cool) + 60/RIFLE.rpm; wpn.pending = Math.max(0, wpn.pending - 1); }
    else { if (!wpn.dryDone) { sfx.dry(0); wpn.dryDone = true; popup('Пусто', st.pos.clone().setY(2), 'small'); } wpn.pending = 0; }
  }

  // ----- melee and reload clocks -----
  if (st.melee) {
    st.melee.t += dt;
    if (!st.hitDone && st.melee.t >= st.melee.def.hit) { st.hitDone = true; doMeleeHit(); }
    if (st.queued && st.melee.t >= st.melee.def.chain + .15) { st.melee = { def: MELEE.shove, t: 0 }; st.hitDone = false; st.queued = false; newMelee = true; }
    else if (st.melee.t >= st.melee.def.dur) st.melee = null;
  }
  if (st.reload) {
    const r = st.reload, def = r.def, pan = panOf(st.pos);
    r.t += dt;
    if (!r.out && r.t >= def.magOut) { r.out = true; sfx.magOut(pan); fx.dropMag(rig.state.out.magMatrix, st.vel.clone().multiplyScalar(.8)); wpn.mag = 0; }
    if (!r.grab && r.t >= def.grab) { r.grab = true; sfx.click(pan); }
    if (!r.seat && r.t >= def.seat) { r.seat = true; sfx.magIn(pan); wpn.mag = RIFLE.cap; }
    if (r.empty && !r.bolt && r.t >= def.bolt) { r.bolt = true; sfx.bolt(pan); wpn.chamber = true; wpn.mag--; }
    if (r.t >= (r.empty ? def.full.dur : def.tactical.dur)) st.reload = null;
  }

  // ----- animation -----
  const shownYaw = snap8 ? Math.round(st.yaw/(Math.PI/4))*(Math.PI/4) : st.yaw;
  soldier.root.position.copy(st.pos);
  soldier.root.rotation.y = shownYaw;
  soldier.root.updateMatrixWorld(true);
  const c = Math.cos(shownYaw), s = Math.sin(shownYaw);
  const toLocal = v => V(v.x*c - v.z*s, 0, v.x*s + v.z*c);
  const acc = dt > 0 ? st.vel.clone().sub(st.prevVel).divideScalar(dt) : V();
  rig.update(dt, {
    velLocal: toLocal(st.vel), accLocal: toLocal(acc), yawRate,
    aim: aiming, aimLocal: aiming && aimInfo.ok ? soldier.root.worldToLocal(aimPt.clone()) : null, crouch: st.crouch,
    shot: shotFlag, melee: st.melee, newMelee, impact, reload: st.reload ? { t: st.reload.t, def: RELOAD, empty: st.reload.empty } : null,
    viewDir: cam.getWorldDirection(V())
  });
  newMelee = impact = shotFlag = false;
  for (const ev of rig.state.events.splice(0)) {
    if (ev.speed > 2.2) fx.emit('dust', ev.pos, toWorldDir(ev.dir), 3, .5, .3);
    sfx.step(panOf(ev.pos), clamp(ev.speed/2, .4, 1.2));
  }

  world.update(dt, time);
  fx.update(dt);
  for (const t of world.targets) {
    if (t.clack) { sfx.clack(panOf(t.center()), clamp(t.clack/4, .3, 1)*volOf(t.center())); t.clack = 0; }
    if (t.clink) { sfx.impact('tin', panOf(t.center()), clamp(t.clink/4, .2, .8)*volOf(t.center())); t.clink = 0; }
  }

  // ----- see-through for props between the camera and the soldier -----
  const sy = Math.sin(camYaw), cy = Math.cos(camYaw);
  for (const f of world.fadeables) {
    const ox = f.x - st.pos.x, oz = f.z - st.pos.z;
    const front = ox*sy + oz*cy, lat = Math.abs(ox*cy - oz*sy);
    const want = front > .1 && front < 11 && lat < f.r + .5 ? .25 : 1;
    f.op = damp(f.op, want, 8, rawDt);
    for (const m of f.mats) { m.opacity = f.op; m.transparent = f.op < .999; m.depthWrite = f.op > .6; }
  }

  // ----- camera: follow, lead into the aim like Zomboid's aim panning, shake -----
  const lead = V(st.vel.x*.25, 0, st.vel.z*.25);
  if (aiming && aimInfo.ok && opts.pan) { const off = V(aimPt.x - st.pos.x, 0, aimPt.z - st.pos.z).multiplyScalar(.3); if (off.length() > 2.6) off.setLength(2.6); lead.add(off); }
  focus.x = damp(focus.x, st.pos.x + lead.x, 4.5, rawDt); focus.z = damp(focus.z, st.pos.z + lead.z, 4.5, rawDt);
  shake = Math.max(0, shake - rawDt*.4);
  const f2 = focus.clone(); if (shake > 0) { f2.x += (Math.random() - .5)*shake; f2.z += (Math.random() - .5)*shake; }
  placeCamera(f2);
  sun.position.set(focus.x - 6, 10, focus.z + 3); sun.target.position.set(focus.x, 0, focus.z); sun.target.updateMatrixWorld();
  rim.position.set(focus.x + 4, 5, focus.z - 6); rim.target.position.set(focus.x, 0, focus.z); rim.target.updateMatrixWorld();
  cam.updateMatrixWorld();

  // ----- hit chance, outline and reticle -----
  outlined = []; retState = null;
  if (aiming && aimInfo.ok) {
    const o = rig.state.out, half = spread(), d = o.aimDir;
    let hover = aimInfo.hit && aimInfo.hit.target, hoverChance = null;
    for (const t of world.targets) {
      const ch = hitChance(t, o.muzzle, d, half);
      if (t === hover) hoverChance = ch;
      if (ch > 0 || t === hover) { t.maskMat.color.copy(chanceColor(ch)); outlined.push(t); }
    }
    const dist = Math.max(.5, aimPt.distanceTo(o.muzzle));
    const c3 = o.muzzle.clone().addScaledVector(d, dist).project(cam);
    const pxm = innerHeight/viewH;
    retState = {
      center: { x: (c3.x + 1)/2*innerWidth, y: (1 - c3.y)/2*innerHeight },
      r: dist*Math.tan(half)*pxm,
      color: hover ? '#' + chanceColor(hoverChance).getHexString() : raised > .85 ? '#e4e0cc' : 'rgba(228,224,204,.5)',
      chance: hover ? hoverChance : null,
      rounds: (wpn.mag + (wpn.chamber ? 1 : 0)) + (st.reload ? ' ⟳' : '')
    };
    aimInfo.chance = hoverChance;
  }
  cv.style.cursor = aiming && mouse.inside ? 'none' : 'default';

  // ----- floating labels -----
  const W = innerWidth, H = innerHeight;
  for (let i = pops.length - 1; i >= 0; i--) {
    const p = pops[i]; p.t += rawDt*(slowmo ? .4 : 1);
    if (p.t > 1) { p.el.remove(); pops.splice(i, 1); continue; }
    const v = p.wp.clone(); v.y += p.t*.7; v.x += p.dx*p.t; v.project(cam);
    p.el.style.transform = `translate(${(v.x + 1)/2*W}px,${(1 - v.y)/2*H}px) translate(-50%,-50%) scale(${1 + Math.max(0, .2 - p.t)*2})`;
    p.el.style.opacity = String(1 - Math.max(0, (p.t - .55)/.45));
  }

  hud();
  const sp = Math.hypot(st.vel.x, st.vel.z);
  const act = st.reload ? RELOAD.name : st.melee ? st.melee.def.name : aiming ? (sp > .2 ? 'Шаг с прицелом' : 'Прицел') : st.crouch ? (sp > .2 ? 'Крадётся' : 'Присед') : sp > 2.2 ? 'Бег' : sp > .2 ? 'Шаг' : Math.abs(yawRate) > 1 ? 'Разворот' : 'Стойка';
  stateEl.textContent = `${act} · ${sp.toFixed(2)} м/с` + (aiming ? ` · разброс ${(spread()*180/Math.PI).toFixed(1)}°` : '') + (slowmo ? ' · ¼×' : '');
}
function toWorldDir(d) { const c = Math.cos(st.yaw), s = Math.sin(st.yaw); return V(d.x*c + d.z*s, 0, -d.x*s + d.z*c); }

resize();
placeCamera(focus);
hud(true);
let last = performance.now();
function loop(now) {
  requestAnimationFrame(loop);
  const dt = Math.min((now - last)/1000, .05); last = now;
  if (paused) return;
  update(dt);
  render();
}
requestAnimationFrame(loop);

// Hooks for the automated screenshot checks (tools/shots.cjs)
window.__game = {
  pause(on = true) { paused = on; },
  input(o) { Object.assign(debugInput, { on: true }, o); if ('crouch' in o) st.crouch = !!o.crouch; },
  aim(x, y, z) { debugAim = x === false || x == null ? null : V(x, y, z); },
  trigger(on = true) { if (on) { wpn.held = true; wpn.pressed = true; wpn.dryDone = false; } else wpn.held = false; },
  fire() { wpn.pressed = true; wpn.dryDone = false; },
  mode(i) { wpn.mode = i % MODES.length; hud(true); },
  reload() { startReload(); },
  attack() { startMelee(); },
  crouch(on = true) { st.crouch = !!on; },
  option(name, v) { opts[name] = v; },
  step(seconds, fps = 60) { for (let i = 0; i < Math.round(seconds*fps); i++) update(1/fps); render(); },
  view(yawDeg, z = zoom) { camYaw = camYawT = yawDeg*Math.PI/180; zoom = z; resize(); placeCamera(focus); render(); },
  place(x, z, yawDeg) { st.pos.set(x, 0, z); st.vel.set(0, 0, 0); st.yaw = st.yawT = yawDeg*Math.PI/180; focus.set(x, .9, z); },
  render,
  // aim line and what it hits, plus every target's hit chance, for numeric checks
  probe() {
    const o = rig.state.out, h = world.trace(o.muzzle, o.aimDir, 200), half = spread();
    return { muzzle: o.muzzle.toArray().map(v => +v.toFixed(3)), dir: o.aimDir.toArray().map(v => +v.toFixed(3)), spreadDeg: +(half*180/Math.PI).toFixed(2),
      hit: h && { t: +h.t.toFixed(2), kind: h.kind, target: h.target && h.target.type, point: h.point.toArray().map(v => +v.toFixed(2)) },
      chances: world.targets.map(t => +hitChance(t, o.muzzle, o.aimDir, half).toFixed(2)) };
  },
  get state() { return { pos: st.pos.toArray(), yaw: st.yaw, aim: rig.state.aim, mag: wpn.mag, chamber: wpn.chamber, shots: wpn.shots, hits: wpn.hits, reload: st.reload && st.reload.t, melee: st.melee && st.melee.def.name }; },
  stats: { triangles: soldier.triangles },
  scene
};
