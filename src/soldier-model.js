import * as THREE from 'three';
import { makeMaterial, CAMO, style } from './textures.js';
import { createKit } from './rigid-kit.js';

// A 1993 U.S. Army rifleman, the soldier of Project Zomboid's Knox Event: M81 woodland BDU with
// rolled sleeves, PASGT helmet and vest, ALICE webbing, black boots and an M16A2.
// Built like the knight: primitive pieces rigidly bound to bones, baked into one SkinnedMesh.
// One unit is one metre, +Y up, the character faces +Z, its right hand is at -X.
// The rifle and its magazine are bones parented to the root; the rig places them every frame.

export const SOLE = { bottom: -.0905, heel: -.0765, toe: .1885, halfWidth: .056 };

// Rifle-space points (origin at the trigger, +Z to the muzzle, +Y up, +X the rifle's left side).
const P = (x, y, z) => new THREE.Vector3(x, y, z);
export const GUN = {
  butt: P(0, -.01, -.378), muzzle: P(0, .03, .61), sight: P(0, .108, -.075),
  eject: P(-.024, .036, -.01), ejectDir: P(-1, .45, -.3).normalize(),
  swivelF: P(0, -.006, .445), swivelR: P(0, -.088, -.33),
  magWell: P(0, -.032, .05),             // magazine bone origin when seated
  boltCatch: P(.025, .0, -.025)           // left-side bolt catch the support hand slaps
};
// Lightened palette for the inside-out fabric of rolled sleeves.
const washed = pal => pal.map(c => '#' + new THREE.Color(c).lerp(new THREE.Color('#d6cdb0'), .38).getHexString());

// opts.camo picks the uniform palette; opts.opfor adds the red armbands of a training aggressor.
export function createSoldier(opts = {}) {
  const pal0 = CAMO[opts.camo] || CAMO.woodland;
  const root = new THREE.Group(); root.name = opts.opfor ? 'OPFOR soldier' : 'Soldier';
  const { bones, joint, box, ellipsoid, rings, panel, profile, piece, bake } = createKit(root);
  const m = {
    camo: makeMaterial('BDU', pal0, 'camo', 0, 21),
    vest: makeMaterial('PASGT vest', pal0, 'camo', 0, 23),
    rolled: makeMaterial('Rolled sleeve', washed(pal0), 'camo', 0, 22),
    armband: makeMaterial('OPFOR armband', '#a8322a', 'cloth', 0, 40),
    web: makeMaterial('ALICE webbing', '#41442d', 'webbing', 0, 24),
    canvas: makeMaterial('Canteen cover', '#58593d', 'cloth', 0, 34),
    skin: makeMaterial('Skin', style.smooth ? '#94735f' : '#9e7155', 'skin', 0, 25),
    lips: makeMaterial('Lips', '#7a4f3e', 'skin', 0, 35),
    hair: makeMaterial('Hair', '#3b2d22', 'plain', 0, 30),
    dark: makeMaterial('Recesses', '#1c1f1d', 'plain', 0, 26),
    boot: makeMaterial('Boot leather', '#232321', 'cloth', .18, 27),
    sole: makeMaterial('Rubber sole', '#141413', 'plain', 0, 28),
    lace: makeMaterial('Laces', '#6f6650', 'plain', 0, 36),
    glove: makeMaterial('Leather gloves', '#272623', 'cloth', 0, 29),
    polymer: makeMaterial('Polymer furniture', '#25272a', 'plain', .12, 31),
    metal: makeMaterial('Parkerised steel', '#343836', 'steel', .45, 32),
    alu: makeMaterial('Anodised aluminium', '#3c3f3d', 'steel', .3, 33),
    band: makeMaterial('Helmet band', '#4a4c33', 'webbing', 0, 37),
    glow: makeMaterial('Cat eyes', '#9cbf82', 'plain', 0, 38),
    lens: makeMaterial('Goggle lens', '#3a4a4c', 'steel', .55, 39)
  };
  // one camo tile wraps each limb and the vest: big blobs read at 2× pixels, small ones turn to noise
  for (const k of ['camo', 'rolled', 'vest']) { const t = m[k].map; t.wrapS = t.wrapT = THREE.RepeatWrapping; t.repeat.set(1, .8); }
  // the helmet cover shares the uniform's texture but is open underneath, so it is double-sided
  m.cover = m.camo.clone(); m.cover.name = 'Helmet cover'; m.cover.side = THREE.DoubleSide;

  // Proportions follow the knight: a broad, chunky silhouette that keeps its shape at game zoom.
  const hips = joint('Hips', root, 0, .92, 0), spine = joint('Spine', hips, 0, .18, 0);
  const chest = joint('Chest', spine, 0, .20, 0), neck = joint('Neck', chest, 0, .21, 0);
  const head = joint('Head', neck, 0, .08, 0);
  const arms = {}, legs = {}, straps = {};
  const SIDES = [['R', -1], ['L', 1]];
  for (const [side, sign] of SIDES) {
    const upper = joint('UpperArm_'+side, chest, sign*.232, .14, 0);
    const fore = joint('Forearm_'+side, upper, sign*.008, -.28, 0);
    const hand = joint('Hand_'+side, fore, 0, -.265, 0);
    arms[side] = { upper, fore, hand };
    const thigh = joint('Thigh_'+side, hips, sign*.11, -.03, 0);
    const shin = joint('Shin_'+side, thigh, 0, -.395, 0);
    const foot = joint('Foot_'+side, shin, 0, -.37, 0);
    legs[side] = { thigh, shin, foot };
    straps[side] = joint('ChinStrap_'+side, head, sign*.146, .07, .012);
  }
  const canteen = joint('Canteen', hips, .17, .0, -.095); canteen.rotation.y = .95;
  const buttpack = joint('ButtPack', hips, 0, .03, -.145);
  const rifle = joint('Rifle', root, 0, 1.05, .35);
  const mag = joint('Magazine', root, 0, 1.0, .4);

  // ---------- head: face under a PASGT helmet ----------
  rings('Neck', [[-.04,.058,.062],[.085,.054,.058]], m.skin, neck);
  ellipsoid('Skull', [.091,.11,.104], m.skin, head, [0,.1,.01], 10);
  ellipsoid('Jaw', [.074,.05,.072], m.skin, head, [0,.042,.038]);
  box('Nose', [.024,.04,.028], m.skin, head, [0,.096,.112]);
  box('Mouth', [.04,.009,.008], m.lips, head, [0,.056,.104]);
  for (const s of [-1, 1]) {
    box('Eye', [.026,.015,.01], m.dark, head, [s*.035,.118,.1]);
    box('Brow', [.034,.011,.014], m.hair, head, [s*.035,.136,.101]);
    ellipsoid('Ear', [.014,.03,.022], m.skin, head, [s*.093,.1,0], 6);
  }
  box('Nape hair', [.13,.05,.05], m.hair, head, [0,.075,-.074]);
  // PASGT shell: an elliptic dome whose rim rises over the brow and drops over the ears and nape,
  // with the skirt flaring out at the back and a visor lip at the front.
  const HC = [0, .13, -.008], HR = { x: .152, y: .126, z: .168 };
  {
    const rim = th => -.05375 + .0525*Math.cos(th) + .02125*Math.cos(2*th);
    const segs = 16, arcRows = 4, p = [], uv = [], idx = [];
    for (let i = 0; i <= segs; i++) {
      const th = i/segs*Math.PI*2, sx = Math.sin(th), cz = Math.cos(th);
      const R = 1/Math.hypot(sx/HR.x, cz/HR.z), yr = rim(th);
      const phiEnd = Math.acos(THREE.MathUtils.clamp(Math.max(yr, 0)/HR.y, 0, 1));
      const prof = [];
      for (let j = 0; j <= arcRows; j++) { const ph = phiEnd*j/arcRows; prof.push([R*Math.sin(ph), HR.y*Math.cos(ph)]); }
      const flare = .07 + .03*Math.max(0, -cz), [r0, y0] = prof[prof.length-1];
      prof.push(yr < 0 ? [r0*(1 + flare*.6), yr] : [r0*1.01, y0 - .004]);
      const lip = .07 + .09*Math.max(0, cz)**2;
      prof.push([prof[prof.length-1][0]*(1 + lip), Math.min(yr, prof[prof.length-1][1]) - .006 - .01*Math.max(0, cz)]);
      prof.forEach(([r, y], j) => { p.push(sx*r, y, cz*r); uv.push(i/segs, j/(prof.length-1)); });
    }
    const rows = arcRows + 3;
    for (let i = 0; i < segs; i++) for (let j = 0; j < rows - 1; j++) {
      const a = i*rows + j, b = (i+1)*rows + j; idx.push(a, a+1, b, b, a+1, b+1);
    }
    const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(p, 3)); g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2)); g.setIndex(idx);
    piece('PASGT shell', g.toNonIndexed(), m.cover, head, HC);
  }
  // band, cat-eye patches and goggles follow the shell's tilt: higher at the brow, lower at the nape
  rings('Helmet band', [[0,HR.x+.005,HR.z+.005],[.026,HR.x+.003,HR.z+.003]], m.band, head, [HC[0],HC[1]+.012,HC[2]], 16, [-.24,0,0]);
  for (const s of [-1, 1]) box('Cat eye', [.026,.009,.005], m.glow, head, [s*.036,HC[1]-.02,HC[2]-HR.z-.007], [.25,0,0]);
  rings('Goggle strap', [[0,HR.x*.97+.005,HR.z*.97+.005],[.016,HR.x*.95+.005,HR.z*.95+.005]], m.dark, head, [HC[0],HC[1]+.048,HC[2]], 16, [-.24,0,0]);
  box('Goggle frame', [.134,.042,.02], m.dark, head, [0,HC[1]+.094,HC[2]+.142], [-.62,0,0]);
  for (const s of [-1, 1]) box('Goggle lens', [.052,.03,.008], m.lens, head, [s*.033,HC[1]+.098,HC[2]+.154], [-.62,0,0]);
  for (const side of ['L', 'R']) {
    box('Chin strap', [.008,.11,.016], m.band, straps[side], [0,-.055,0]);
    box('Strap buckle', [.012,.016,.02], m.metal, straps[side], [0,-.11,0]);
  }

  // ---------- torso: a bulky PASGT vest over the BDU, ALICE suspenders ----------
  rings('Vest', [[-.37,.19,.142],[-.22,.207,.152],[-.03,.218,.158],[.1,.214,.15],[.17,.182,.124]], m.vest, chest);
  rings('Vest collar', [[.14,.146,.124,-.012],[.19,.13,.11,-.02],[.218,.112,.098,-.026]], m.vest, chest);
  box('Vest seam', [.014,.34,.01], m.dark, chest, [0,-.13,.158]);
  box('Storm flap', [.066,.35,.014], m.vest, chest, [.028,-.13,.161]);
  box('Vest hem', [.4,.03,.29], m.vest, chest, [0,-.365,.0]);
  for (const s of [-1, 1]) {
    ellipsoid('Shoulder pad', [.092,.042,.1], m.vest, chest, [s*.165,.166,-.006]);
    box('Suspender', [.05,.38,.012], m.web, chest, [s*.105,-.125,.16], [0,0,-s*.07]);
    box('Suspender back', [.05,.42,.012], m.web, chest, [s*.074,-.13,-.155], [0,0,s*.26]);
    box('Suspender buckle', [.04,.026,.016], m.metal, chest, [s*.11,.03,.168]);
  }
  box('First aid pouch', [.08,.084,.048], m.web, chest, [.108,.035,.182]);
  box('First aid flap', [.083,.028,.05], m.web, chest, [.108,.082,.183]);
  // pistol belt with two 30-round pouches, canteen, bayonet and butt pack
  rings('Trousers seat', [[-.12,.162,.12],[0,.176,.128],[.06,.175,.127]], m.camo, hips);
  rings('Pistol belt', [[0,.184,.138],[.056,.184,.138]], m.web, hips);
  box('Belt buckle', [.06,.042,.014], m.metal, hips, [0,.028,.142]);
  for (const s of [-1, 1]) {
    box('Ammo pouch', [.088,.112,.064], m.web, hips, [s*.096,-.008,.158]);
    box('Pouch flap', [.092,.036,.068], m.web, hips, [s*.096,.044,.16]);
    box('Pouch snap', [.016,.016,.008], m.metal, hips, [s*.096,.036,.196]);
  }
  box('Bayonet scabbard', [.04,.22,.028], m.polymer, hips, [.19,-.1,.03], [.1,0,.12]);
  box('Bayonet grip', [.032,.085,.032], m.polymer, hips, [.176,.035,.034], [.1,0,.12]);
  ellipsoid('Canteen cover', [.064,.088,.046], m.canvas, canteen, [0,-.08,0]);
  box('Canteen cap', [.026,.024,.026], m.polymer, canteen, [0,.0,0]);
  box('Canteen clip', [.05,.02,.012], m.web, canteen, [0,-.004,.034]);
  box('Butt pack', [.25,.145,.1], m.web, buttpack, [0,-.075,-.046]);
  box('Butt pack lid', [.256,.05,.106], m.web, buttpack, [0,-.014,-.046]);
  for (const s of [-1, 1]) box('Butt pack strap', [.03,.11,.008], m.dark, buttpack, [s*.068,-.055,-.1]);

  // ---------- arms: rolled sleeves, bare forearms, leather gloves ----------
  for (const [side, sign] of SIDES) {
    const { upper, fore, hand } = arms[side];
    ellipsoid('Deltoid', [.088,.09,.088], m.camo, upper, [sign*.01,-.028,0]);
    rings('Sleeve', [[-.215,.066,.068],[-.09,.074,.076],[0,.082,.082]], m.camo, upper);
    rings('Rolled cuff', [[-.262,.071,.073],[-.2,.076,.078]], m.rolled, upper);
    box(side === 'L' ? 'Unit patch' : 'Flag patch', [.008,.05,.044], m.dark, upper, [sign*.085,-.085,0]);
    if (opts.opfor) rings('Armband', [[-.16,.08,.082],[-.115,.083,.085]], m.armband, upper);
    ellipsoid('Elbow', [.058,.058,.058], m.skin, fore, [0,-.01,0], 6);
    rings('Forearm', [[-.245,.044,.047],[-.13,.055,.058],[-.02,.058,.06]], m.skin, fore);
    if (side === 'L') {
      rings('Watch strap', [[-.24,.05,.052],[-.214,.05,.052]], m.polymer, fore);
      box('Watch face', [.01,.028,.028], m.metal, fore, [sign*.051,-.227,0]);
    }
    rings('Glove cuff', [[-.014,.046,.049],[.012,.048,.051]], m.glove, hand);
    ellipsoid('Glove', [.042,.066,.053], m.glove, hand, [0,-.054,.004], 8);
    box('Fingers', [.038,.056,.066], m.glove, hand, [-sign*.01,-.094,.016]);
    ellipsoid('Thumb', [.018,.035,.019], m.glove, hand, [-sign*.027,-.05,.04], 6);
  }

  // ---------- legs: BDU trousers bloused into black boots ----------
  for (const [side, sign] of SIDES) {
    const { thigh, shin, foot } = legs[side];
    rings('Trouser leg', [[-.4,.077,.081],[-.24,.097,.101],[-.05,.11,.11],[.03,.11,.11]], m.camo, thigh);
    box('Cargo pocket', [.044,.15,.132], m.camo, thigh, [sign*.098,-.19,0]);
    box('Pocket flap', [.048,.04,.138], m.camo, thigh, [sign*.1,-.106,0]);
    ellipsoid('Knee', [.078,.078,.08], m.camo, shin, [0,.012,.014]);
    rings('Trouser shin', [[-.22,.068,.07],[-.08,.075,.078],[0,.08,.083]], m.camo, shin);
    rings('Bloused cuff', [[-.258,.074,.077],[-.2,.08,.084]], m.camo, shin);
    rings('Boot shaft', [[-.375,.06,.066],[-.24,.066,.07]], m.boot, shin);
    for (let i = 0; i < 3; i++) box('Boot lace', [.05,.01,.01], m.lace, shin, [0,-.27 - i*.034,.067]);
    ellipsoid('Boot', [.064,.062,.14], m.boot, foot, [0,-.034,.058]);
    ellipsoid('Toe cap', [.058,.05,.066], m.boot, foot, [0,-.044,.128]);
    box('Sole', [.13,.036,.28], m.sole, foot, [0,-.0725,.056]);
    box('Heel', [.116,.028,.086], m.sole, foot, [0,-.056,-.038]);
    for (let i = 0; i < 2; i++) box('Instep lace', [.044,.01,.014], m.lace, foot, [0,.01 - i*.022,.034 + i*.038], [-.5,0,0]);
  }

  // ---------- M16A2: side profiles (z, y) extruded across the rifle ----------
  const along = [Math.PI/2, 0, 0];   // rings built along +Y, turned to run along the barrel (+Z)
  profile('Buttstock', [[-.11,.036],[-.372,.036],[-.372,-.098],[-.355,-.103],[-.24,-.062],[-.11,-.022]], .05, m.polymer, rifle);
  box('Buttplate', [.054,.138,.014], m.dark, rifle, [0,-.031,-.377]);
  box('Stock swivel', [.01,.018,.022], m.metal, rifle, [0,-.092,-.33]);
  box('Lower receiver', [.038,.044,.21], m.alu, rifle, [0,-.012,-.005]);
  box('Upper receiver', [.04,.05,.225], m.alu, rifle, [0,.03,-.002]);
  box('Magazine well', [.043,.05,.075], m.alu, rifle, [0,-.042,.07]);
  profile('Pistol grip', [[-.022,-.03],[.002,-.03],[-.034,-.122],[-.064,-.126],[-.048,-.072],[-.04,-.03]], .034, m.polymer, rifle);
  box('Trigger guard', [.008,.007,.062], m.alu, rifle, [0,-.062,.012]);
  box('Trigger', [.004,.02,.006], m.metal, rifle, [0,-.04,.004], [.3,0,0]);
  box('Rear sight', [.033,.042,.034], m.alu, rifle, [0,.094,-.074]);
  box('Carry handle', [.028,.018,.15], m.alu, rifle, [0,.092,-.012]);
  box('Handle front', [.028,.036,.024], m.alu, rifle, [0,.07,.05]);
  box('Charging handle', [.05,.012,.014], m.metal, rifle, [0,.05,-.105]);
  box('Forward assist', [.016,.018,.034], m.metal, rifle, [-.026,.038,-.07]);
  box('Ejection port', [.004,.024,.056], m.dark, rifle, [-.0205,.035,-.008]);
  box('Brass deflector', [.014,.024,.016], m.alu, rifle, [-.023,.04,-.04]);
  box('Bolt catch', [.008,.032,.014], m.metal, rifle, [.021,.0,-.025]);
  box('Selector', [.008,.01,.018], m.metal, rifle, [.021,-.012,-.045]);
  rings('Delta ring', [[0,.041,.041],[.018,.041,.041]], m.metal, rifle, [0,.03,.108], 8, along);
  {
    const ribs = [];
    for (let k = 0; k <= 7; k++) { const z = k*.036, r = .034 - k*.0005; ribs.push([z, r, r], [z + .006, r + .003, r + .003], [z + .03, r + .003, r + .003], [z + .036, r, r]); }
    ribs.splice(ribs.length - 3);
    rings('Handguard', ribs, m.polymer, rifle, [0,.03,.116], 8, along);
  }
  rings('Handguard cap', [[0,.028,.028],[.014,.026,.026]], m.metal, rifle, [0,.03,.372], 8, along);
  rings('Barrel', [[0,.0125,.0125],[.2,.011,.011]], m.metal, rifle, [0,.03,.385], 6, along);
  box('Front sight base', [.03,.038,.036], m.metal, rifle, [0,.028,.446]);
  profile('Front sight', [[-.015,.04],[.015,.04],[.004,.088],[.002,.1],[-.002,.1],[-.004,.088]], .016, m.metal, rifle, [0,0,.446], .001);
  box('Bayonet lug', [.012,.018,.02], m.metal, rifle, [0,.004,.452]);
  box('Front swivel', [.008,.02,.014], m.metal, rifle, [0,-.004,.445]);
  rings('Flash hider', [[0,.0145,.0145],[.05,.0145,.0145]], m.dark, rifle, [0,.03,.56], 6, along);
  // 30-round STANAG magazine, curved forward; origin at the top rear of the magazine
  profile('Magazine', [[0,0],[.062,0],[.067,-.07],[.088,-.168],[.034,-.188],[.008,-.096]], .03, m.alu, mag, [0,0,0], .002);
  profile('Mag floorplate', [[.03,-.18],[.09,-.162],[.094,-.172],[.034,-.194]], .034, m.dark, mag, [0,0,0], .001);
  // seat the magazine in the well for the bind pose
  mag.position.copy(rifle.position).add(GUN.magWell);

  // Rifle sling: a free strap between the front and stock swivels, simulated by the rig (verlet).
  const SLING_N = 12;
  const slingGeo = new THREE.BufferGeometry();
  slingGeo.setAttribute('position', new THREE.Float32BufferAttribute(new Float32Array(SLING_N*2*3), 3));
  const sIdx = []; for (let i = 0; i < SLING_N - 1; i++) { const a = i*2; sIdx.push(a, a+1, a+2, a+1, a+3, a+2); }
  const sUv = []; for (let i = 0; i < SLING_N; i++) sUv.push(0, i/SLING_N*4, .3, i/SLING_N*4);
  slingGeo.setIndex(sIdx); slingGeo.setAttribute('uv', new THREE.Float32BufferAttribute(sUv, 2));
  const slingMat = new THREE.MeshStandardMaterial({ map: m.web.map, roughness: .96, side: THREE.DoubleSide, flatShading: !style.smooth });
  const sling = new THREE.Mesh(slingGeo, slingMat); sling.name = 'Sling'; sling.castShadow = true; sling.frustumCulled = false;

  const { skin, geometry } = bake('Soldier', { crease: style.smooth ? 1.0 : 0 });

  return {
    root, skin, bones, materials: m, sling, slingN: SLING_N,
    j: { hips, spine, chest, neck, head, arms, legs, straps, canteen, buttpack, rifle, mag },
    triangles: geometry.attributes.position.count / 3,
    dye(key) {
      const pal = CAMO[key] || CAMO.woodland;
      m.camo.map.paint(pal); m.vest.map.paint(pal); m.rolled.map.paint(washed(pal));
    }
  };
}

// A loose magazine with the same silhouette, for drops and the ammo HUD.
export function magazineMesh(materials) {
  const g = new THREE.Group();
  const kit = createKit(g);
  const b = kit.joint('M', g, 0, 0, 0);
  kit.profile('Magazine', [[0,0],[.062,0],[.067,-.07],[.088,-.168],[.034,-.188],[.008,-.096]], .03, materials.alu, b, [0,0,0], .002);
  kit.profile('Mag floorplate', [[.03,-.18],[.09,-.162],[.094,-.172],[.034,-.194]], .034, materials.dark, b, [0,0,0], .001);
  const geos = kit.pieces.map(({ mesh }) => { mesh.updateMatrix(); return mesh.geometry.clone().applyMatrix4(mesh.matrix); });
  const meshes = kit.pieces.map(({ mesh }, i) => new THREE.Mesh(geos[i], mesh.material));
  b.removeFromParent();
  for (const x of meshes) { x.castShadow = true; g.add(x); }
  return g;
}
