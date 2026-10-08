import * as THREE from 'three';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';
import { style } from './textures.js';
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
  return files;
};
window.__ready = true;
