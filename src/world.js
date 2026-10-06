import * as THREE from 'three';
import { makeMaterial, makeTexture } from './knight-model.js';

// The yard: a ruined training court in the knight's own palette — worn olive grass,
// grey-green flagstones, oak and straw — built from the same 64px textures as the armour.

function rng(seed) { return () => { seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }
const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
const clamp = THREE.MathUtils.clamp;
export const BG = 0x222823;

export function buildWorld(scene, knight) {
  const R = rng(1337);
  const colliders = [], fadeables = [], dummies = [], torches = [], banners = [];
  const circ = (x, z, r) => colliders.push({ ax: x, az: z, bx: x, bz: z, r });
  const seg = (ax, az, bx, bz, r) => colliders.push({ ax, az, bx, bz, r });
  const mesh = (geo, mat, x=0, y=0, z=0, parent = scene) => { const m = new THREE.Mesh(geo, mat); m.position.set(x, y, z); m.castShadow = m.receiveShadow = true; parent.add(m); return m; };
  const grp = (x=0, y=0, z=0, parent = scene) => { const g = new THREE.Group(); g.position.set(x, y, z); parent.add(g); return g; };
  const inCourt = (x, z) => x > -5.05 && x < 5.05 && z > -5.05 && z < 5.05;
  const onPath = (x, z) => (Math.abs(z + .1 + Math.sin(x*.7)*.25) < .75 && x > 4.6) || (Math.abs(x - .3 - Math.sin(z*.6)*.3) < .7 && z > 4.6);

  const M = {
    oak: makeMaterial('Oak planks', '#7a5d3e', 'planks', 0, 31),
    oakDark: makeMaterial('Dark oak', '#4e3b29', 'planks', 0, 32),
    iron: makeMaterial('Black iron', '#4a504c', 'steel', .45, 33),
    straw: makeMaterial('Straw', '#b39a5c', 'straw', 0, 34),
    burlap: makeMaterial('Burlap', '#9c8a62', 'burlap', 0, 35),
    rope: makeMaterial('Rope', '#6f5d3f', 'cloth', 0, 36),
    bark: makeMaterial('Bark', '#4a3d2e', 'planks', 0, 37),
    rock: makeMaterial('Field stone', '#6c6e62', 'plain', 0, 38),
    soil: makeMaterial('Packed soil', '#3c3a2c', 'plain', 0, 39),
    dirt: makeMaterial('Path dirt', '#6d5f45', 'plain', 0, 40),
    leaf: ['#56603a', '#4b5634', '#626b42'].map((c, i) => makeMaterial('Leaves', c, 'leaves', 0, 41 + i))
  };

  // ---------- ground: tiled grass texture, macro tint baked into vertex colours ----------
  {
    const N = 48, geo = new THREE.PlaneGeometry(N, N, N, N); geo.rotateX(-Math.PI/2);
    const tex = makeTexture('#9aa172', 'leaves', 50); tex.wrapS = tex.wrapT = THREE.RepeatWrapping; tex.repeat.set(N/1.5, N/1.5);
    const pos = geo.attributes.position, col = [], bg = new THREE.Color(BG), c = new THREE.Color();
    for (let i = 0; i < pos.count; i++) {
      const x = pos.getX(i), z = pos.getZ(i), r = Math.hypot(x, z);
      const n = Math.sin(x*.55 + Math.sin(z*.4)*2)*.5 + Math.sin(z*.73 - x*.21)*.5;
      c.setRGB(.46 + n*.05, .5 + n*.045, .37 + n*.035);
      c.lerp(bg, THREE.MathUtils.smoothstep(r, 12, 20));
      col.push(c.r, c.g, c.b);
    }
    geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
    const g = new THREE.Mesh(geo, new THREE.MeshStandardMaterial({ map: tex, vertexColors: true, roughness: 1 }));
    g.receiveShadow = true; scene.add(g);
  }
  // soil under the flagstones
  { const s = mesh(new THREE.PlaneGeometry(10.2, 10.2), M.soil, 0, .003, 0); s.rotation.x = -Math.PI/2; s.castShadow = false; M.soil.map.wrapS = M.soil.map.wrapT = THREE.RepeatWrapping; M.soil.map.repeat.set(6, 6); }
  // dirt paths leaving the court through the open sides
  {
    M.dirt.map.wrapS = M.dirt.map.wrapT = THREE.RepeatWrapping; M.dirt.map.repeat.set(1, 1);
    for (let i = 0; i < 26; i++) {
      const alongX = i < 13, t = 5.2 + (i % 13)*.75;
      const x = alongX ? t : .3 + Math.sin(t*.6)*.3, z = alongX ? -.1 - Math.sin(t*.7)*.25 : t;
      const p = mesh(new THREE.CircleGeometry(.8 + R()*.2, 7), M.dirt, x, .004 + i*.0001, z);
      p.rotation.x = -Math.PI/2; p.rotation.z = R()*6; p.scale.set(1, .85, 1); p.castShadow = false;
    }
  }

  // ---------- flagstones (one instanced draw) ----------
  {
    const stones = [], palette = [0x656858, 0x5a6252, 0x70715f, 0x595b4e, 0x6e6c5b].map(c => new THREE.Color(c));
    for (let z = -4.75; z < 5; z += .5) {
      let x = -5 + (Math.round((z + 4.75)/.5) % 2 ? .22 : 0) + R()*.1;
      while (x < 4.98) {
        const w = Math.min(.42 + R()*.32, 5 - x); if (w < .18) break;
        const edge = Math.min(5 - Math.abs(x + w/2), 5 - Math.abs(z));
        if (R() > (edge < .8 ? .2 : .035)) stones.push([x + w/2, z, w - .035, .465, .012 + R()*.013, (R() - .5)*.05]);
        x += w;
      }
    }
    const tex = makeMaterial('Flagstone', '#e9e7da', 'plain', 0, 52);
    const im = new THREE.InstancedMesh(new THREE.BoxGeometry(1, 1, 1), tex, stones.length);
    const m4 = new THREE.Matrix4(), q = new THREE.Quaternion(), e = new THREE.Euler();
    stones.forEach(([x, z, w, d, top, rot], i) => {
      q.setFromEuler(e.set((R() - .5)*.03, rot, (R() - .5)*.03));
      m4.compose(V(x, top - .06, z), q, V(w, .12, d)); im.setMatrixAt(i, m4);
      im.setColorAt(i, palette[(R()*palette.length) | 0].clone().multiplyScalar(.95 + R()*.12));
    });
    im.castShadow = false; im.receiveShadow = true; scene.add(im);
  }

  // ---------- ruined walls from individual blocks, chunked so they can fade ----------
  function wall(ax, az, bx, bz, profile, gap) {
    const len = Math.hypot(bx - ax, bz - az), dx = (bx - ax)/len, dz = (bz - az)/len, rot = Math.atan2(-dz, dx);
    const palette = [0x6a6b5d, 0x5f6254, 0x74725f, 0x585b4f].map(c => new THREE.Color(c));
    const geo = new THREE.BoxGeometry(1, 1, 1);
    for (let c0 = 0; c0 < len; c0 += 2) {
      const blocks = [];
      for (let course = 0; course < 8; course++) {
        let s = c0 + (course % 2 ? .27 : 0);
        if (course % 2 && c0 === 0 && (course + .6)*.23 < profile(.135)) blocks.push([.135, course, .24]);
        while (s < Math.min(c0 + 2, len)) {
          const w = Math.min(.48 + R()*.12, Math.min(c0 + 2, len) - s); if (w < .1) break;
          const mid = s + w/2, h = profile(mid);
          if ((course + .6)*.23 < h && !(gap && mid > gap[0] && mid < gap[1])) blocks.push([mid, course, w - .03]);
          s += w;
        }
      }
      if (!blocks.length) continue;
      const mat = makeMaterial('Wall stone', '#e4e1d4', 'plain', 0, 60 + c0*7 + Math.round(ax*3));
      const im = new THREE.InstancedMesh(geo, mat, blocks.length), m4 = new THREE.Matrix4(), q = new THREE.Quaternion();
      blocks.forEach(([mid, course, w], i) => {
        const j = (R() - .5)*.03;
        q.setFromEuler(new THREE.Euler(0, rot + (R() - .5)*.06, 0));
        m4.compose(V(ax + dx*mid + dz*j, course*.23 + .115, az + dz*mid - dx*j), q, V(w, .215, .32 + (R() - .5)*.04));
        im.setMatrixAt(i, m4);
        const col = palette[(R()*palette.length) | 0].clone().multiplyScalar(.9 + R()*.2);
        if (course >= 3 && R() < .25) col.lerp(new THREE.Color(0x58663a), .45);   // moss on the upper courses
        im.setColorAt(i, col);
      });
      im.castShadow = im.receiveShadow = true; scene.add(im);
      const cx = ax + dx*(c0 + 1), cz = az + dz*(c0 + 1);
      fadeables.push({ x: cx, z: cz, mats: [mat], op: 1, r: 1.3 });
    }
  }
  const noise = s => Math.sin(s*1.7) + Math.sin(s*3.1 + 1)*.5;
  wall(-5.4, -5.25, 5.4, -5.25, s => s > 2.4 && s < 3.6 || s > 7.6 && s < 8.6 ? 1.75 : 1.15 + noise(s)*.35, null);
  wall(-5.25, -5.4, -5.25, 5.4, s => s > 9 ? .5 : 1.05 + noise(s + 4)*.4, [6.3, 7.5]);
  seg(-5.6, -5.25, 5.6, -5.25, .17); seg(-5.25, -5.6, -5.25, .95, .17); seg(-5.25, 2.15, -5.25, 5.6, .17);
  // corner pillar
  { const p = mesh(new THREE.BoxGeometry(.5, 2.1, .5), makeMaterial('Pillar', '#6b6c5e', 'plain', 0, 70), -5.25, 1.05, -5.25); }

  // ---------- torches on the tall pieces of the back wall ----------
  function torch(x, z) {
    mesh(new THREE.BoxGeometry(.05, .05, .24), M.iron, x, 1.3, z + .27);
    mesh(new THREE.CylinderGeometry(.035, .025, .2, 6), M.oakDark, x, 1.38, z + .38);
    const f = new THREE.Mesh(new THREE.IcosahedronGeometry(.065, 0), new THREE.MeshBasicMaterial({ color: 0xffa040 }));
    f.position.set(x, 1.52, z + .38); scene.add(f);
    const core = new THREE.Mesh(new THREE.IcosahedronGeometry(.035, 0), new THREE.MeshBasicMaterial({ color: 0xfff0b0 }));
    core.position.set(x, 1.5, z + .39); scene.add(core);
    const l = new THREE.PointLight(0xff9a40, 7, 7, 2); l.position.set(x, 1.6, z + .7); scene.add(l);
    torches.push({ l, f, core, seed: R()*10 });
  }
  torch(-2.25, -5.25); torch(2.75, -5.25);

  // ---------- props ----------
  function crate(x, z, s, r, y = 0) {
    const g = grp(x, y, z); g.rotation.y = r;
    mesh(new THREE.BoxGeometry(s, s, s), M.oak, 0, s/2, 0, g);
    for (const yy of [.06, s - .06]) mesh(new THREE.BoxGeometry(s + .02, .06, s + .02), M.oakDark, 0, yy, 0, g);
    mesh(new THREE.BoxGeometry(.06, s*.95, s + .02), M.oakDark, 0, s/2, 0, g).rotation.x = Math.PI/4;
    return g;
  }
  crate(-4.45, -4.45, .62, .12); crate(-3.75, -4.5, .5, -.2); crate(-4.4, -4.42, .46, .5, .62);
  circ(-4.2, -4.45, .55);
  function barrel(x, z) {
    const g = grp(x, 0, z), pts = [];
    for (let i = 0; i <= 6; i++) { const t = i/6; pts.push(new THREE.Vector2(.24 + Math.sin(Math.PI*t)*.05, t*.66)); }
    mesh(new THREE.LatheGeometry(pts, 10), M.oak, 0, 0, 0, g);
    mesh(new THREE.CircleGeometry(.24, 10), M.oakDark, 0, .66, 0, g).rotation.x = -Math.PI/2;
    for (const y of [.1, .33, .56]) mesh(new THREE.CylinderGeometry(.29 - Math.abs(y - .33)*.12, .29 - Math.abs(y - .33)*.12, .035, 10), M.iron, 0, y, 0, g);
    circ(x, z, .3);
  }
  barrel(-4.5, 2.75); barrel(-4.55, 3.45); barrel(-3.9, 3.15);
  function bale(x, z, r, y = 0) {
    const g = grp(x, y, z); g.rotation.y = r;
    mesh(new THREE.BoxGeometry(.9, .44, .5), M.straw, 0, .22, 0, g);
    for (const xx of [-.24, .24]) mesh(new THREE.BoxGeometry(.03, .45, .51), M.rope, xx, .22, 0, g);
    if (!y) circ(x, z, .42);
  }
  bale(3.75, 1.3, .3); bale(4.05, 2.25, -.15); bale(3.9, 1.75, .1, .44);
  { // weapon rack
    const g = grp(1.2, 0, -4.75);
    for (const x of [-.55, .55]) mesh(new THREE.BoxGeometry(.08, 1.05, .08), M.oakDark, x, .52, 0, g);
    mesh(new THREE.BoxGeometry(1.24, .07, .08), M.oakDark, 0, .95, 0, g); mesh(new THREE.BoxGeometry(1.24, .07, .08), M.oakDark, 0, .16, .1, g);
    for (const x of [-.34, -.02, .3]) {
      const s = grp(x, 0, .14, g); s.rotation.x = -.17; s.rotation.z = (R() - .5)*.05;
      mesh(new THREE.CylinderGeometry(.018, .02, 1.6, 5), M.oak, 0, .8, 0, s);
      mesh(new THREE.ConeGeometry(.035, .18, 4), M.iron, 0, 1.69, 0, s);
    }
    seg(.55, -4.75, 1.85, -4.75, .2);
  }
  { // banner on a pole, dyed with the knight's own cloth texture
    const pole = grp(-3.7, 0, -1.2);
    mesh(new THREE.CylinderGeometry(.04, .05, 2.6, 6), M.oakDark, 0, 1.3, 0, pole);
    mesh(new THREE.CylinderGeometry(.018, .018, .75, 5), M.oakDark, .36, 2.45, 0, pole).rotation.z = Math.PI/2;
    mesh(new THREE.OctahedronGeometry(.06, 0), knight.materials.brass, 0, 2.66, 0, pole);
    const geo = new THREE.PlaneGeometry(.68, 1.15, 6, 10); geo.translate(.36, -.575, 0);
    const mat = new THREE.MeshStandardMaterial({ map: knight.materials.cloth.map, side: THREE.DoubleSide, roughness: 1, flatShading: true });
    const cloth = mesh(geo, mat, 0, 2.43, .0, pole);
    const crossMat = new THREE.MeshStandardMaterial({ map: knight.materials.linen.map, side: THREE.DoubleSide, roughness: 1 });
    const crossV = mesh(new THREE.PlaneGeometry(.1, .7), crossMat, .36, 1.9, .012, pole), crossH = mesh(new THREE.PlaneGeometry(.46, .1), crossMat, .36, 2.05, .012, pole);
    banners.push({ geo, base: geo.attributes.position.array.slice(), parts: [crossV, crossH] });
    circ(-3.7, -1.2, .12);
  }

  // ---------- training dummies: pendulum springs with free-spinning arms ----------
  function dummy(x, z, yaw) {
    const base = grp(x, 0, z); base.rotation.y = yaw;
    mesh(new THREE.BoxGeometry(.75, .09, .14), M.oakDark, 0, .045, 0, base); mesh(new THREE.BoxGeometry(.14, .09, .75), M.oakDark, 0, .045, 0, base);
    const piv = grp(0, .09, 0, base);
    mesh(new THREE.CylinderGeometry(.05, .06, 1.4, 6), M.oak, 0, .7, 0, piv);
    const spin = grp(0, 0, 0, piv);
    const sack = [[.0, 0], [.17, .02], [.22, .2], [.21, .45], [.15, .6], [.0, .62]].map(([r, y]) => new THREE.Vector2(r, y));
    mesh(new THREE.LatheGeometry(sack, 8), M.burlap, 0, .72, 0, spin);
    for (const y of [.8, 1.12]) mesh(new THREE.CylinderGeometry(.225, .225, .035, 8), M.rope, 0, y, 0, spin);
    const bar = mesh(new THREE.CylinderGeometry(.035, .035, 1.0, 5), M.oak, 0, 1.24, 0, spin); bar.rotation.z = Math.PI/2;
    for (const s of [-1, 1]) mesh(new THREE.ConeGeometry(.06, .16, 5), M.straw, s*.55, 1.24, 0, spin).rotation.z = -s*Math.PI/2;
    mesh(new THREE.IcosahedronGeometry(.15, 0), M.burlap, 0, 1.5, 0, spin);
    mesh(new THREE.BoxGeometry(.13, .025, .02), knight.materials.dark, 0, 1.53, .14, spin).rotation.z = .5;
    mesh(new THREE.BoxGeometry(.13, .025, .02), knight.materials.dark, 0, 1.53, .14, spin).rotation.z = -.5;
    for (let i = 0; i < 5; i++) mesh(new THREE.ConeGeometry(.02, .14, 3), M.straw, (R() - .5)*.12, 1.63, (R() - .5)*.12, spin).rotation.set((R() - .5)*1.2, 0, (R() - .5)*1.2);
    circ(x, z, .3);
    dummies.push({ x, z, piv, spin, tilt: new THREE.Vector2(), tv: new THREE.Vector2(), yaw: 0, yv: 0, hits: 0 });
  }
  dummy(1.8, -1.5, .3); dummy(-1.8, -2.6, -.4); dummy(2.6, -3.7, .9);

  // ---------- trees, bushes, rocks, grass ----------
  const trees = [];
  function tree(x, z, s) {
    const g = grp(x, 0, z); g.rotation.y = R()*6;
    mesh(new THREE.CylinderGeometry(.07*s, .13*s, 1.5*s, 6), M.bark, 0, .75*s, 0, g);
    const b = mesh(new THREE.CylinderGeometry(.03*s, .05*s, .6*s, 5), M.bark, .18*s, 1.25*s, 0, g); b.rotation.z = -.7;
    const mats = M.leaf.map(m => { const c = m.clone(); c.transparent = true; return c; });
    [[0, 1.65, 0, .78], [.32, 2.05, .12, .62], [-.26, 2.3, -.14, .52], [.05, 2.62, .05, .4], [-.35, 1.8, .25, .45]].forEach(([dx, y, dz, r], i) => {
      mesh(new THREE.IcosahedronGeometry(r*s, 0), mats[(i + ((R()*3) | 0)) % 3], dx*s, y*s, dz*s, g).rotation.set(R()*3, R()*3, 0);
    });
    circ(x, z, .2*s); fadeables.push({ x, z, mats, op: 1, r: 1.6 });
  }
  for (let n = 0; n < 500 && trees.length < 34; n++) {
    const a = R()*Math.PI*2, d = 7 + R()*10, x = Math.cos(a)*d, z = Math.sin(a)*d;
    if (onPath(x, z) || (Math.abs(z) < 2 && x > 4) || (Math.abs(x) < 2 && z > 4)) continue;
    if (x > -1 && z > -1 && d < 11.5) continue;   // keep the default camera's line of sight into the court clear
    if (trees.some(t => Math.hypot(t[0] - x, t[1] - z) < 1.9)) continue;
    trees.push([x, z]); tree(x, z, .85 + R()*.5);
  }
  for (let i = 0; i < 26; i++) {
    const a = R()*Math.PI*2, d = 6.2 + R()*9, x = Math.cos(a)*d, z = Math.sin(a)*d;
    if (onPath(x, z)) continue;
    const g = grp(x, 0, z), m = M.leaf[i % 3];
    for (let k = 0; k < 3; k++) mesh(new THREE.IcosahedronGeometry(.2 + R()*.16, 0), m, (R() - .5)*.5, .16, (R() - .5)*.5, g).rotation.set(R()*3, R()*3, 0);
  }
  for (let i = 0; i < 18; i++) {
    const a = R()*Math.PI*2, d = 5.7 + R()*9;
    mesh(new THREE.DodecahedronGeometry(.07 + R()*.15, 0), M.rock, Math.cos(a)*d, .04, Math.sin(a)*d).rotation.set(R()*3, R()*3, R()*3);
  }
  {
    const blade = new THREE.ConeGeometry(.018, .16, 3); blade.translate(0, .08, 0);
    const mat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 1, flatShading: true });
    const N = 3200, im = new THREE.InstancedMesh(blade, mat, N), m4 = new THREE.Matrix4(), q = new THREE.Quaternion(), e = new THREE.Euler();
    const cols = [0x6d7448, 0x5e6a3c, 0x7c8150, 0x8a8a58].map(c => new THREE.Color(c));
    let n = 0;
    while (n < N) {
      const x = (R() - .5)*30, z = (R() - .5)*30, r = Math.hypot(x, z);
      if (r > 15 || onPath(x, z)) continue;
      // inside the court only along the broken edges and in cracks
      if (inCourt(x, z) && Math.min(5 - Math.abs(x), 5 - Math.abs(z)) > .6 && R() > .03) continue;
      const tuft = 2 + ((R()*3) | 0);
      for (let k = 0; k < tuft && n < N; k++, n++) {
        q.setFromEuler(e.set((R() - .5)*.7, R()*6, (R() - .5)*.7));
        const s = .6 + R()*.8;
        m4.compose(V(x + (R() - .5)*.08, 0, z + (R() - .5)*.08), q, V(s, s*(.7 + R()*.6), s)); im.setMatrixAt(n, m4);
        im.setColorAt(n, cols[(R()*cols.length) | 0]);
      }
    }
    im.receiveShadow = true; scene.add(im);
  }

  // ---------- per-frame ----------
  const _m = new THREE.Matrix4(), _q = new THREE.Quaternion(), _qs = new THREE.Quaternion(), _ax = V();
  function update(dt, time) {
    for (const d of dummies) {
      // 2-D tilt pendulum: spring back to upright with damping, arms spin on friction only
      d.tv.x += (-48*d.tilt.x - 4*d.tv.x)*dt; d.tv.y += (-48*d.tilt.y - 4*d.tv.y)*dt;
      d.tilt.x += d.tv.x*dt; d.tilt.y += d.tv.y*dt;
      d.yv *= Math.exp(-1.8*dt); d.yaw += d.yv*dt;
      const a = d.tilt.length();
      if (a > 1e-5) { _ax.set(d.tilt.y, 0, -d.tilt.x).divideScalar(a); d.piv.quaternion.setFromAxisAngle(_ax, a); } else d.piv.quaternion.identity();
      d.spin.rotation.y = d.yaw;
    }
    for (const t of torches) {
      const f = Math.sin(time*13 + t.seed)*.5 + Math.sin(time*31 + t.seed*2)*.3 + Math.sin(time*7.3)*.2;
      t.l.intensity = 7 + f*1.6; t.f.scale.setScalar(1 + f*.14); t.core.scale.setScalar(1 + f*.2);
    }
    for (const b of banners) {
      const p = b.geo.attributes.position;
      for (let i = 0; i < p.count; i++) {
        const x = b.base[i*3], y = b.base[i*3 + 1], w = Math.max(0, x - .02);
        p.setZ(i, Math.sin(time*2.6 - x*5 + y*1.3)*.06*w*2 + Math.sin(time*4.1 - x*9)*.015*w*2);
      }
      p.needsUpdate = true; b.geo.computeVertexNormals();
      const z = Math.sin(time*2.6 - .36*5 - 1.9*1.3 + .5)*.05;
      b.parts.forEach(m => m.position.z = .012 + z);
    }
  }
  // impulse from a sword cut: the dummy leans away along the blade's path and its arms spin
  function hit(d, swingDir, from, strength = 1) {
    const rx = d.x - from.x, rz = d.z - from.z, rl = Math.hypot(rx, rz) || 1;
    const ix = swingDir.x*.75 + rx/rl*.55, iz = swingDir.z*.75 + rz/rl*.55;
    d.tv.x += ix*3.4*strength; d.tv.y += iz*3.4*strength;
    // spin sign from the cut's tangential direction around the post
    d.yv += (rx*swingDir.z - rz*swingDir.x)/rl * -9*strength;
    d.hits++;
    return V(d.x - rx/rl*.22, 1.05, d.z - rz/rl*.22);
  }
  return { colliders, fadeables, dummies, update, hit, M };
}
