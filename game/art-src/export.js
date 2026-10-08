import * as THREE from 'three';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';
import { style, makeRandom } from './textures.js';
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
  return files;
};
window.__ready = true;
