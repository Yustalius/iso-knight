import * as THREE from 'three';
import { makeRandom, makeMaterial } from './textures.js';
import { createKit } from './rigid-kit.js';
export { makeTexture, makeMaterial } from './textures.js';

// Knight geometry and textures. Base mesh taken from the "Последний караул" prototype
// (reference/gpt-isometric-knight/model.js), mirrored so the sword is in the right hand,
// with hinged tabard panels and a back panel added for cloth simulation.
// One unit is one metre, +Y up, the character faces +Z, its right hand is at -X.

export const SOLE = { bottom: -.0905, heel: -.0765, toe: .1885, halfWidth: .0715 };

export function createKnight() {
  const random = makeRandom(1729);
  const root = new THREE.Group(); root.name = 'Knight';
  const { bones, joint, box, ellipsoid, rings, panel, bake } = createKit(root);
  const m = {
    steel: makeMaterial('Worn iron', '#7f8984', 'steel', .48, 11),
    edge: makeMaterial('Polished edges', '#a7aea1', 'steel', .5, 12),
    dark: makeMaterial('Recesses', '#252b2a', 'plain', 0, 13),
    mail: makeMaterial('Riveted chainmail', '#555f5b', 'mail', .28, 14),
    cloth: makeMaterial('Wool', '#683b39', 'cloth', 0, 15),
    linen: makeMaterial('Old ivory heraldry', '#b7ad8e', 'cloth', 0, 16),
    leather: makeMaterial('Dark leather', '#493b2c', 'cloth', 0, 17),
    brass: makeMaterial('Aged brass', '#9d8352', 'steel', .35, 18),
    wood: makeMaterial('Oak shield backing', '#6b5539', 'cloth', 0, 19)
  };
  const hips = joint('Hips', root, 0, .92, 0), spine = joint('Spine', hips, 0, .18, 0);
  const chest = joint('Chest', spine, 0, .20, 0), neck = joint('Neck', chest, 0, .23, 0);
  const head = joint('Head', neck, 0, .07, 0);
  const arms = {}, legs = {};
  const SIDES = [['R', -1], ['L', 1]];
  for (const [side, sign] of SIDES) {
    const upper = joint('UpperArm_'+side, chest, sign*.255, .15, 0);
    const fore = joint('Forearm_'+side, upper, sign*.026, -.265, 0);
    const hand = joint('Hand_'+side, fore, 0, -.25, 0);
    arms[side] = { upper, fore, hand };
    const thigh = joint('Thigh_'+side, hips, sign*.112, -.03, 0);
    const shin = joint('Shin_'+side, thigh, 0, -.395, 0);
    const foot = joint('Foot_'+side, shin, 0, -.37, 0);
    legs[side] = { thigh, shin, foot };
  }
  const flaps = {
    L: joint('Flap_L', hips, .105, .015, .159),
    R: joint('Flap_R', hips, -.105, .015, .159),
    B: joint('Flap_B', hips, 0, .015, -.168)
  };

  const rivet = (bone, x, y, z, r = .008) => ellipsoid('Rivet', [r, r, r*.65], m.brass, bone, [x, y, z], 6);

  rings('Gambeson', [[0,.16,.105],[.21,.17,.105],[.40,.215,.115],[.46,.19,.10]], m.cloth, hips, [0,-.025,0]);
  rings('Mail skirt', [[0,.22,.15],[.22,.17,.12]], m.mail, hips, [0,-.20,0]);
  rings('Breastplate', [[0,.167,.115],[.16,.19,.14],[.28,.224,.15],[.34,.18,.11]], m.steel, chest, [0,-.245,0]);
  rings('Gorget', [[0,.105,.091],[.057,.095,.08],[.077,.074,.071]], m.edge, neck, [0,-.045,0]);
  for (const s of [-1, 1]) {
    box('Tabard shoulder strap', [.075,.31,.012], m.linen, chest, [s*.13,.02,.142], [0,0,s*.07]);
    for (let i = 0; i < 3; i++) rivet(chest, s*.175, -.055 + i*.08, .136);
  }
  panel('Chest tabard', [[-.115,.12],[.115,.12],[.12,-.16],[-.12,-.16]], .012, m.cloth, chest, [0,-.01,.151]);
  box('Chest cross upright', [.03,.19,.007], m.linen, chest, [0,-.027,.174]);
  box('Chest cross arm', [.11,.03,.008], m.linen, chest, [0,.014,.176]);
  rings('Waist belt', [[0,.182,.14],[.049,.18,.135]], m.leather, hips, [0,.02,0]);
  box('Buckle', [.061,.052,.022], m.brass, hips, [-.016,.042,.144]);
  box('Buckle inset', [.036,.025,.01], m.dark, hips, [-.016,.042,.16]);
  box('Belt tongue', [.032,.17,.012], m.leather, hips, [-.086,-.046,.16], [0,0,.12]);
  // Hinged tabard panels: geometry hangs from each flap bone's origin.
  for (const s of ['L', 'R']) {
    panel('Split tabard', [[-.095,0],[.095,0],[.11,-.27],[.02,-.29],[-.095,-.26]].map(([x,y]) => [s === 'R' ? -x : x, y]), .006, m.cloth, flaps[s], [0,0,0], .001);
    box('Hem stitching', [.183,.014,.009], m.linen, flaps[s], [0,-.254,.015], [0,0,(s === 'L' ? 1 : -1)*.035]);
  }
  panel('Back tabard', [[-.13,0],[.13,0],[.14,-.31],[0,-.33],[-.14,-.31]], .006, m.cloth, flaps.B, [0,0,-.006], .001);
  box('Back hem', [.25,.014,.009], m.linen, flaps.B, [0,-.3,-.012]);
  ellipsoid('Belt pouch', [.065,.087,.045], m.leather, hips, [.197,-.035,.016]);
  box('Pouch strap', [.025,.085,.012], m.brass, hips, [.197,-.025,.06]);
  rings('Mail coif', [[-.11,.103,.095],[-.03,.128,.112],[.08,.134,.12]], m.mail, head);
  rings('Helmet shell', [[-.025,.144,.137],[.09,.151,.145],[.19,.137,.132],[.247,.08,.09],[.263,.012,.03]], m.steel, head);
  rings('Helmet lower rim', [[-.027,.147,.14],[-.011,.148,.142]], m.edge, head);
  panel('Visor', [[-.132,.064],[-.12,-.071],[0,-.115],[.12,-.071],[.132,.064]], .025, m.steel, head, [0,.055,.127]);
  box('Eye slit', [.239,.018,.012], m.dark, head, [0,.11,.164]);
  box('Visor brow', [.265,.018,.027], m.edge, head, [0,.13,.154]);
  panel('Nose ridge', [[-.012,.103],[.012,.103],[.017,-.086],[0,-.11],[-.017,-.086]], .019, m.edge, head, [0,.027,.167], .002);
  for (const s of [-1, 1]) {
    for (let row = 0; row < 2; row++) for (let col = 0; col < 3; col++) box('Breathing hole', [.009,.012,.006], m.dark, head, [s*(.048 + col*.027), .042 - row*.023, .161]);
    ellipsoid('Visor hinge', [.018,.018,.011], m.brass, head, [s*.139,.078,.113], 6);
    for (let i = 0; i < 3; i++) rivet(head, s*(.04 + i*.035), -.017, .153, .005);
  }
  box('Helmet crest', [.023,.013,.255], m.edge, head, [0,.238,.002]);
  for (const [side, sign] of SIDES) {
    const { upper, fore, hand } = arms[side], { thigh, shin, foot } = legs[side];
    rings('Mail sleeve', [[0,.079,.085],[.24,.092,.089]], m.mail, upper, [0,-.24,0]);
    ellipsoid('Pauldron', [.128,.09,.13], m.steel, upper, [sign*.013,.005,0]);
    for (let i = 0; i < 2; i++) rings('Shoulder lames', [[0,.106-i*.005,.108-i*.003],[.043,.12-i*.008,.116-i*.004]], i ? m.steel : m.edge, upper, [sign*.018,-.08-i*.04,0]);
    ellipsoid('Elbow cop', [.084,.081,.083], m.steel, fore, [0,.001,.009]);
    panel('Elbow wing', [[-.03,.05],[.035,.025],[.06,-.025],[0,-.065]], .012, m.edge, fore, [sign*.065,0,0]);
    rings('Vambrace', [[-.205,.058,.063],[-.07,.077,.082],[0,.07,.071]], m.steel, fore);
    rings('Cuff', [[-.025,.071,.068],[.014,.073,.069]], m.edge, hand, [0,.032,0]);
    ellipsoid('Leather glove', [.058,.076,.06], m.leather, hand, [0,-.033,.014]);
    box('Gauntlet plate', [.095,.075,.035], m.steel, hand, [0,-.015,.052]);
    for (let k = 0; k < 3; k++) box('Finger plate', [.092,.012,.018], m.edge, hand, [0,-.032-k*.017,.067]);
    rings('Padded leg', [[-.36,.076,.08],[-.20,.092,.096],[0,.103,.106]], m.cloth, thigh);
    rings('Cuisses', [[-.29,.077,.082],[-.055,.10,.107]], m.steel, thigh, [0,0,.005]);
    ellipsoid('Knee cop', [.092,.078,.085], m.edge, shin, [0,.005,.035]);
    rings('Greave', [[-.32,.054,.057],[-.17,.068,.071],[-.035,.083,.078]], m.steel, shin, [0,0,.007]);
    for (const y of [-.09, -.27]) rings('Greave strap', [[0,.076,.078],[.022,.077,.079]], m.leather, shin, [0,y,0]);
    box('Boot sole', [.143,.039,.265], m.dark, foot, [0,-.071,.056]);
    ellipsoid('Sabatons', [.078,.068,.145], m.steel, foot, [0,-.035,.066]);
    for (let i = 0; i < 3; i++) box('Toe lames', [.132-i*.008,.018,.029], m.edge, foot, [0,-.011-i*.011,.095+i*.035]);
  }
  const shield = joint('Shield', arms.L.hand, 0, .13, .12); shield.rotation.set(-.08, .16, .08);
  const outline = [[-.22,.30],[.22,.30],[.22,-.015],[.145,-.205],[0,-.34],[-.145,-.205],[-.22,-.015]];
  panel('Shield rim', outline, .048, m.edge, shield, [0,0,0], .009);
  panel('Shield oak', outline.map(([x,y]) => [x*.94, y*.94]), .009, m.wood, shield, [0,0,-.017], .002);
  panel('Shield paint', outline.map(([x,y]) => [x*.9, y*.9]), .009, m.cloth, shield, [0,0,.055], .002);
  panel('Shield cross upright', [[-.033,.27],[.033,.27],[.033,-.27],[0,-.305],[-.033,-.27]], .002, m.linen, shield, [0,0,.067], 0);
  box('Shield cross arms', [.396,.060,.005], m.linen, shield, [0,.088,.069]);
  for (const s of [-1, 1]) for (let i = 0; i < 3; i++) rivet(shield, s*.199, .259 - i*.135, .06, .008);
  rivet(shield, 0, -.295, .062, .008);
  for (let i = 0; i < 10; i++) box('Shield scar', [.002 + random()*.004, .015 + random()*.028, .003], m.wood, shield, [(random()-.5)*.34, (random()-.5)*.42, .072], [0,0,.45]);
  for (const x of [-.095, .095]) box('Shield back strap', [.045,.23,.024], m.leather, shield, [x,.03,-.036]);
  box('Shield handle', [.16,.032,.034], m.leather, shield, [0,0,-.066]);
  const sword = joint('Sword', arms.R.hand, 0, -.025, .025); sword.rotation.x = -Math.PI/2;
  rings('Leather grip', [[-.067,.021,.021],[.062,.021,.021]], m.leather, sword);
  for (let i = 0; i < 5; i++) rings('Grip binding', [[0,.022,.022],[.005,.022,.022]], m.brass, sword, [0,-.045 + i*.022,0]);
  ellipsoid('Pommel', [.033,.039,.027], m.brass, sword, [0,.093,0], 6);
  box('Crossguard', [.239,.027,.039], m.brass, sword, [0,-.088,0]);
  for (const s of [-1, 1]) ellipsoid('Guard end', [.026,.023,.024], m.brass, sword, [s*.115,-.096,0], 6);
  panel('Sword blade', [[-.026,-.11],[.026,-.11],[.021,-.64],[0,-.77],[-.021,-.64]], .012, m.edge, sword, [0,0,-.006], .001);
  panel('Blade fuller', [[-.007,-.12],[.007,-.12],[.005,-.61],[0,-.685],[-.005,-.61]], .002, m.steel, sword, [0,0,.007], 0);

  // Rigid armour: each vertex belongs to one bone; one SkinnedMesh, one draw per material.
  const { skin, geometry } = bake('Knight_Armor');

  return {
    root, skin, bones, materials: m,
    j: { hips, spine, chest, neck, head, arms, legs, shield, sword, flaps },
    triangles: geometry.attributes.position.count / 3,
    dye(hex) { m.cloth.map.paint(hex); }
  };
}
