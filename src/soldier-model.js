import * as THREE from 'three';
import { makeMaterial, CAMO } from './textures.js';
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
  eject: P(-.019, .036, -.01), ejectDir: P(-1, .45, -.3).normalize(),
  swivelF: P(0, -.006, .445), swivelR: P(0, -.088, -.33),
  magWell: P(0, -.032, .05),             // magazine bone origin when seated
  boltCatch: P(.02, .0, -.025)           // left-side bolt catch the support hand slaps
};
// Lightened palette for the inside-out fabric of rolled sleeves.
const washed = pal => pal.map(c => '#' + new THREE.Color(c).lerp(new THREE.Color('#d6cdb0'), .38).getHexString());

export function createSoldier() {
  const root = new THREE.Group(); root.name = 'Soldier';
  const { bones, joint, box, ellipsoid, rings, panel, profile, piece, bake } = createKit(root);
  const m = {
    camo: makeMaterial('BDU woodland', CAMO.woodland, 'camo', 0, 21),
    vest: makeMaterial('PASGT vest', CAMO.woodland, 'camo', 0, 23),
    rolled: makeMaterial('Rolled sleeve', washed(CAMO.woodland), 'camo', 0, 22),
    web: makeMaterial('ALICE webbing', '#4d5034', 'webbing', 0, 24),
    canvas: makeMaterial('Canteen cover', '#58593d', 'cloth', 0, 34),
    skin: makeMaterial('Skin', '#9e7155', 'skin', 0, 25),
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
  // finer camo: the 64px pattern repeats around limbs and the vest
  for (const [k, r] of [['camo', 2], ['rolled', 2], ['vest', 2]]) { const t = m[k].map; t.wrapS = t.wrapT = THREE.RepeatWrapping; t.repeat.set(r, r*.8); }
  // the helmet cover shares the uniform's texture but is open underneath, so it is double-sided
  m.cover = m.camo.clone(); m.cover.name = 'Helmet cover'; m.cover.side = THREE.DoubleSide;

  const hips = joint('Hips', root, 0, .92, 0), spine = joint('Spine', hips, 0, .18, 0);
  const chest = joint('Chest', spine, 0, .20, 0), neck = joint('Neck', chest, 0, .21, 0);
  const head = joint('Head', neck, 0, .08, 0);
  const arms = {}, legs = {}, straps = {};
  const SIDES = [['R', -1], ['L', 1]];
  for (const [side, sign] of SIDES) {
    const upper = joint('UpperArm_'+side, chest, sign*.2, .14, 0);
    const fore = joint('Forearm_'+side, upper, 0, -.28, 0);
    const hand = joint('Hand_'+side, fore, 0, -.265, 0);
    arms[side] = { upper, fore, hand };
    const thigh = joint('Thigh_'+side, hips, sign*.105, -.03, 0);
    const shin = joint('Shin_'+side, thigh, 0, -.395, 0);
    const foot = joint('Foot_'+side, shin, 0, -.37, 0);
    legs[side] = { thigh, shin, foot };
    straps[side] = joint('ChinStrap_'+side, head, sign*.128, .072, .012);
  }
  const canteen = joint('Canteen', hips, .15, .0, -.085); canteen.rotation.y = .95;
  const buttpack = joint('ButtPack', hips, 0, .03, -.13);
  const rifle = joint('Rifle', root, 0, 1.05, .35);
  const mag = joint('Magazine', root, 0, 1.0, .4);

  // ---------- head: face under a PASGT helmet ----------
  rings('Neck', [[-.04,.051,.055],[.085,.047,.051]], m.skin, neck);
  ellipsoid('Skull', [.083,.103,.097], m.skin, head, [0,.098,.008], 10);
  ellipsoid('Jaw', [.066,.046,.066], m.skin, head, [0,.042,.034]);
  box('Nose', [.02,.036,.024], m.skin, head, [0,.094,.104]);
  box('Mouth', [.034,.007,.008], m.lips, head, [0,.056,.096]);
  for (const s of [-1, 1]) {
    box('Eye', [.022,.012,.008], m.dark, head, [s*.032,.116,.093]);
    box('Brow', [.03,.009,.012], m.hair, head, [s*.032,.133,.094]);
    ellipsoid('Ear', [.012,.028,.02], m.skin, head, [s*.085,.1,0], 6);
  }
  box('Nape hair', [.12,.05,.045], m.hair, head, [0,.075,-.068]);
  // PASGT shell: an elliptic dome whose rim rises over the brow and drops over the ears and nape,
  // with the skirt flaring out at the back and a visor lip at the front.
  const HC = [0, .126, -.008], HR = { x: .134, y: .114, z: .15 };
  {
    const rim = th => -.05375 + .0525*Math.cos(th) + .02125*Math.cos(2*th);
    const segs = 14, arcRows = 4, p = [], uv = [], idx = [];
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
      prof.forEach(([r, y], j) => { p.push(sx*r, y, cz*r); uv.push(i/segs*2, j/(prof.length-1)); });
    }
    const rows = arcRows + 3;
    for (let i = 0; i < segs; i++) for (let j = 0; j < rows - 1; j++) {
      const a = i*rows + j, b = (i+1)*rows + j; idx.push(a, a+1, b, b, a+1, b+1);
    }
    const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(p, 3)); g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2)); g.setIndex(idx);
    piece('PASGT shell', g.toNonIndexed(), m.cover, head, HC);
  }
  // band, cat-eye patches and goggles follow the shell's tilt: higher at the brow, lower at the nape
  rings('Helmet band', [[0,HR.x+.004,HR.z+.004],[.022,HR.x+.002,HR.z+.002]], m.band, head, [HC[0],HC[1]+.012,HC[2]], 14, [-.24,0,0]);
  for (const s of [-1, 1]) box('Cat eye', [.022,.007,.004], m.glow, head, [s*.032,HC[1]-.02,HC[2]-HR.z-.006], [.25,0,0]);
  rings('Goggle strap', [[0,HR.x*.97+.004,HR.z*.97+.004],[.014,HR.x*.95+.004,HR.z*.95+.004]], m.dark, head, [HC[0],HC[1]+.045,HC[2]], 14, [-.24,0,0]);
  box('Goggle frame', [.118,.036,.018], m.dark, head, [0,HC[1]+.088,HC[2]+.128], [-.62,0,0]);
  for (const s of [-1, 1]) box('Goggle lens', [.046,.026,.008], m.lens, head, [s*.029,HC[1]+.092,HC[2]+.139], [-.62,0,0]);
  for (const side of ['L', 'R']) {
    box('Chin strap', [.006,.1,.014], m.band, straps[side], [0,-.05,0]);
    box('Strap buckle', [.01,.014,.017], m.metal, straps[side], [0,-.1,0]);
  }

  // ---------- torso: PASGT vest over the BDU, ALICE suspenders ----------
  rings('Vest', [[-.37,.172,.128],[-.22,.188,.138],[-.03,.198,.143],[.1,.196,.136],[.17,.168,.114]], m.vest, chest);
  rings('Vest collar', [[.14,.132,.112,-.012],[.19,.118,.1,-.02],[.215,.1,.088,-.026]], m.vest, chest);
  box('Vest seam', [.012,.33,.008], m.dark, chest, [0,-.13,.144]);
  box('Storm flap', [.058,.34,.012], m.vest, chest, [.024,-.13,.148]);
  for (const s of [-1, 1]) {
    ellipsoid('Shoulder pad', [.075,.032,.088], m.vest, chest, [s*.148,.162,-.006]);
    for (let i = 0; i < 3; i++) box('Side lacing', [.004,.008,.04], m.dark, chest, [s*.199,-.3 + i*.09,-.02]);
    box('Suspender', [.038,.37,.009], m.web, chest, [s*.098,-.125,.146], [0,0,-s*.07]);
    box('Suspender back', [.038,.4,.009], m.web, chest, [s*.068,-.13,-.141], [0,0,s*.26]);
    box('Suspender buckle', [.03,.02,.012], m.metal, chest, [s*.102,.025,.152]);
  }
  box('First aid pouch', [.064,.072,.038], m.web, chest, [.1,.03,.166]);
  box('First aid flap', [.066,.022,.04], m.web, chest, [.1,.07,.167]);
  // pistol belt with two 30-round pouches, canteen, bayonet and butt pack
  rings('Trousers seat', [[-.12,.148,.108],[0,.162,.116],[.06,.16,.114]], m.camo, hips);
  rings('Pistol belt', [[0,.168,.124],[.052,.168,.124]], m.web, hips);
  box('Belt buckle', [.05,.036,.012], m.metal, hips, [0,.026,.127]);
  for (const s of [-1, 1]) {
    box('Ammo pouch', [.074,.1,.052], m.web, hips, [s*.088,-.004,.142]);
    box('Pouch flap', [.077,.03,.055], m.web, hips, [s*.088,.04,.143]);
    box('Pouch snap', [.012,.012,.006], m.metal, hips, [s*.088,.034,.172]);
    box('Belt keeper', [.016,.05,.01], m.metal, hips, [s*.15,.026,.085], [0,s*.6,0]);
  }
  box('Bayonet scabbard', [.032,.21,.022], m.polymer, hips, [.172,-.1,.03], [.1,0,.12]);
  box('Bayonet grip', [.026,.08,.026], m.polymer, hips, [.159,.03,.034], [.1,0,.12]);
  ellipsoid('Canteen cover', [.056,.078,.04], m.canvas, canteen, [0,-.075,0]);
  box('Canteen cap', [.022,.02,.022], m.polymer, canteen, [0,.0,0]);
  box('Canteen clip', [.045,.016,.01], m.web, canteen, [0,-.004,.03]);
  box('Butt pack', [.22,.13,.09], m.web, buttpack, [0,-.07,-.042]);
  box('Butt pack lid', [.224,.045,.094], m.web, buttpack, [0,-.012,-.042]);
  for (const s of [-1, 1]) box('Butt pack strap', [.024,.1,.006], m.dark, buttpack, [s*.06,-.05,-.09]);

  // ---------- arms: rolled sleeves, bare forearms, leather gloves ----------
  for (const [side, sign] of SIDES) {
    const { upper, fore, hand } = arms[side];
    ellipsoid('Deltoid', [.072,.078,.076], m.camo, upper, [sign*.008,-.025,0]);
    rings('Sleeve', [[-.21,.056,.058],[-.09,.062,.064],[0,.068,.07]], m.camo, upper);
    rings('Rolled cuff', [[-.255,.06,.062],[-.2,.064,.066]], m.rolled, upper);
    box(side === 'L' ? 'Unit patch' : 'Flag patch', [.006,.04,.036], m.dark, upper, [sign*.071,-.08,0]);
    ellipsoid('Elbow', [.05,.05,.05], m.skin, fore, [0,-.01,0], 6);
    rings('Forearm', [[-.245,.037,.04],[-.13,.047,.05],[-.02,.05,.052]], m.skin, fore);
    if (side === 'L') {
      rings('Watch strap', [[-.238,.043,.045],[-.218,.043,.045]], m.polymer, fore);
      box('Watch face', [.008,.022,.022], m.metal, fore, [sign*.044,-.228,0]);
    }
    rings('Glove cuff', [[-.012,.039,.042],[.012,.041,.044]], m.glove, hand);
    ellipsoid('Glove', [.034,.058,.045], m.glove, hand, [0,-.052,.004], 6);
    box('Fingers', [.03,.048,.058], m.glove, hand, [-sign*.008,-.088,.014]);
    ellipsoid('Thumb', [.015,.03,.016], m.glove, hand, [-sign*.022,-.048,.034], 6);
  }

  // ---------- legs: BDU trousers bloused into black boots ----------
  for (const [side, sign] of SIDES) {
    const { thigh, shin, foot } = legs[side];
    rings('Trouser leg', [[-.4,.066,.07],[-.24,.085,.09],[-.05,.1,.1],[.03,.1,.1]], m.camo, thigh);
    box('Cargo pocket', [.036,.13,.12], m.camo, thigh, [sign*.086,-.19,0]);
    box('Pocket flap', [.04,.034,.126], m.camo, thigh, [sign*.089,-.115,0]);
    ellipsoid('Knee', [.068,.07,.072], m.camo, shin, [0,.012,.012]);
    rings('Trouser shin', [[-.22,.06,.062],[-.08,.066,.07],[0,.07,.074]], m.camo, shin);
    rings('Bloused cuff', [[-.255,.064,.066],[-.205,.069,.073]], m.camo, shin);
    rings('Boot shaft', [[-.375,.052,.058],[-.24,.057,.061]], m.boot, shin);
    for (let i = 0; i < 4; i++) box('Boot lace', [.042,.006,.008], m.lace, shin, [0,-.268 - i*.026,.058]);
    ellipsoid('Boot', [.055,.056,.13], m.boot, foot, [0,-.036,.058]);
    ellipsoid('Toe cap', [.05,.042,.06], m.boot, foot, [0,-.046,.128]);
    box('Sole', [.112,.03,.27], m.sole, foot, [0,-.0755,.056]);
    box('Heel', [.1,.022,.078], m.sole, foot, [0,-.058,-.04]);
    for (let i = 0; i < 3; i++) box('Instep lace', [.036,.006,.012], m.lace, foot, [0,.006 - i*.017,.03 + i*.03], [-.5,0,0]);
  }

  // ---------- M16A2: side profiles (z, y) extruded across the rifle ----------
  const along = [Math.PI/2, 0, 0];   // rings built along +Y, turned to run along the barrel (+Z)
  profile('Buttstock', [[-.11,.036],[-.372,.036],[-.372,-.098],[-.355,-.103],[-.24,-.062],[-.11,-.022]], .038, m.polymer, rifle);
  box('Buttplate', [.042,.138,.012], m.dark, rifle, [0,-.031,-.377]);
  box('Stock swivel', [.008,.016,.02], m.metal, rifle, [0,-.092,-.33]);
  box('Lower receiver', [.028,.042,.21], m.alu, rifle, [0,-.012,-.005]);
  box('Upper receiver', [.03,.046,.225], m.alu, rifle, [0,.03,-.002]);
  box('Magazine well', [.033,.05,.075], m.alu, rifle, [0,-.042,.07]);
  profile('Pistol grip', [[-.022,-.03],[.002,-.03],[-.034,-.122],[-.064,-.126],[-.048,-.072],[-.04,-.03]], .026, m.polymer, rifle);
  box('Trigger guard', [.006,.005,.062], m.alu, rifle, [0,-.062,.012]);
  box('Trigger', [.004,.02,.006], m.metal, rifle, [0,-.04,.004], [.3,0,0]);
  box('Rear sight', [.026,.04,.032], m.alu, rifle, [0,.094,-.074]);
  box('Carry handle', [.022,.016,.15], m.alu, rifle, [0,.092,-.012]);
  box('Handle front', [.022,.036,.022], m.alu, rifle, [0,.07,.05]);
  box('Charging handle', [.04,.01,.012], m.metal, rifle, [0,.05,-.105]);
  box('Forward assist', [.014,.016,.034], m.metal, rifle, [-.02,.038,-.07]);
  box('Ejection port', [.003,.02,.052], m.dark, rifle, [-.0155,.035,-.008]);
  box('Brass deflector', [.012,.022,.014], m.alu, rifle, [-.018,.04,-.04]);
  box('Bolt catch', [.006,.03,.012], m.metal, rifle, [.016,.0,-.025]);
  box('Selector', [.006,.008,.016], m.metal, rifle, [.016,-.012,-.045]);
  rings('Delta ring', [[0,.034,.034],[.016,.034,.034]], m.metal, rifle, [0,.03,.108], 8, along);
  {
    const ribs = [];
    for (let k = 0; k <= 7; k++) { const z = k*.036, r = .028 - k*.0004; ribs.push([z, r, r], [z + .006, r + .0025, r + .0025], [z + .03, r + .0025, r + .0025], [z + .036, r, r]); }
    ribs.splice(ribs.length - 3);
    rings('Handguard', ribs, m.polymer, rifle, [0,.03,.116], 8, along);
  }
  rings('Handguard cap', [[0,.023,.023],[.014,.021,.021]], m.metal, rifle, [0,.03,.372], 8, along);
  rings('Barrel', [[0,.0095,.0095],[.2,.0085,.0085]], m.metal, rifle, [0,.03,.385], 6, along);
  box('Front sight base', [.024,.034,.034], m.metal, rifle, [0,.028,.446]);
  profile('Front sight', [[-.015,.04],[.015,.04],[.004,.088],[.002,.1],[-.002,.1],[-.004,.088]], .012, m.metal, rifle, [0,0,.446], .001);
  box('Bayonet lug', [.01,.016,.02], m.metal, rifle, [0,.004,.452]);
  box('Front swivel', [.006,.018,.012], m.metal, rifle, [0,-.004,.445]);
  rings('Flash hider', [[0,.0115,.0115],[.05,.0115,.0115]], m.dark, rifle, [0,.03,.56], 6, along);
  // 30-round STANAG magazine, curved forward; origin at the top rear of the magazine
  profile('Magazine', [[0,0],[.062,0],[.067,-.07],[.088,-.168],[.034,-.188],[.008,-.096]], .023, m.alu, mag, [0,0,0], .002);
  profile('Mag floorplate', [[.03,-.18],[.09,-.162],[.094,-.172],[.034,-.194]], .026, m.dark, mag, [0,0,0], .001);
  // seat the magazine in the well for the bind pose
  mag.position.copy(rifle.position).add(GUN.magWell);

  // Rifle sling: a free strap between the front and stock swivels, simulated by the rig (verlet).
  const SLING_N = 12;
  const slingGeo = new THREE.BufferGeometry();
  slingGeo.setAttribute('position', new THREE.Float32BufferAttribute(new Float32Array(SLING_N*2*3), 3));
  const sIdx = []; for (let i = 0; i < SLING_N - 1; i++) { const a = i*2; sIdx.push(a, a+1, a+2, a+1, a+3, a+2); }
  const sUv = []; for (let i = 0; i < SLING_N; i++) sUv.push(0, i/SLING_N*4, .3, i/SLING_N*4);
  slingGeo.setIndex(sIdx); slingGeo.setAttribute('uv', new THREE.Float32BufferAttribute(sUv, 2));
  const slingMat = new THREE.MeshStandardMaterial({ map: m.web.map, roughness: .96, side: THREE.DoubleSide, flatShading: true });
  const sling = new THREE.Mesh(slingGeo, slingMat); sling.name = 'Sling'; sling.castShadow = true; sling.frustumCulled = false;

  const { skin, geometry } = bake('Soldier');

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
  kit.profile('Magazine', [[0,0],[.062,0],[.067,-.07],[.088,-.168],[.034,-.188],[.008,-.096]], .023, materials.alu, b, [0,0,0], .002);
  kit.profile('Mag floorplate', [[.03,-.18],[.09,-.162],[.094,-.172],[.034,-.194]], .026, materials.dark, b, [0,0,0], .001);
  const geos = kit.pieces.map(({ mesh }) => { mesh.updateMatrix(); return mesh.geometry.clone().applyMatrix4(mesh.matrix); });
  const meshes = kit.pieces.map(({ mesh }, i) => new THREE.Mesh(geos[i], mesh.material));
  b.removeFromParent();
  for (const x of meshes) { x.castShadow = true; g.add(x); }
  return g;
}
