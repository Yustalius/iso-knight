import * as THREE from 'three';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';
import { style, makeRandom, makeTexture } from './textures.js';
import { createSoldier, magazineMesh } from './soldier-model.js';

// Builds the concept's soldiers exactly as soldier.html does (painted 256 px textures, creased normals) and hands
// them to tools/export-art.cjs as .glb files and PNG textures for the Godot viewer (game/art/).
// The model sources next to this file are copies from the concept branch; only this file is new.

style.smooth = true; style.anisotropy = 8;   // must be set before any material is made

const b64 = buf => { let s = ''; const u = new Uint8Array(buf); for (let i = 0; i < u.length; i += 0x8000) s += String.fromCharCode(...u.subarray(i, i + 0x8000)); return btoa(s); };
const png = tex => tex.image.toDataURL('image/png').split(',')[1];

// texture.repeat is a material-level setting in three; glTF would need KHR_texture_transform for it, so the
// repeat is baked into the UVs of the triangles that use such a map (camo: 1 × 0.8 per tile)
function bakeRepeat(skin) {
  const g = skin.geometry, uv = g.attributes.uv, mats = skin.material, idx = g.index;
  for (const grp of g.groups) {
    const map = mats[grp.materialIndex].map;
    if (!map || (map.repeat.x === 1 && map.repeat.y === 1)) continue;
    const seen = new Set();
    for (let k = grp.start; k < grp.start + grp.count; k++) {
      const i = idx ? idx.getX(k) : k; if (seen.has(i)) continue; seen.add(i);
      uv.setXY(i, uv.getX(i)*map.repeat.x, uv.getY(i)*map.repeat.y);
    }
  }
  for (const m of mats) if (m.map) m.map.repeat.set(1, 1);
}

async function glb(obj) {
  const buf = await new GLTFExporter().parseAsync(obj, { binary: true, onlyVisible: false });
  return b64(buf);
}

// Sprites of gunfx.js and the blood pool of enemy.js, drawn by the same code (copied from the concept branch).
function sprite(draw, size = 64) { const c = document.createElement('canvas'); c.width = c.height = size; draw(c.getContext('2d')); return c.toDataURL('image/png').split(',')[1]; }
const SPRITES = {
  flash: ctx => {
    const rnd = makeRandom(7);
    for (let i = 0; i < 9; i++) {
      const a = i/9*Math.PI*2 + rnd()*.3, L = 14 + rnd()*16;
      for (let r = 2; r < L; r++) { const w = Math.max(1, Math.round((1 - r/L)*4)); ctx.fillStyle = r < 7 ? '#fff6d0' : r < 14 ? '#ffc858' : '#e0782a'; ctx.fillRect(32 + Math.cos(a)*r - w/2, 32 + Math.sin(a)*r - w/2, w, w); }
    }
    ctx.fillStyle = '#fffbe8'; ctx.fillRect(27, 27, 10, 10);
  },
  cone: ctx => {
    for (let x = 0; x < 64; x++) { const h = Math.round((1 - x/64)*13 + 2); ctx.fillStyle = x < 16 ? '#fff3c8' : x < 36 ? '#ffbe50' : '#d9702a'; ctx.fillRect(x, 32 - h, 1, h*2); }
  },
  hole: ctx => {
    ctx.fillStyle = 'rgba(205,190,150,.95)';
    for (let i = 0; i < 26; i++) { const a = i/26*Math.PI*2, r = 18 + (i % 3)*5; ctx.fillRect(32 + Math.cos(a)*r - 3, 32 + Math.sin(a)*r - 3, 6, 6); }
    ctx.beginPath(); ctx.arc(32, 32, 19, 0, Math.PI*2); ctx.fill();
    ctx.fillStyle = '#16140f'; ctx.beginPath(); ctx.arc(32, 32, 11, 0, Math.PI*2); ctx.fill();
  },
  pit: ctx => {
    ctx.fillStyle = 'rgba(30,25,18,.55)'; ctx.beginPath(); ctx.arc(32, 32, 26, 0, Math.PI*2); ctx.fill();
    ctx.fillStyle = 'rgba(14,12,9,.9)'; ctx.beginPath(); ctx.arc(32, 32, 13, 0, Math.PI*2); ctx.fill();
  },
  splat: ctx => {
    const rnd = makeRandom(11); ctx.fillStyle = '#5a5c58';
    for (let i = 0; i < 12; i++) { const a = rnd()*Math.PI*2, L = 10 + rnd()*20; for (let r = 0; r < L; r += 2) ctx.fillRect(32 + Math.cos(a)*r - 2, 32 + Math.sin(a)*r - 2, 4, 4); }
    ctx.fillStyle = '#3d3f3c'; ctx.beginPath(); ctx.arc(32, 32, 9, 0, Math.PI*2); ctx.fill();
  },
  blood: ctx => {
    const rnd = makeRandom(23);
    for (let i = 0; i < 7; i++) {
      const a = rnd()*Math.PI*2, d = rnd()*10, r = 7 + rnd()*9, x = 32 + Math.cos(a)*d, y = 32 + Math.sin(a)*d;
      const g = ctx.createRadialGradient(x, y, 0, x, y, r); g.addColorStop(0, 'rgba(96,12,9,.95)'); g.addColorStop(.7, 'rgba(84,10,8,.9)'); g.addColorStop(1, 'rgba(84,10,8,0)');
      ctx.fillStyle = g; ctx.fillRect(0, 0, 64, 64);
    }
    ctx.fillStyle = 'rgba(90,12,9,.9)';
    for (let i = 0; i < 10; i++) { const a = rnd()*Math.PI*2, d = 18 + rnd()*12; ctx.beginPath(); ctx.arc(32 + Math.cos(a)*d, 32 + Math.sin(a)*d, 1 + rnd()*2.2, 0, Math.PI*2); ctx.fill(); }
  }
};
function pool(x) {   // enemy.js poolMesh: a soft irregular disc, 128 px
  for (let i = 0; i < 9; i++) {
    const a = i/9*Math.PI*2, r = 26 + (i % 3)*7, px = 64 + Math.cos(a)*14, py = 64 + Math.sin(a)*14;
    const g = x.createRadialGradient(px, py, 0, px, py, r);
    g.addColorStop(0, 'rgba(74,10,8,.95)'); g.addColorStop(.75, 'rgba(70,10,8,.85)'); g.addColorStop(1, 'rgba(70,10,8,0)');
    x.fillStyle = g; x.fillRect(0, 0, 128, 128);
  }
}

// World textures for the map: the range's painted 256 px maps (range-world.js colours and seeds) plus two new ones in the
// same style for house walls, which the range has none of: plaster and brick.
const WORLD = {
  grass: ['#6f8a4a', 'leaves', 50], soil: ['#6d5f45', 'plain', 54], sand: ['#a39470', 'burlap', 35],
  concrete: ['#8f8d82', 'concrete', 41], oak: ['#7a6447', 'planks', 31], timber: ['#4e3d2b', 'planks', 32],
  crate: ['#535a37', 'ammo', 34], bark: ['#4a3d2e', 'planks', 37], tin: ['#7c817a', 'corrugated', 43],
  leaf0: ['#56603a', 'leaves', 51], leaf1: ['#4b5634', 'leaves', 52], leaf2: ['#626b42', 'leaves', 53],
  plaster: ['#cdc3aa', 'concrete', 61], gravel: ['#77705f', 'gravel', 40]
};
function bricks() {
  // running bond, 8 courses per tile, mortar joints and a painted wash like the range's textures
  const S = 256, c = document.createElement('canvas'); c.width = c.height = S; const x = c.getContext('2d'), R = makeRandom(71);
  x.fillStyle = '#b8b0a0'; x.fillRect(0, 0, S, S);
  const rows = 8, h = S/rows, w = S/4;
  for (let r = 0; r < rows; r++) for (let k = -1; k < 5; k++) {
    const x0 = k*w + (r % 2 ? w/2 : 0), t = R();
    const col = t < .3 ? [142, 74, 52] : t < .7 ? [156, 84, 58] : t < .9 ? [128, 66, 48] : [168, 98, 70];
    const v = .9 + R()*.18;
    x.fillStyle = `rgb(${col.map(c => Math.round(c*v)).join(',')})`;
    x.fillRect(x0 + 2, r*h + 2, w - 4, h - 4);
    x.fillStyle = 'rgba(255,235,210,.07)'; x.fillRect(x0 + 2, r*h + 2, w - 4, 3);
    x.fillStyle = 'rgba(20,10,5,.12)'; x.fillRect(x0 + 2, r*h + h - 5, w - 4, 3);
    for (let i = 0; i < 6; i++) { x.fillStyle = R() > .5 ? 'rgba(40,20,10,.18)' : 'rgba(230,200,170,.12)'; x.fillRect(x0 + 3 + R()*(w - 8), r*h + 3 + R()*(h - 8), 1 + R()*3, 1 + R()*2); }
  }
  for (let i = 0; i < 16; i++) {
    const cx = R()*S, cy = R()*S, rr = 30 + R()*70, lite = R() > .5, g = x.createRadialGradient(cx, cy, 0, cx, cy, rr);
    g.addColorStop(0, lite ? 'rgba(255,244,214,.07)' : 'rgba(18,20,14,.1)'); g.addColorStop(1, 'rgba(0,0,0,0)'); x.fillStyle = g; x.fillRect(0, 0, S, S);
  }
  return c.toDataURL('image/png').split(',')[1];
}

window.__export = async () => {
  const files = {};
  const variants = { soldier_m81: {}, soldier_opfor: { camo: 'khaki', opfor: true } };
  let mats = null;
  for (const [name, opts] of Object.entries(variants)) {
    const s = createSoldier(opts);
    bakeRepeat(s.skin);
    files[name + '.glb'] = await glb(s.root);
    mats = mats || s.materials;
    files[`tex/${name}_sling.png`] = png(s.sling.material.map);
  }
  files['magazine.glb'] = await glb(magazineMesh(mats));
  for (const [name, draw] of Object.entries(SPRITES)) files[`tex/fx_${name}.png`] = sprite(draw);
  files['tex/fx_pool.png'] = sprite(pool, 128);
  for (const [name, [col, type, seed]] of Object.entries(WORLD)) files[`tex/w_${name}.png`] = png(makeTexture(col, type, seed));
  files['tex/w_brick.png'] = bricks();
  return files;
};
window.__ready = true;
