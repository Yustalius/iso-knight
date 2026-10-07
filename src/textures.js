import * as THREE from 'three';

// Shared canvas textures for every character and prop. Originally part of knight-model.js;
// the knight's 64 px patterns are unchanged, the soldier and the range add camo, webbing, skin and
// friends, plus a painted 256 px style (see `style`).

export function makeRandom(seed) { return () => ((seed = (1664525 * seed + 1013904223) >>> 0) / 4294967296); }

// Camouflage palettes: [base, large blobs, dark branches, light spots].
export const CAMO = {
  woodland: ['#6f7853', '#5d4b36', '#30302a', '#a09371'],
  desert: ['#bba983', '#9b8564', '#6e5c47', '#cfc3a2'],
  olive: ['#5d613f', '#585c3c', '#4d5035', '#64683f'],
  khaki: ['#8a8259', '#6f6446', '#4b4834', '#a69d75']   // the OPFOR target's uniform
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

// Render style shared by every texture and material made after it is set. The knight keeps the
// default pixel style (64 px, nearest filtering, flat shading); the soldier's range switches to
// `smooth` before building anything: 256 px painted textures, trilinear + anisotropic filtering,
// smooth shading.
export const style = { smooth: false, anisotropy: 1 };

// ---------- painted (smooth) style ----------
const SMOOTH = 256;
// draw once per wrap offset so every mark tiles across the edges
function tiled(S, x, y, r, f) {
  for (const ox of [-S, 0, S]) for (const oy of [-S, 0, S]) {
    if (x + ox + r < 0 || x + ox - r > S || y + oy + r < 0 || y + oy - r > S) continue;
    f(x + ox, y + oy);
  }
}
// low-frequency light/dark variation: soft radial washes, the "painted" look
function mottle(ctx, S, random, n, r0, r1, light, dark) {
  for (let i = 0; i < n; i++) {
    const x = random()*S, y = random()*S, r = r0 + random()*(r1 - r0), lite = random() > .5;
    const a = (lite ? light : dark)*(.4 + random()*.6), col = lite ? '255,244,214' : '18,20,14';
    tiled(S, x, y, r, (cx, cy) => {
      const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, r);
      g.addColorStop(0, `rgba(${col},${a})`); g.addColorStop(1, `rgba(${col},0)`);
      ctx.fillStyle = g; ctx.fillRect(cx - r, cy - r, 2*r, 2*r);
    });
  }
}
// an organic closed shape (camo blob, leaf cluster, pebble) through jittered points
function blob(ctx, S, random, x, y, r, sx = 1, sy = 1, rot = 0, jag = .55) {
  const n = 9, pts = [];
  for (let i = 0; i < n; i++) { const a = i/n*Math.PI*2, k = r*(1 - jag/2 + random()*jag); pts.push([Math.cos(a)*k, Math.sin(a)*k]); }
  const mid = (p, q) => [(p[0] + q[0])/2, (p[1] + q[1])/2];
  tiled(S, x, y, r*Math.max(sx, sy)*1.3, (cx, cy) => {
    ctx.save(); ctx.translate(cx, cy); ctx.rotate(rot); ctx.scale(sx, sy);
    ctx.beginPath(); ctx.moveTo(...mid(pts[0], pts[1]));
    for (let i = 1; i <= n; i++) ctx.quadraticCurveTo(...pts[i % n], ...mid(pts[i % n], pts[(i + 1) % n]));
    ctx.closePath(); ctx.fill(); ctx.restore();
  });
}
function strokes(ctx, S, random, n, len0, len1, width, colors, angle, jitter) {
  ctx.lineCap = 'round'; ctx.lineWidth = width;
  for (let i = 0; i < n; i++) {
    const x = random()*S, y = random()*S, L = len0 + random()*(len1 - len0), a = angle + (random() - .5)*jitter;
    ctx.strokeStyle = colors[(random()*colors.length) | 0];
    tiled(S, x, y, L, (cx, cy) => { ctx.beginPath(); ctx.moveTo(cx, cy); ctx.quadraticCurveTo(cx + Math.cos(a)*L*.5 + (random() - .5)*3, cy + Math.sin(a)*L*.5, cx + Math.cos(a)*L, cy + Math.sin(a)*L); ctx.stroke(); });
  }
}
function lines(ctx, S, step, alpha, dark = true, vertical = false, width = 1) {
  ctx.fillStyle = dark ? `rgba(16,18,12,${alpha})` : `rgba(255,246,220,${alpha})`;
  for (let t = 0; t < S; t += step) vertical ? ctx.fillRect(t, 0, width, S) : ctx.fillRect(0, t, S, width);
}
function twill(ctx, S, step, alpha) {
  ctx.strokeStyle = `rgba(16,15,10,${alpha})`; ctx.lineWidth = step*.45;
  for (let x = -S; x < 2*S; x += step) { ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x + S, S); ctx.stroke(); }
}

function paintSmooth(ctx, S, type, pal, random) {
  ctx.clearRect(0, 0, S, S);
  if (type === 'chainlink') {
    // galvanised wire diamonds on a transparent background (use with alphaTest / alpha-to-coverage)
    ctx.lineWidth = 2.2; ctx.lineCap = 'round';
    for (let k = -S; k < 2*S; k += S/8) {
      for (const [dx, col] of [[1.2, 'rgba(20,24,22,.7)'], [0, pal[0]]]) {
        ctx.strokeStyle = col;
        ctx.beginPath(); ctx.moveTo(k + dx, 0); ctx.lineTo(k + S + dx, S); ctx.stroke();
        ctx.beginPath(); ctx.moveTo(k + dx, S); ctx.lineTo(k + S + dx, 0); ctx.stroke();
      }
    }
    return;
  }
  const base = pal[0];
  ctx.fillStyle = base; ctx.fillRect(0, 0, S, S);
  switch (type) {
    case 'camo': {
      // woodland: big meandering blobs, light patches, dark branches; soft fabric shading on top
      // each patch is a cluster of overlapping lobes, so shapes stay irregular instead of banding on limbs
      const [, big, dark, light] = pal;
      const patch = (n, r0, r1, sx, sy, spin) => { for (let i = 0; i < n; i++) {
        const x = random()*S, y = random()*S, r = r0 + random()*(r1 - r0), rot = (random() - .5)*spin;
        for (let k = 0; k < 3; k++) { const a = random()*Math.PI*2, d = r*.55*random(); blob(ctx, S, random, x + Math.cos(a)*d*sx, y + Math.sin(a)*d*sy, r*(.5 + random()*.35), sx, sy, rot + (random() - .5)*.8, .7); }
      } };
      mottle(ctx, S, random, 10, 40, 90, .05, .06);
      ctx.fillStyle = big; patch(9, 26, 40, 1.25, .95, 1.2);
      ctx.fillStyle = light; patch(6, 14, 22, 1.2, .9, 1.4);
      ctx.fillStyle = dark; patch(10, 11, 17, 1.7, .6, 1.6);
      twill(ctx, S, 8, .04); mottle(ctx, S, random, 12, 30, 80, .05, .08);
      break;
    }
    case 'cloth': mottle(ctx, S, random, 16, 30, 90, .07, .1); twill(ctx, S, 8, .06); break;
    case 'webbing':
      mottle(ctx, S, random, 10, 40, 90, .05, .08); lines(ctx, S, 4, .13); lines(ctx, S, 4, .06, false, false, 1);
      ctx.fillStyle = 'rgba(16,18,12,.3)'; for (let y = 0; y < S; y += S/4) ctx.fillRect(0, y, S, 2);
      ctx.fillStyle = 'rgba(225,220,180,.16)'; for (let y = 0; y < S; y += S/4) for (let x = 0; x < S; x += 10) ctx.fillRect(x, y + 7, 6, 1.5);
      break;
    case 'skin': mottle(ctx, S, random, 14, 30, 80, .04, .05); ctx.fillStyle = 'rgba(170,70,50,.04)'; for (let i = 0; i < 8; i++) blob(ctx, S, random, random()*S, random()*S, 20 + random()*20); break;
    case 'steel':
      mottle(ctx, S, random, 12, 40, 90, .08, .1);
      strokes(ctx, S, random, 70, 20, 90, 1.2, ['rgba(230,232,215,.12)', 'rgba(20,24,22,.1)'], Math.PI/2, .08);
      { const g = ctx.createLinearGradient(0, S*.8, 0, S); g.addColorStop(0, 'rgba(26,33,30,0)'); g.addColorStop(1, 'rgba(26,33,30,.22)'); ctx.fillStyle = g; ctx.fillRect(0, 0, S, S); }
      break;
    case 'planks': case 'plywood': {
      const rows = type === 'planks' ? 4 : 1, h = S/rows;
      for (let r = 0; r < rows; r++) {
        ctx.fillStyle = random() > .5 ? 'rgba(255,236,200,.06)' : 'rgba(30,20,10,.08)'; ctx.fillRect(0, r*h, S, h);
        for (let k = 0; k < (type === 'planks' ? 7 : 22); k++) {
          const y0 = r*h + random()*h, amp = 1 + random()*3, f = (1 + (random()*3 | 0))*Math.PI*2/S;
          ctx.strokeStyle = random() > .4 ? 'rgba(40,26,12,.14)' : 'rgba(255,236,200,.1)'; ctx.lineWidth = .8 + random()*1.4;
          ctx.beginPath(); for (let x = 0; x <= S; x += 8) { const y = y0 + Math.sin(x*f + k)*amp; x ? ctx.lineTo(x, y) : ctx.moveTo(x, y); } ctx.stroke();
        }
        if (type === 'planks') {
          ctx.fillStyle = 'rgba(16,10,6,.55)'; ctx.fillRect(0, r*h, S, 2);
          ctx.fillStyle = 'rgba(255,230,190,.1)'; ctx.fillRect(0, r*h + 2, S, 2);
          ctx.fillStyle = 'rgba(30,30,30,.55)'; for (const x of [S*.12, S*.88]) { ctx.beginPath(); ctx.arc(x, r*h + h/2, 2, 0, Math.PI*2); ctx.fill(); }
        }
      }
      mottle(ctx, S, random, 8, 40, 100, .04, .06);
      break;
    }
    case 'straw':
      mottle(ctx, S, random, 10, 30, 80, .06, .08);
      strokes(ctx, S, random, 520, 10, 26, 1.6, ['rgba(255,236,170,.35)', 'rgba(110,84,36,.3)', 'rgba(220,190,110,.3)'], Math.PI/2, .7);
      break;
    case 'burlap':
      mottle(ctx, S, random, 12, 30, 90, .06, .09); lines(ctx, S, 4, .12); lines(ctx, S, 4, .08, true, true); lines(ctx, S, 4, .05, false, false, 1.5);
      break;
    case 'leaves':
      // grass and foliage: soft clumps plus short blades, light from the top-left
      mottle(ctx, S, random, 18, 30, 90, .06, .1);
      ctx.fillStyle = 'rgba(14,26,8,.08)'; for (let i = 0; i < 60; i++) blob(ctx, S, random, random()*S, random()*S, 5 + random()*9, 1.3, .8, random()*3);
      ctx.fillStyle = 'rgba(205,220,130,.06)'; for (let i = 0; i < 50; i++) blob(ctx, S, random, random()*S, random()*S, 4 + random()*7, 1.3, .8, random()*3);
      strokes(ctx, S, random, 1100, 5, 12, 1.1, ['rgba(200,215,120,.16)', 'rgba(16,30,10,.2)', 'rgba(120,140,60,.18)', 'rgba(60,84,30,.2)'], -Math.PI/2, 1.2);
      break;
    case 'concrete':
      mottle(ctx, S, random, 18, 30, 90, .07, .09);
      ctx.fillStyle = 'rgba(30,30,26,.28)'; for (let i = 0; i < 140; i++) { ctx.beginPath(); ctx.arc(random()*S, random()*S, .6 + random()*1.4, 0, Math.PI*2); ctx.fill(); }
      ctx.fillStyle = 'rgba(70,64,50,.07)'; for (let i = 0; i < 6; i++) blob(ctx, S, random, random()*S, random()*S, 18 + random()*26, 1.4, 1, random()*3);
      break;
    case 'gravel':
      mottle(ctx, S, random, 12, 30, 80, .06, .08);
      for (let i = 0; i < 260; i++) {
        const x = random()*S, y = random()*S, r = 2.5 + random()*4.5;
        ctx.fillStyle = random() > .5 ? 'rgba(235,228,205,.18)' : 'rgba(30,26,18,.2)'; blob(ctx, S, random, x, y, r, 1.2, .8, random()*3, .3);
      }
      break;
    case 'asphalt':
      mottle(ctx, S, random, 16, 30, 90, .05, .08);
      ctx.fillStyle = 'rgba(210,206,192,.12)'; for (let i = 0; i < 700; i++) { ctx.beginPath(); ctx.arc(random()*S, random()*S, .5 + random()*.9, 0, Math.PI*2); ctx.fill(); }
      break;
    case 'corrugated':
      for (let x = 0; x < S; x++) { const v = Math.sin(x/S*Math.PI*2*16); ctx.fillStyle = v > 0 ? `rgba(255,250,235,${v*.16})` : `rgba(10,12,10,${-v*.24})`; ctx.fillRect(x, 0, 1, S); }
      strokes(ctx, S, random, 30, 20, 70, 3, ['rgba(130,72,30,.18)', 'rgba(110,60,24,.14)'], Math.PI/2, .1);
      mottle(ctx, S, random, 10, 40, 90, .05, .07);
      break;
    case 'ammo':
      mottle(ctx, S, random, 12, 40, 90, .05, .08);
      ctx.fillStyle = 'rgba(10,12,8,.35)'; ctx.fillRect(0, 0, S, 6); ctx.fillRect(0, S - 6, S, 6);
      ctx.fillStyle = 'rgba(214,190,90,.9)'; ctx.textAlign = 'center'; ctx.font = 'bold 26px monospace';
      ctx.fillText('CTG 5.56MM', S/2, S*.44); ctx.font = 'bold 20px monospace'; ctx.fillText('M855  840 RDS', S/2, S*.6);
      break;
    case 'can': {
      const [, label, accent] = pal;
      ctx.fillStyle = label; ctx.fillRect(0, S*.125, S, S*.75);
      ctx.fillStyle = accent; ctx.fillRect(0, S*.34, S, S*.1); ctx.fillRect(S*.28, S*.5, S*.34, S*.22);
      ctx.fillStyle = 'rgba(255,255,255,.4)'; ctx.fillRect(0, S*.18, S, 5);
      for (let y = 0; y < S*.125; y += 6) { ctx.fillStyle = 'rgba(20,20,20,.18)'; ctx.fillRect(0, y, S, 2); ctx.fillRect(0, S*.875 + y, S, 2); }
      mottle(ctx, S, random, 8, 30, 80, .05, .06);
      break;
    }
    default: mottle(ctx, S, random, 16, 30, 100, .07, .09); mottle(ctx, S, random, 30, 6, 18, .03, .04);
  }
}

// Canvas textures; `paint` is reusable so cloth can be re-dyed.
// For 'camo' and 'can' the colour argument is a palette array.
export function makeTexture(color, type = 'plain', seed = 1729) {
  const smoothStyle = style.smooth, S = smoothStyle ? SMOOTH : 64;
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = S;
  const ctx = canvas.getContext('2d');
  const texture = new THREE.CanvasTexture(canvas);
  if (smoothStyle) { texture.magFilter = THREE.LinearFilter; texture.minFilter = THREE.LinearMipmapLinearFilter; texture.anisotropy = style.anisotropy; }
  else { texture.magFilter = THREE.NearestFilter; texture.minFilter = THREE.NearestMipmapNearestFilter; }
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.paint = c => {
    const random = makeRandom(seed);
    const pal = Array.isArray(c) ? c : [c];
    if (smoothStyle) { paintSmooth(ctx, S, type, pal, random); texture.needsUpdate = true; return; }
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
      // M81-style woodland at a readable scale: a few large blobs per tile and no pixel noise, so the
      // limbs keep their shading at game zoom (fine speckle turns into mush at 2× pixels); wraps seamlessly
      const [base, big, dark, light] = pal;
      ctx.fillStyle = base; ctx.fillRect(0, 0, 64, 64);
      ctx.fillStyle = big; for (let i = 0; i < 8; i++) smear(ctx, random, 5, 4, 6, 5, .55);
      ctx.fillStyle = light; for (let i = 0; i < 5; i++) smear(ctx, random, 3, 3, 4, 4, .6);
      ctx.fillStyle = dark; for (let i = 0; i < 8; i++) smear(ctx, random, 4, 2, 3, 4, .45);
      for (let y = 0; y < 64; y += 2) { ctx.fillStyle = 'rgba(15,18,12,.05)'; ctx.fillRect(0, y, 64, 1); }   // faint twill
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
      for (let i = 0; i < 160; i++) { ctx.fillStyle = random() > .5 ? 'rgba(235,228,205,.13)' : 'rgba(25,22,16,.17)'; ctx.fillRect(random()*64, random()*64, 2, 2); }
    } else if (type === 'asphalt') {
      for (let i = 0; i < 260; i++) { ctx.fillStyle = random() > .6 ? 'rgba(200,198,185,.08)' : 'rgba(5,5,5,.14)'; ctx.fillRect(random()*64, random()*64, 1, 1); }
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
  return new THREE.MeshStandardMaterial({ name, map, roughness: metalness ? .7 : .96, metalness, flatShading: !style.smooth });
}
