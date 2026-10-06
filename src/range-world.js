import * as THREE from 'three';
import { makeMaterial, makeTexture } from './textures.js';

// Knox County firing range, summer 1993: a gravel firing line, pop-up silhouettes behind sandbags,
// steel gongs and tin cans downrange, an earth backstop, chain-link fence and a guard tower.
// Same construction rules as the knight's yard: 64px textures, nearest filtering, flat shading.
// Shooting goes through trace(): static props are raycast, moving targets are tested analytically.

function rng(seed) { return () => { seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }
const V = (x=0, y=0, z=0) => new THREE.Vector3(x, y, z);
const clamp = THREE.MathUtils.clamp;
export const BG = 0x20251f;
export const START = { x: .2, z: 2.7, yaw: Math.PI };

// E-type "Ivan" silhouette, metres from the hinge.
const IVAN = [[-.25,0],[.25,0],[.25,.52],[.21,.6],[.12,.645],[.075,.67],[.07,.7],[.09,.74],[.095,.82],[.08,.89],[.045,.93],[0,.94],[-.045,.93],[-.08,.89],[-.095,.82],[-.09,.74],[-.07,.7],[-.075,.67],[-.12,.645],[-.21,.6],[-.25,.52]];
function inPoly(x, y, poly) {
  let c = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, yi] = poly[i], [xj, yj] = poly[j];
    if ((yi > y) !== (yj > y) && x < (xj - xi)*(y - yi)/(yj - yi) + xi) c = !c;
  }
  return c;
}

export function buildRange(scene) {
  const R = rng(1993);
  const colliders = [], fadeables = [], solids = [], targets = [], flags = [], supports = [];
  const circ = (x, z, r) => colliders.push({ ax: x, az: z, bx: x, bz: z, r });
  const seg = (ax, az, bx, bz, r) => colliders.push({ ax, az, bx, bz, r });
  const mesh = (geo, mat, x=0, y=0, z=0, parent = scene) => { const m = new THREE.Mesh(geo, mat); m.position.set(x, y, z); m.castShadow = m.receiveShadow = true; parent.add(m); return m; };
  const grp = (x=0, y=0, z=0, parent = scene) => { const g = new THREE.Group(); g.position.set(x, y, z); parent.add(g); return g; };
  const solid = (m, kind) => { m.userData.kind = kind; solids.push(m); return m; };
  const onRoad = (x, z) => z > 7.6 && z < 11.8;

  const M = {
    oak: makeMaterial('Weathered planks', '#7a6447', 'planks', 0, 31),
    oakDark: makeMaterial('Dark timber', '#4e3d2b', 'planks', 0, 32),
    olive: makeMaterial('Olive drab steel', '#4b5034', 'steel', .35, 33),
    oliveWood: makeMaterial('Olive crate', '#535a37', 'ammo', 0, 34),
    sand: makeMaterial('Sandbag', '#a39470', 'burlap', 0, 35),
    rope: makeMaterial('Rope', '#6f5d3f', 'cloth', 0, 36),
    bark: makeMaterial('Bark', '#4a3d2e', 'planks', 0, 37),
    rock: makeMaterial('Field stone', '#6c6e62', 'plain', 0, 38),
    dirt: makeMaterial('Berm earth', '#6b5a40', 'gravel', 0, 39),
    gravel: makeMaterial('Gravel', '#77705f', 'gravel', 0, 40),
    concrete: makeMaterial('Concrete', '#8f8d82', 'concrete', 0, 41),
    canvas: makeMaterial('Tent canvas', '#5c5e3e', 'cloth', 0, 42),
    tin: makeMaterial('Corrugated tin', '#7c817a', 'corrugated', .4, 43),
    target: makeMaterial('Target olive', '#4f5b31', 'plywood', 0, 44),
    white: makeMaterial('Gong paint', '#d9d6c8', 'plain', .2, 45),
    pipe: makeMaterial('Pipe', '#3f4441', 'steel', .45, 46),
    asphalt: makeMaterial('Asphalt', '#3b3d3a', 'asphalt', 0, 47),
    paint: makeMaterial('Road paint', '#c9a94a', 'plain', 0, 48),
    signWhite: makeMaterial('Sign', '#d8d4c4', 'plain', 0, 49),
    red: makeMaterial('Bravo red', '#9b3328', 'cloth', 0, 50),
    leaf: ['#56603a', '#4b5634', '#626b42'].map((c, i) => makeMaterial('Leaves', c, 'leaves', 0, 51 + i))
  };
  const tile = (mat, n) => { mat.map.wrapS = mat.map.wrapT = THREE.RepeatWrapping; mat.map.repeat.set(n, n); return mat; };

  // ---------- ground: grass with macro tint, gravel firing line, road ----------
  {
    const N = 52, geo = new THREE.PlaneGeometry(N, N, N, N); geo.rotateX(-Math.PI/2);
    const tex = makeTexture('#9aa172', 'leaves', 50); tex.wrapS = tex.wrapT = THREE.RepeatWrapping; tex.repeat.set(N/1.5, N/1.5);
    const pos = geo.attributes.position, col = [], bg = new THREE.Color(BG), c = new THREE.Color();
    for (let i = 0; i < pos.count; i++) {
      const x = pos.getX(i), z = pos.getZ(i), r = Math.hypot(x, z + 2);
      const n = Math.sin(x*.55 + Math.sin(z*.4)*2)*.5 + Math.sin(z*.73 - x*.21)*.5;
      c.setRGB(.47 + n*.05, .5 + n*.045, .36 + n*.035);
      if (z < 0 && z > -13 && Math.abs(x) < 8) c.multiplyScalar(1.05);    // mown lanes
      c.lerp(bg, THREE.MathUtils.smoothstep(r, 15, 23));
      col.push(c.r, c.g, c.b);
    }
    geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
    const g = new THREE.Mesh(geo, new THREE.MeshStandardMaterial({ map: tex, vertexColors: true, roughness: 1 }));
    g.receiveShadow = true; scene.add(g);
  }
  const flat = (w, d, mat, x, z, y = .004, rot = 0) => { const p = mesh(new THREE.PlaneGeometry(w, d), mat, x, y, z); p.rotation.set(-Math.PI/2, 0, rot); p.castShadow = false; return p; };
  tile(M.gravel, 1); flat(15, 3.4, M.gravel, 0, 2.6).material.map.repeat.set(5, 1.2);
  { const line = flat(14.6, .07, M.signWhite, 0, 1.0, .006); line.material = M.signWhite; }
  for (let i = 0; i < 18; i++) {   // worn dirt where people stood and around the target pits
    const p = mesh(new THREE.CircleGeometry(.5 + R()*.5, 7), M.dirt, (R() - .5)*13, .005 + i*.0001, i < 8 ? 2 + R()*1.6 : -2 - R()*9);
    p.rotation.x = -Math.PI/2; p.rotation.z = R()*6; p.scale.set(1, .7, 1); p.castShadow = false;
  }
  tile(M.asphalt, 1); flat(52, 3.2, M.asphalt, 0, 9.6, .005).material.map.repeat.set(16, 1);
  for (let x = -25; x < 25; x += 2.4) flat(1.2, .1, M.paint, x, 9.6, .007);
  flat(52, .08, M.signWhite, 0, 8.15, .007); flat(52, .08, M.signWhite, 0, 11.05, .007);

  // ---------- sandbags: one instanced, pillow-shaped bag per course position ----------
  // a filled bag: a superellipsoid, flat on top and bottom, pinched where the seams are
  const bagGeo = (() => {
    const g = new THREE.SphereGeometry(.5, 10, 6), p = g.attributes.position;
    for (let i = 0; i < p.count; i++) {
      const x = p.getX(i)*2, y = p.getY(i)*2, z = p.getZ(i)*2, sg = (v, e) => Math.sign(v)*Math.abs(v)**e;
      p.setXYZ(i, sg(x, .45)*.5*(1 - .12*y*y), sg(y, .8)*.5, sg(z, .55)*.5*(1 - .2*y*y));
    }
    g.computeVertexNormals(); return g;
  })();
  const bagCols = [0xffffff, 0xeee6d4, 0xf6efe0, 0xc4cba0, 0xd9d2bd].map(c => new THREE.Color(c));   // tints over the burlap texture
  function sandbags(ax, az, bx, bz, courses, fade) {
    const len = Math.hypot(bx - ax, bz - az), dx = (bx - ax)/len, dz = (bz - az)/len, rot = Math.atan2(-dz, dx);
    const bags = [];
    for (let c = 0; c < courses; c++) {
      for (let s = (c % 2 ? .25 : 0); s < len - .1; s += .5) bags.push([s + .25, c, (R() - .5)*.12]);
    }
    const mat = M.sand, im = new THREE.InstancedMesh(bagGeo, mat, bags.length), m4 = new THREE.Matrix4(), q = new THREE.Quaternion();
    bags.forEach(([s, c, r], i) => {
      const ss = Math.min(s, len - .22);
      q.setFromEuler(new THREE.Euler((R() - .5)*.06, rot + r, (R() - .5)*.06));
      m4.compose(V(ax + dx*ss + (R() - .5)*.03, .075 + c*.14, az + dz*ss + (R() - .5)*.03), q, V(.47, .17, .3)); im.setMatrixAt(i, m4);
      im.setColorAt(i, bagCols[(R()*bagCols.length) | 0]);
    });
    im.castShadow = im.receiveShadow = true; scene.add(im); solid(im, 'sand');
    seg(ax, az, bx, bz, .2);
    if (fade) fadeables.push({ x: (ax + bx)/2, z: (az + bz)/2, mats: [mat], op: 1, r: len/2, shared: true });
    return im;
  }
  sandbags(-5.2, .55, -2.6, .55, 3); sandbags(2.6, .55, 5.2, .55, 3);
  sandbags(-7.2, -4.4, -5.6, -4.4, 4); sandbags(-7.2, -4.4, -7.2, -6.2, 4); sandbags(-7.2, -6.2, -5.8, -6.2, 4);

  // ---------- jersey barriers, ammo crates, drums ----------
  const barrierGeo = (() => {
    const shape = new THREE.Shape([[-.3,0],[.3,0],[.3,.08],[.2,.26],[.1,.8],[-.1,.8],[-.2,.26],[-.3,.08]].map(p => new THREE.Vector2(...p)));
    const g = new THREE.ExtrudeGeometry(shape, { depth: 2.6, bevelEnabled: true, bevelThickness: .02, bevelSize: .015, bevelSegments: 1 });
    g.translate(0, 0, -1.3); return g;
  })();
  function barrier(x, z, yaw) {
    const b = solid(mesh(barrierGeo, M.concrete, x, 0, z), 'concrete'); b.rotation.y = yaw;
    for (const s of [-1, 1]) { const h = mesh(new THREE.BoxGeometry(.62, .05, .1), M.pipe, 0, .06, s*.9, b); h.castShadow = false; }
    const c = Math.cos(yaw), sn = Math.sin(yaw); seg(x - sn*1.3, z - c*1.3, x + sn*1.3, z + c*1.3, .3);
  }
  barrier(-4.6, 6.6, Math.PI/2); barrier(-1.9, 6.6, Math.PI/2); barrier(2.4, 6.6, Math.PI/2); barrier(5.1, 6.6, Math.PI/2);
  barrier(8.2, -1.5, .08);
  function crate(x, z, r, y = 0) {
    const g = grp(x, y, z); g.rotation.y = r;
    solid(mesh(new THREE.BoxGeometry(.72, .3, .3), M.oliveWood, 0, .15, 0, g), 'wood');
    mesh(new THREE.BoxGeometry(.74, .05, .32), M.olive, 0, .3, 0, g);
    for (const s of [-1, 1]) mesh(new THREE.BoxGeometry(.04, .04, .1), M.rope, s*.38, .18, 0, g);
    return g;
  }
  crate(-5.4, 2.7, .1); crate(-5.35, 3.15, -.05); crate(-5.38, 2.92, .2, .32); crate(-4.6, 3.4, 1.2);
  circ(-5.35, 2.95, .55); circ(-4.6, 3.4, .35);
  function drum(x, z, tilt = 0) {
    const g = grp(x, 0, z); g.rotation.z = tilt;
    solid(mesh(new THREE.CylinderGeometry(.29, .29, .88, 12), M.olive, 0, .44, 0, g), 'metal');
    for (const y of [.29, .59]) mesh(new THREE.CylinderGeometry(.3, .3, .03, 12), M.pipe, 0, y, 0, g);
    mesh(new THREE.CylinderGeometry(.27, .27, .01, 12), M.pipe, 0, .885, 0, g);
    mesh(new THREE.CylinderGeometry(.03, .03, .02, 6), M.pipe, .14, .89, .05, g);
    circ(x, z, .32);
    return g;
  }
  drum(5.7, 2.4); drum(6.25, 2.95); drum(5.75, 3.1); drum(-6, -1.1);
  supports.push({ x: -6, z: -1.1, r: .27, y: .885 });

  // ---------- earth backstop berm ----------
  {
    const shape = new THREE.Shape([[-1.9,0],[1.6,0],[.5,2.5],[-.4,2.6]].map(p => new THREE.Vector2(...p)));
    const geo = new THREE.ExtrudeGeometry(shape, { depth: 24, steps: 24, bevelEnabled: false });
    const p = geo.attributes.position;
    for (let i = 0; i < p.count; i++) { const y = p.getY(i), z = p.getZ(i); if (y > .1) p.setY(i, y + Math.sin(z*.9)*.12 + Math.sin(z*2.3)*.06); }
    geo.computeVertexNormals(); geo.rotateY(Math.PI/2); geo.translate(-12, 0, -14.4);
    tile(M.dirt, 1); M.dirt.map.repeat.set(1.5, 1.5);
    const b = solid(mesh(geo, M.dirt), 'dirt');
    seg(-12, -14.2, 12, -14.2, 1.6);
    fadeables.push({ x: 0, z: -14.4, mats: [M.dirt], op: 1, r: 12, shared: true });
    b.receiveShadow = true;
  }

  // ---------- chain-link fence with concertina on the outriggers ----------
  {
    const link = new THREE.MeshStandardMaterial({ map: makeTexture(['#a8aca2'], 'chainlink', 60), alphaTest: .5, side: THREE.DoubleSide, roughness: .6, metalness: .5 });
    link.map.wrapS = link.map.wrapT = THREE.RepeatWrapping;
    const wireMat = new THREE.MeshStandardMaterial({ color: 0x6d726c, roughness: .5, metalness: .6, flatShading: true });
    const fenceMats = [link, M.pipe, wireMat];
    function fence(x, z0, z1) {
      const len = z1 - z0, g = grp(x, 0, (z0 + z1)/2);
      const net = mesh(new THREE.PlaneGeometry(len, 1.8), link, 0, .95, 0, g); net.rotation.y = Math.PI/2;
      net.material.map.repeat.set(len*1.6, 1.8*1.6);
      for (let t = -len/2; t <= len/2 + .01; t += 2.5) {
        solid(mesh(new THREE.CylinderGeometry(.035, .035, 2.2, 6), M.pipe, 0, 1.1, t, g), 'metal');
        mesh(new THREE.BoxGeometry(.04, .04, .4), M.pipe, Math.sign(x)*.15, 2.18, t, g).rotation.z = Math.sign(x)*-.8;
      }
      mesh(new THREE.CylinderGeometry(.025, .025, len, 6), M.pipe, 0, 1.86, 0, g).rotation.x = Math.PI/2;
      // concertina: a helix of razor wire riding on the outriggers
      const pts = [], turns = len/.16;
      for (let i = 0; i <= turns*10; i++) { const a = i/10*Math.PI*2, t = -len/2 + i/10*.16; pts.push(V(Math.sign(x)*.28 + Math.cos(a)*.2, 2.25 + Math.sin(a)*.2, t + Math.sin(a*.5)*.03)); }
      const coil = new THREE.Mesh(new THREE.TubeGeometry(new THREE.CatmullRomCurve3(pts), pts.length, .008, 3), wireMat); g.add(coil);
      seg(x, z0, x, z1, .1);
      fadeables.push({ x, z: (z0 + z1)/2, mats: fenceMats, op: 1, r: len/2, alpha: true });
    }
    fence(-9.4, -15.5, 6); fence(9.4, -15.5, 6);
  }

  // ---------- guard tower ----------
  {
    const g = grp(-7.7, 0, 3.5), mats = [M.oakDark, M.oak, M.tin];
    const T = mats.map(m => m.clone()); const [tDark, tOak, tTin] = T;
    for (const [x, z] of [[-.85,-.85],[.85,-.85],[-.85,.85],[.85,.85]]) {
      solid(mesh(new THREE.BoxGeometry(.16, 3.9, .16), tDark, x*.94, 1.95, z*.94, g), 'wood').rotation.set(z*.03, 0, -x*.03);
    }
    for (const y of [.9, 2.0]) for (const s of [-1, 1]) {
      mesh(new THREE.BoxGeometry(1.7, .08, .06), tDark, 0, y, s*.86, g).rotation.z = s*.55;
      mesh(new THREE.BoxGeometry(.06, .08, 1.7), tDark, s*.86, y, 0, g).rotation.x = s*.55;
    }
    solid(mesh(new THREE.BoxGeometry(2.1, .1, 2.1), tOak, 0, 3.0, 0, g), 'wood');
    for (const s of [-1, 1]) {
      mesh(new THREE.BoxGeometry(2.1, .5, .05), tOak, 0, 3.3, s*1.03, g); mesh(new THREE.BoxGeometry(.05, .5, 2.1), tOak, s*1.03, 3.3, 0, g);
    }
    for (const [x, z] of [[-1,-1],[1,-1],[-1,1],[1,1]]) mesh(new THREE.BoxGeometry(.08, 1.2, .08), tDark, x, 3.6, z, g);
    const roof = mesh(new THREE.ConeGeometry(1.75, .7, 4, 1), tTin, 0, 4.55, 0, g); roof.rotation.y = Math.PI/4;
    for (const s of [-1, 1]) mesh(new THREE.BoxGeometry(.06, 3.1, .06), tDark, s*.22, 1.5, 1.05, g).rotation.x = -.18;
    for (let i = 0; i < 9; i++) mesh(new THREE.BoxGeometry(.44, .04, .05), tDark, 0, .3 + i*.32, 1.1 - i*.32*.18, g);
    circ(-7.7, 3.5, 1.05);
    fadeables.push({ x: -7.7, z: 3.5, mats: T, op: 1, r: 1.6, tall: true });
  }

  // ---------- GP-small tent ----------
  {
    const g = grp(7.6, 0, 4.9); g.rotation.y = -.12;
    const tc = M.canvas.clone(), tr = M.rope.clone(), td = M.oakDark.clone();
    const shape = new THREE.Shape([[-1.6,0],[1.6,0],[1.6,1.25],[0,2.15],[-1.6,1.25]].map(p => new THREE.Vector2(...p)));
    const geo = new THREE.ExtrudeGeometry(shape, { depth: 3.4, bevelEnabled: false }); geo.translate(0, 0, -1.7);
    solid(mesh(geo, tc, 0, 0, 0, g), 'canvas').rotation.y = Math.PI/2;
    mesh(new THREE.BoxGeometry(.04, 1.5, .9), td, -1.72, .75, 0, g);
    for (const s of [-1, 1]) for (const z of [-1.2, 0, 1.2]) {
      const r = mesh(new THREE.BoxGeometry(.012, .012, 1.35), tr, z, .62, s*2.05, g); r.rotation.x = s*.72;
      mesh(new THREE.BoxGeometry(.04, .2, .04), td, z, .05, s*2.5, g);
    }
    circ(7.6, 4.9, 1.9);
    fadeables.push({ x: 7.6, z: 4.9, mats: [tc, tr, td], op: 1, r: 2.2 });
  }

  // ---------- range flag (red "Bravo": live fire) and distance boards ----------
  {
    const pole = grp(-7.1, 0, .2);
    solid(mesh(new THREE.CylinderGeometry(.035, .045, 4.2, 6), M.pipe, 0, 2.1, 0, pole), 'metal');
    mesh(new THREE.OctahedronGeometry(.06, 0), M.olive, 0, 4.25, 0, pole);
    const geo = new THREE.PlaneGeometry(.95, .62, 8, 5); geo.translate(.5, -.31, 0);
    const mat = new THREE.MeshStandardMaterial({ map: M.red.map, side: THREE.DoubleSide, roughness: 1, flatShading: true });
    const cloth = mesh(geo, mat, 0, 4.1, 0, pole);
    flags.push({ geo, base: geo.attributes.position.array.slice(), cloth });
    circ(-7.1, .2, .1);
  }
  function board(x, z, text) {
    const g = grp(x, 0, z);
    mesh(new THREE.BoxGeometry(.06, .9, .06), M.oakDark, 0, .45, 0, g);
    const tex = makeTexture('#d8d4c4', 'plain', 70 + text.length);
    const ctx = tex.image.getContext('2d'); ctx.fillStyle = '#2a2b26'; ctx.font = 'bold 34px monospace'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.fillText(text, 32, 34);
    ctx.fillStyle = 'rgba(40,40,30,.6)'; ctx.fillRect(0, 0, 64, 3); ctx.fillRect(0, 61, 64, 3); tex.needsUpdate = true;
    const face = new THREE.MeshStandardMaterial({ map: tex, roughness: .95, flatShading: true });
    const b = solid(mesh(new THREE.BoxGeometry(.44, .3, .03), [M.oak, M.oak, M.oak, M.oak, face, M.oak], 0, .95, 0, g), 'wood');
    g.rotation.y = .5;
    return b;
  }
  board(-7.8, -3.8, '5м'); board(-7.8, -8.8, '10м'); board(-7.8, -13.0, '15м');

  // ---------- pop-up silhouette targets ----------
  const holeMat = new THREE.MeshBasicMaterial({ color: 0xcdbb8e });
  function popup(x, z, yaw) {
    const base = grp(x, 0, z); base.rotation.y = yaw;
    solid(mesh(new THREE.BoxGeometry(.56, .26, .3), M.olive, 0, .13, -.12, base), 'metal');
    mesh(new THREE.BoxGeometry(.12, .1, .2), M.pipe, .2, .3, -.12, base);
    sandbags(x - Math.cos(yaw)*.7, z + Math.sin(yaw)*.7 + .32*Math.cos(yaw), x + Math.cos(yaw)*.7, z - Math.sin(yaw)*.7 + .32*Math.cos(yaw), 2);
    const hinge = grp(0, .27, 0, base);
    const shape = new THREE.Shape(IVAN.map(p => new THREE.Vector2(...p)));
    const geo = new THREE.ExtrudeGeometry(shape, { depth: .018, bevelEnabled: false }); geo.translate(0, 0, -.009);
    const uv = geo.attributes.uv, p = geo.attributes.position; for (let i = 0; i < p.count; i++) uv.setXY(i, p.getX(i) + .5, p.getY(i));
    const brd = mesh(geo, M.target, 0, 0, 0, hinge);
    mesh(new THREE.BoxGeometry(.04, .3, .03), M.pipe, 0, .05, -.03, hinge);
    const t = {
      type: 'popup', material: 'wood', group: hinge, meshes: [brd], x, z, h: .94,
      th: 0, w: 0, state: 'up', timer: 0, decals: [],
      center() { return V(0, .5, 0).applyMatrix4(hinge.matrixWorld); },
      test(o, d) {
        if (this.th > 1.2) return null;
        const inv = _m.copy(hinge.matrixWorld).invert(), lo = o.clone().applyMatrix4(inv), ld = d.clone().transformDirection(inv);
        if (Math.abs(ld.z) < 1e-5) return null;
        const tt = -lo.z/ld.z; if (tt <= 0) return null;
        const hx = lo.x + ld.x*tt, hy = lo.y + ld.y*tt;
        if (!inPoly(hx, hy, IVAN)) return null;
        const n = V(0, 0, ld.z > 0 ? -1 : 1).transformDirection(hinge.matrixWorld);
        return { t: tt, point: o.clone().addScaledVector(d, tt), normal: n, zone: hy > .66 ? 'head' : 'torso', local: V(hx, hy, ld.z > 0 ? -.01 : .01) };
      },
      hit(r, dir, power = 1) {
        const back = V(0, 0, -1).transformDirection(base.matrixWorld).dot(dir);
        this.w += (2.4 + R()*.6)*power*Math.sign(back || 1)*(this.state === 'up' ? 1 : .3);
        if (this.state === 'up') { this.state = 'fall'; this.timer = 0; return true; }
        return false;
      },
      update(dt) {
        const g = 14.7;
        if (this.state === 'up') { this.w += (-90*this.th - 9*this.w)*dt; }
        else if (this.state === 'fall' || this.state === 'down') {
          this.w += (g*Math.sin(this.th) - .4*this.w)*dt;
          this.timer += dt;
          if (this.timer > 2.6) this.state = 'rise';
        } else if (this.state === 'rise') {
          const want = Math.max(0, this.th - 1.4*dt);
          this.w += ((want - this.th)*60 - 7*this.w)*dt;
          if (this.th < .05) { this.state = 'up'; }
        }
        this.th += this.w*dt;
        if (this.th > 1.45) { this.th = 1.45; if (this.w > 0) { if (this.w > 1.5) this.clack = this.w; this.w *= -.28; } this.state = this.state === 'fall' ? 'down' : this.state; }
        if (this.th < -.25) { this.th = -.25; this.w *= -.3; }
        hinge.rotation.x = -this.th;
      }
    };
    targets.push(t); circ(x, z, .4);
    return t;
  }
  popup(-3, -3.6, 0); popup(.7, -5.6, 0); popup(3.5, -4.1, -.1); popup(-1.7, -8.7, .05); popup(2.5, -10.2, 0); popup(-4.6, -11.2, .1);

  // ---------- steel gongs on pipe frames ----------
  function gongFrame(x, z, plates) {
    const g = grp(x, 0, z), w = plates.length*.55 + .2;
    for (const s of [-1, 1]) {
      solid(mesh(new THREE.CylinderGeometry(.03, .03, 1.6, 6), M.pipe, s*w/2, .8, -.18, g), 'metal').rotation.x = .2;
      solid(mesh(new THREE.CylinderGeometry(.03, .03, 1.6, 6), M.pipe, s*w/2, .8, .18, g), 'metal').rotation.x = -.2;
    }
    const bar = mesh(new THREE.CylinderGeometry(.03, .03, w + .1, 6), M.pipe, 0, 1.56, 0, g); bar.rotation.z = Math.PI/2; solid(bar, 'metal');
    plates.forEach(([px, len, r, rect], i) => {
      const piv = grp(px, 1.56, 0, g);
      for (const s of [-1, 1]) {
        for (let k = 0; k < 4; k++) mesh(new THREE.TorusGeometry(.016, .005, 3, 6), M.pipe, s*r*.55, -.03 - k*len/4, 0, piv).rotation.y = k % 2 ? Math.PI/2 : 0;
      }
      const plateGeo = rect ? new THREE.BoxGeometry(r*2, r*2.8, .02) : new THREE.CylinderGeometry(r, r, .02, 14);
      if (!rect) plateGeo.rotateX(Math.PI/2);
      const plate = mesh(plateGeo, M.white, 0, -len - (rect ? r*1.4 : r), 0, piv);   // a target: traced analytically, not as a solid
      const t = {
        type: 'gong', material: 'steel', group: plate, meshes: [plate], piv, x: x + px, z, r, rect, ph: 0, pw: 0, yw: 0, yv: 0, decals: [],
        center() { return V(0, 0, 0).applyMatrix4(plate.matrixWorld); },
        test(o, d) {
          const inv = _m.copy(plate.matrixWorld).invert(), lo = o.clone().applyMatrix4(inv), ld = d.clone().transformDirection(inv);
          if (Math.abs(ld.z) < 1e-5) return null;
          const tt = -lo.z/ld.z; if (tt <= 0) return null;
          const hx = lo.x + ld.x*tt, hy = lo.y + ld.y*tt;
          if (rect ? (Math.abs(hx) > r || Math.abs(hy) > r*1.4) : Math.hypot(hx, hy) > r) return null;
          return { t: tt, point: o.clone().addScaledVector(d, tt), normal: V(0, 0, ld.z > 0 ? -1 : 1).transformDirection(plate.matrixWorld), zone: Math.hypot(hx, hy) < r*.3 ? 'center' : 'edge', local: V(hx, hy, ld.z > 0 ? -.011 : .011) };
        },
        hit(r2, dir, power = 1) {
          const n = V(0, 0, 1).transformDirection(piv.matrixWorld);
          this.pw += -n.dot(dir)*3.2*power; this.yv += (r2.local ? r2.local.x : 0)*-40*power;
          return true;
        },
        update(dt) {
          this.pw += (-9.8/(len + r)*Math.sin(this.ph) - .5*this.pw)*dt; this.ph += this.pw*dt;
          this.yv += (-30*this.yw - 1.2*this.yv)*dt; this.yw += this.yv*dt;
          piv.rotation.set(this.ph, this.yw, 0);
        }
      };
      targets.push(t);
    });
    circ(x, z, .2); seg(x - w/2, z, x + w/2, z, .2);
  }
  gongFrame(-2.6, -12.1, [[-.3, .28, .16], [.3, .2, .13]]);
  gongFrame(4.9, -12.4, [[0, .22, .15, true]]);
  gongFrame(5.6, -7.2, [[0, .3, .12]]);

  // ---------- tin cans on a plank table and a drum ----------
  {
    const tb = grp(5.3, 0, -1.4); tb.rotation.y = -.15;
    solid(mesh(new THREE.BoxGeometry(1.5, .05, .42), M.oak, 0, .78, 0, tb), 'wood');
    for (const s of [-1, 1]) {
      for (const d of [-1, 1]) mesh(new THREE.BoxGeometry(.05, .8, .05), M.oakDark, s*.6, .38, d*.14, tb).rotation.x = d*-.12;
      mesh(new THREE.BoxGeometry(.05, .05, .3), M.oakDark, s*.6, .25, 0, tb);
    }
    seg(5.3 - .75, -1.4 - .11, 5.3 + .75, -1.4 + .11, .26);
    supports.push({ table: tb, w: .74, d: .2, y: .805 });
  }
  const canGeo = new THREE.CylinderGeometry(.034, .034, .12, 9);
  const canMats = [['#a9aca4', '#9b3a2c', '#e0d2a0'], ['#a9aca4', '#3c5b78', '#d8d0b0'], ['#a9aca4', '#4f6a35', '#caa84e'], ['#a9aca4', '#b88a2f', '#7a2a20']]
    .map((pal, i) => new THREE.MeshStandardMaterial({ map: makeTexture(pal, 'can', 80 + i), roughness: .5, metalness: .45, flatShading: true }));
  function can(x, y, z, i) {
    const m = mesh(canGeo, canMats[i % canMats.length], x, y + .06, z);
    m.rotation.y = R()*6;
    const t = {
      type: 'can', material: 'tin', group: m, meshes: [m], home: V(x, y + .06, z), homeQ: m.quaternion.clone(),
      v: V(), w: V(), awake: false, sleep: 0, off: false, decals: [], pop: 1,
      center() { return m.position.clone(); },
      test(o, d) {
        const ax = V(0, 1, 0).applyQuaternion(m.quaternion), a = m.position.clone().addScaledVector(ax, -.06);
        // closest approach between the ray and the can's axis segment
        const w0 = o.clone().sub(a), b = d.dot(ax), dd = d.dot(w0), e = ax.dot(w0), den = 1 - b*b;
        let sc = den > 1e-6 ? (b*e - dd)/den : 0, tc = clamp(den > 1e-6 ? (e - b*dd)/den : e, 0, .12);
        sc = Math.max(0, d.dot(a.clone().addScaledVector(ax, tc).sub(o)));
        const pc = o.clone().addScaledVector(d, sc), qc = a.clone().addScaledVector(ax, tc);
        if (pc.distanceTo(qc) > .042) return null;
        const n = pc.clone().sub(qc); n.addScaledVector(ax, -n.dot(ax)); if (n.lengthSq() < 1e-8) n.copy(d).negate(); n.normalize();
        return { t: sc - .02, point: pc, normal: n, zone: 'can' };
      },
      hit(r, dir, power = 1) {
        this.awake = true; this.sleep = 0; this.off = true;
        this.v.copy(dir).multiplyScalar((2.6 + R()*1.4)*power); this.v.y += 1.8 + R()*1.6;
        this.w.set((R() - .5)*30, (R() - .5)*16, (R() - .5)*30);
        return true;
      },
      update(dt) {
        if (!this.awake) {
          if (this.off) { this.sleep += dt; if (this.sleep > 5) { this.off = false; this.pop = 0; m.position.copy(this.home); m.quaternion.copy(this.homeQ); this.v.set(0, 0, 0); this.w.set(0, 0, 0); } }
          if (this.pop < 1) { this.pop = Math.min(1, this.pop + dt*3); m.scale.setScalar(.2 + .8*THREE.MathUtils.smootherstep(this.pop, 0, 1)); }
          return;
        }
        this.v.y -= 9.8*dt; m.position.addScaledVector(this.v, dt);
        const wl = this.w.length(); if (wl > 1e-4) m.quaternion.premultiply(_q.setFromAxisAngle(_v.copy(this.w).divideScalar(wl), wl*dt));
        // contact: lowest point of the cylinder against the floor (ground, table top, drum lid)
        const ax = V(0, 1, 0).applyQuaternion(m.quaternion), floor = groundAt(m.position.x, m.position.z, m.position.y);
        const low = .06*Math.abs(ax.y) + .034*Math.sqrt(Math.max(0, 1 - ax.y*ax.y));
        if (m.position.y - low < floor) {
          m.position.y = floor + low;
          if (this.v.y < 0) { if (this.v.y < -1.2) this.clink = -this.v.y; this.v.y *= -.32; }
          this.v.x *= .82; this.v.z *= .82; this.w.multiplyScalar(.8);
          // tip onto the side or the base, whichever is closer
          const flatten = Math.abs(ax.y) > .7 ? (ax.y > 0 ? V(0, 1, 0) : V(0, -1, 0)) : V(ax.x, 0, ax.z).normalize();
          m.quaternion.premultiply(_q.setFromUnitVectors(ax, ax.clone().lerp(flatten, .12).normalize()));
          if (this.v.lengthSq() < .02 && this.w.lengthSq() < .5) { this.sleep += dt; if (this.sleep > .3) { this.awake = false; this.sleep = 0; } }
        }
      }
    };
    targets.push(t);
    return t;
  }
  {
    const tb = supports.find(s => s.table).table; tb.updateMatrixWorld(true);
    for (let i = 0; i < 6; i++) { const p = V(-.6 + i*.24, 0, (R() - .5)*.08).applyMatrix4(tb.matrixWorld); can(p.x, .805, p.z, i); }
    for (let i = 0; i < 3; i++) can(-6 + (i - 1)*.11, .885, -1.1 + (i === 1 ? .08 : -.04), i + 2);
  }
  // floor height under a point for loose objects: table top and drum lid, else the ground
  function groundAt(x, z, y = 9) {
    let h = 0;
    for (const s of supports) {
      if (s.table) {
        const l = s.table.worldToLocal(_v.set(x, s.y, z));
        if (Math.abs(l.x) < s.w && Math.abs(l.z) < s.d && y > s.y - .05) h = Math.max(h, s.y);
      } else if (Math.hypot(x - s.x, z - s.z) < s.r && y > s.y - .05) h = Math.max(h, s.y);
    }
    return h;
  }

  // ---------- trees, bushes, rocks, grass (the knight's yard recipe) ----------
  const trees = [];
  function tree(x, z, s) {
    const g = grp(x, 0, z); g.rotation.y = R()*6;
    solid(mesh(new THREE.CylinderGeometry(.07*s, .13*s, 1.5*s, 6), M.bark, 0, .75*s, 0, g), 'wood');
    const b = mesh(new THREE.CylinderGeometry(.03*s, .05*s, .6*s, 5), M.bark, .18*s, 1.25*s, 0, g); b.rotation.z = -.7;
    const mats = M.leaf.map(m => { const c = m.clone(); c.transparent = true; return c; });
    [[0, 1.65, 0, .78], [.32, 2.05, .12, .62], [-.26, 2.3, -.14, .52], [.05, 2.62, .05, .4], [-.35, 1.8, .25, .45]].forEach(([dx, y, dz, r], i) => {
      solid(mesh(new THREE.IcosahedronGeometry(r*s, 0), mats[(i + ((R()*3) | 0)) % 3], dx*s, y*s, dz*s, g), 'leaf').rotation.set(R()*3, R()*3, 0);
    });
    circ(x, z, .2*s); fadeables.push({ x, z, mats, op: 1, r: 1.6 });
  }
  for (let n = 0; n < 700 && trees.length < 40; n++) {
    const x = (R() - .5)*40, z = (R() - .5)*40, d = Math.hypot(x, z + 2);
    if (d > 20 || onRoad(x, z)) continue;
    if (Math.abs(x) < 10.3 && z < 7 && z > -17) continue;      // keep the range clear
    if (x > -2 && z > 5 && d < 13) continue;                    // and the default view
    if (trees.some(t => Math.hypot(t[0] - x, t[1] - z) < 2)) continue;
    trees.push([x, z]); tree(x, z, .85 + R()*.5);
  }
  for (let i = 0; i < 30; i++) {
    const x = (R() - .5)*36, z = (R() - .5)*36;
    if (onRoad(x, z) || (Math.abs(x) < 9.6 && z < 7.2 && z > -15.6)) continue;
    const g = grp(x, 0, z), m = M.leaf[i % 3];
    for (let k = 0; k < 3; k++) mesh(new THREE.IcosahedronGeometry(.2 + R()*.16, 0), m, (R() - .5)*.5, .16, (R() - .5)*.5, g).rotation.set(R()*3, R()*3, 0);
  }
  for (let i = 0; i < 24; i++) {
    const x = (R() - .5)*30, z = (R() - .5)*30;
    if (onRoad(x, z)) continue;
    mesh(new THREE.DodecahedronGeometry(.05 + R()*.12, 0), M.rock, x, .03, z).rotation.set(R()*3, R()*3, R()*3);
  }
  {
    const blade = new THREE.ConeGeometry(.018, .16, 3); blade.translate(0, .08, 0);
    const mat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 1, flatShading: true });
    const N = 3600, im = new THREE.InstancedMesh(blade, mat, N), m4 = new THREE.Matrix4(), q = new THREE.Quaternion(), e = new THREE.Euler();
    const cols = [0x6d7448, 0x5e6a3c, 0x7c8150, 0x8a8a58].map(c => new THREE.Color(c));
    let n = 0;
    while (n < N) {
      const x = (R() - .5)*36, z = (R() - .5)*36 - 2;
      if (Math.hypot(x, z + 2) > 18 || onRoad(x, z)) continue;
      if (z > .9 && z < 4.3 && Math.abs(x) < 7.5 && R() > .04) continue;   // gravel firing line
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

  // ---------- shooting ----------
  const rc = new THREE.Raycaster(), _m4 = new THREE.Matrix4(), _n = V();
  // nearest hit along a ray: static props by raycast, targets analytically, then the ground plane
  function trace(o, d, far, skipFaded = false) {
    let best = null;
    rc.set(o, d); rc.near = 0; rc.far = far;
    for (const h of rc.intersectObjects(solids, false)) {
      const mat = Array.isArray(h.object.material) ? h.object.material[0] : h.object.material;
      if (skipFaded && mat.opacity < .6) continue;
      _n.copy(h.face ? h.face.normal : V(0, 1, 0));
      if (h.object.isInstancedMesh) { h.object.getMatrixAt(h.instanceId, _m4); _n.transformDirection(_m4); }
      _n.transformDirection(h.object.matrixWorld); if (_n.dot(d) > 0) _n.negate();
      best = { t: h.distance, point: h.point.clone(), normal: _n.clone(), kind: h.object.userData.kind || 'wood', object: h.object };
      break;
    }
    for (const tg of targets) {
      const r = tg.test(o, d);
      if (r && r.t > 0 && r.t < far && (!best || r.t < best.t)) best = { ...r, kind: tg.material, target: tg };
    }
    if (d.y < -1e-4) {
      const t = -o.y/d.y;
      if (t < far && (!best || t < best.t)) {
        const p = o.clone().addScaledVector(d, t);
        best = { t, point: p, normal: V(0, 1, 0), kind: onRoad(p.x, p.z) ? 'asphalt' : (p.z > .9 && p.z < 4.3 && Math.abs(p.x) < 7.5) ? 'gravel' : 'dirt' };
      }
    }
    return best;
  }

  // ---------- per-frame ----------
  function update(dt, time) {
    for (const t of targets) t.update(dt);
    for (const f of flags) {
      const p = f.geo.attributes.position;
      for (let i = 0; i < p.count; i++) {
        const x = f.base[i*3], y = f.base[i*3 + 1], w = Math.max(0, x - .02);
        p.setZ(i, Math.sin(time*3.1 - x*5.5 + y*1.4)*.07*w + Math.sin(time*5.3 - x*10)*.018*w);
        p.setY(i, y - w*w*.05);
      }
      p.needsUpdate = true; f.geo.computeVertexNormals();
    }
  }
  return { colliders, fadeables, solids, targets, update, trace, groundAt, M };
}
const _m = new THREE.Matrix4(), _q = new THREE.Quaternion(), _v = new THREE.Vector3();
