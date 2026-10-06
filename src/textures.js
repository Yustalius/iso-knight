import * as THREE from 'three';

// Shared 64×64 pixel textures for every character and prop. Originally part of knight-model.js;
// the knight's patterns are unchanged, the soldier and the range add camo, webbing, skin and friends.

export function makeRandom(seed) { return () => ((seed = (1664525 * seed + 1013904223) >>> 0) / 4294967296); }

// Camouflage palettes: [base, large blobs, dark branches, light spots].
export const CAMO = {
  woodland: ['#6b7450', '#5a4834', '#26261f', '#9c8f6c'],
  desert: ['#bba983', '#9b8564', '#6e5c47', '#cfc3a2'],
  olive: ['#5b5f3d', '#54583a', '#474a31', '#63673f']
};

// Pixel-crisp filled circle, drawn with wrap-around so the pattern tiles.
function blot(ctx, x, y, r) {
  for (const ox of [-64, 0, 64]) for (const oy of [-64, 0, 64]) {
    for (let dy = -r; dy <= r; dy++) { const w = Math.floor(Math.sqrt(r*r - dy*dy + r*.6)); ctx.fillRect(Math.round(x - w) + ox, Math.round(y + dy) + oy, 2*w + 1, 1); }
  }
}
// A meandering stroke of blots: the organic shapes of woodland camo.
function smear(ctx, random, n, r0, r1, step, flat) {
  let x = random()*64, y = random()*64, a = random()*Math.PI*2;
  for (let i = 0; i < n; i++) {
    blot(ctx, x, y, Math.round(r0 + random()*(r1 - r0)));
    a += (random() - .5)*1.6; x += Math.cos(a)*step; y += Math.sin(a)*step*flat;
  }
}

// 64×64 nearest-filtered canvas textures; `paint` is reusable so cloth can be re-dyed.
// For 'camo' and 'can' the colour argument is a palette array.
export function makeTexture(color, type = 'plain', seed = 1729) {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 64;
  const ctx = canvas.getContext('2d');
  const texture = new THREE.CanvasTexture(canvas);
  texture.magFilter = THREE.NearestFilter; texture.minFilter = THREE.NearestMipmapNearestFilter;
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.paint = c => {
    const random = makeRandom(seed);
    const pal = Array.isArray(c) ? c : [c];
    if (type === 'chainlink') {
      // galvanised wire diamonds on a transparent background (use with alphaTest)
      ctx.clearRect(0, 0, 64, 64);
      for (let i = 0; i < 64; i++) for (let k = 0; k < 64; k += 8) {
        ctx.fillStyle = pal[0]; ctx.fillRect((i + k) % 64, i, 1, 1); ctx.fillRect((k - i + 128) % 64, i, 1, 1);
        ctx.fillStyle = 'rgba(20,24,22,.8)'; ctx.fillRect((i + k + 1) % 64, i, 1, 1);
      }
      texture.needsUpdate = true; return;
    }
    const grain = type === 'skin' ? .35 : 1;
    ctx.fillStyle = pal[0]; ctx.fillRect(0, 0, 64, 64);
    for (let i = 0; i < 1600; i++) {
      ctx.fillStyle = random() > .48 ? `rgba(255,240,205,${random()*.13*grain})` : `rgba(15,19,17,${random()*.22*grain})`;
      ctx.fillRect(Math.floor(random()*64), Math.floor(random()*64), 1 + Math.floor(random()*2), 1);
    }
    if (type === 'mail') {
      for (let y = 0; y < 64; y += 5) for (let x = -4; x < 64; x += 6) {
        const dx = x + (y % 10 === 0 ? 3 : 0);
        ctx.fillStyle = '#222928'; ctx.fillRect(dx, y, 5, 4);
        ctx.fillStyle = '#7d8581'; ctx.fillRect(dx, y, 4, 1); ctx.fillRect(dx, y+1, 1, 2);
        ctx.fillStyle = '#454c48'; ctx.fillRect(dx+1, y+3, 3, 1);
      }
    } else if (type === 'cloth') {
      ctx.strokeStyle = 'rgba(17,16,12,.2)'; ctx.lineWidth = 1;
      for (let x = -64; x < 128; x += 8) { ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x+64, 64); ctx.stroke(); }
    } else if (type === 'steel') {
      for (let i = 0; i < 22; i++) { ctx.fillStyle = 'rgba(220,222,198,.24)'; ctx.fillRect(random()*64, random()*64, 1, random()*8+1); }
      ctx.fillStyle = 'rgba(26,33,30,.18)'; ctx.fillRect(0, 56, 64, 8);
    } else if (type === 'planks') {
      for (let y = 0; y < 64; y += 16) { ctx.fillStyle = 'rgba(20,14,8,.55)'; ctx.fillRect(0, y, 64, 1); ctx.fillStyle = 'rgba(255,230,190,.08)'; ctx.fillRect(0, y+1, 64, 1); }
      for (let i = 0; i < 40; i++) { ctx.fillStyle = 'rgba(30,20,10,.25)'; ctx.fillRect(random()*64, random()*64, random()*14 + 4, 1); }
      for (let i = 0; i < 8; i++) { ctx.fillStyle = 'rgba(40,40,40,.6)'; ctx.fillRect((random()*64)|0, ((random()*4)|0)*16 + 7, 1, 1); }
    } else if (type === 'bricks') {
      ctx.fillStyle = 'rgba(22,24,20,.5)';
      for (let y = 0; y < 64; y += 16) { ctx.fillRect(0, y, 64, 1); for (let x = (y/16)%2 ? 16 : 0; x < 64; x += 32) ctx.fillRect(x, y, 1, 16); }
      for (let i = 0; i < 18; i++) { ctx.fillStyle = 'rgba(90,110,60,.35)'; ctx.fillRect(random()*64, random()*64, 2, 1); }
    } else if (type === 'straw') {
      for (let i = 0; i < 260; i++) { ctx.fillStyle = random() > .5 ? 'rgba(255,236,170,.3)' : 'rgba(90,70,30,.3)'; ctx.fillRect(random()*64, random()*64, 1, 2 + random()*5); }
    } else if (type === 'burlap') {
      for (let y = 0; y < 64; y += 2) { ctx.fillStyle = 'rgba(40,30,15,.18)'; ctx.fillRect(0, y, 64, 1); }
      for (let x = 0; x < 64; x += 2) { ctx.fillStyle = 'rgba(40,30,15,.12)'; ctx.fillRect(x, 0, 1, 64); }
    } else if (type === 'leaves') {
      for (let i = 0; i < 300; i++) { ctx.fillStyle = random() > .55 ? 'rgba(190,210,120,.22)' : 'rgba(10,20,8,.35)'; ctx.fillRect(random()*64, random()*64, 2, 2); }
    } else if (type === 'camo') {
      // M81-style: large meandering blobs, then black branches, then light spots; everything wraps
      const [, big, dark, light] = pal;
      ctx.fillStyle = big; for (let i = 0; i < 7; i++) smear(ctx, random, 7, 3, 6, 4, .6);
      ctx.fillStyle = light; for (let i = 0; i < 5; i++) smear(ctx, random, 3, 2, 3, 3, .7);
      ctx.fillStyle = dark; for (let i = 0; i < 8; i++) smear(ctx, random, 5, 1, 2, 3, .45);
      for (let i = 0; i < 500; i++) { ctx.fillStyle = random() > .5 ? 'rgba(255,240,205,.07)' : 'rgba(15,19,17,.12)'; ctx.fillRect(Math.floor(random()*64), Math.floor(random()*64), 1, 1); }
      ctx.strokeStyle = 'rgba(17,16,12,.12)'; ctx.lineWidth = 1;
      for (let x = -64; x < 128; x += 4) { ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x+64, 64); ctx.stroke(); }
    } else if (type === 'webbing') {
      // nylon webbing: tight horizontal weave, stitched edges every 16 px
      for (let y = 0; y < 64; y += 2) { ctx.fillStyle = 'rgba(12,14,8,.22)'; ctx.fillRect(0, y, 64, 1); }
      for (let y = 0; y < 64; y += 16) { ctx.fillStyle = 'rgba(10,12,8,.45)'; ctx.fillRect(0, y, 64, 1); for (let x = 0; x < 64; x += 3) { ctx.fillStyle = 'rgba(220,215,170,.18)'; ctx.fillRect(x, y + 2, 2, 1); } }
    } else if (type === 'skin') {
      for (let i = 0; i < 40; i++) { ctx.fillStyle = 'rgba(150,60,40,.08)'; ctx.fillRect(random()*64, random()*64, 3, 2); }
    } else if (type === 'plywood') {
      for (let i = 0; i < 70; i++) { ctx.fillStyle = random() > .5 ? 'rgba(255,235,190,.1)' : 'rgba(40,28,14,.16)'; ctx.fillRect(0, (random()*64)|0, 64, 1); }
      for (let i = 0; i < 4; i++) { ctx.fillStyle = 'rgba(50,34,18,.35)'; ctx.fillRect(random()*60, random()*62, 4, 2); }
    } else if (type === 'concrete') {
      for (let i = 0; i < 160; i++) { ctx.fillStyle = 'rgba(20,20,18,.35)'; ctx.fillRect(random()*64, random()*64, 1, 1); }
      for (let i = 0; i < 7; i++) { ctx.fillStyle = `rgba(${random() > .5 ? '70,66,52' : '230,226,210'},.08)`; ctx.fillRect(random()*56, random()*56, 6 + random()*10, 5 + random()*8); }
      ctx.fillStyle = 'rgba(20,20,18,.18)'; ctx.fillRect(0, 31, 64, 1);
    } else if (type === 'gravel') {
      for (let i = 0; i < 420; i++) { ctx.fillStyle = random() > .5 ? 'rgba(235,228,205,.28)' : 'rgba(25,22,16,.35)'; ctx.fillRect(random()*64, random()*64, 1 + (random()*2|0), 1 + (random()*2|0)); }
    } else if (type === 'asphalt') {
      for (let i = 0; i < 700; i++) { ctx.fillStyle = random() > .6 ? 'rgba(200,198,185,.14)' : 'rgba(5,5,5,.3)'; ctx.fillRect(random()*64, random()*64, 1, 1); }
    } else if (type === 'corrugated') {
      for (let x = 0; x < 64; x += 4) { ctx.fillStyle = 'rgba(255,250,235,.16)'; ctx.fillRect(x, 0, 1, 64); ctx.fillStyle = 'rgba(10,12,10,.3)'; ctx.fillRect(x + 2, 0, 1, 64); }
      for (let i = 0; i < 26; i++) { ctx.fillStyle = 'rgba(120,70,30,.25)'; ctx.fillRect(random()*64, random()*64, 2, 3 + random()*6); }
    } else if (type === 'ammo') {
      // olive crate with yellow stencilled lot numbers
      ctx.fillStyle = 'rgba(10,12,8,.35)'; ctx.fillRect(0, 0, 64, 2); ctx.fillRect(0, 62, 64, 2);
      for (let row = 0; row < 3; row++) for (let x = 12; x < 52; x += 4) if (random() > .2) { ctx.fillStyle = 'rgba(214,190,90,.85)'; ctx.fillRect(x, 24 + row*6, 3, 4); }
    } else if (type === 'can') {
      // tin can: bare metal rims, printed label band with a stripe and a logo block
      const [, label, accent] = pal;
      ctx.fillStyle = label; ctx.fillRect(0, 8, 64, 48);
      ctx.fillStyle = accent; ctx.fillRect(0, 22, 64, 6); ctx.fillRect(18, 32, 22, 14);
      ctx.fillStyle = 'rgba(255,255,255,.5)'; ctx.fillRect(0, 12, 64, 2);
      for (let y = 0; y < 8; y += 2) { ctx.fillStyle = 'rgba(20,20,20,.2)'; ctx.fillRect(0, y, 64, 1); ctx.fillRect(0, 56 + y, 64, 1); }
    }
    texture.needsUpdate = true;
  };
  texture.paint(color);
  return texture;
}

export function makeMaterial(name, color, type = 'plain', metalness = 0, seed) {
  const map = makeTexture(color, type, seed);
  return new THREE.MeshStandardMaterial({ name, map, roughness: metalness ? .7 : .96, metalness, flatShading: true });
}
