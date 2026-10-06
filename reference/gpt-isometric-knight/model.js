import * as THREE from 'three';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { createAnimationRig } from './animation.js';

// One unit is one metre. +Y is up and +Z is the character's forward axis.
export function createKnight() {
  let seed = 1729;
  const random = () => ((seed = (1664525 * seed + 1013904223) >>> 0) / 4294967296);
  const root = new THREE.Group(); root.name = 'Knight';
  const bones = [], joints = {}, pieces = [];
  const material = (name, color, type = 'plain', metalness = 0) => {
    const canvas = document.createElement('canvas'); canvas.width = canvas.height = 64;
    const ctx = canvas.getContext('2d'); ctx.fillStyle = color; ctx.fillRect(0, 0, 64, 64);
    for (let i = 0; i < 1600; i++) {
      ctx.fillStyle = random() > .48 ? `rgba(255,240,205,${random()*.13})` : `rgba(15,19,17,${random()*.22})`;
      ctx.fillRect(Math.floor(random()*64), Math.floor(random()*64), 1 + Math.floor(random()*2), 1);
    }
    if (type === 'mail') {
      for(let y=0;y<64;y+=5) for(let x=-4;x<64;x+=6) {
        const dx=x+(y%10===0?3:0);
        ctx.fillStyle='#222928'; ctx.fillRect(dx,y,5,4);
        ctx.fillStyle='#7d8581'; ctx.fillRect(dx,y,4,1);ctx.fillRect(dx,y+1,1,2);
        ctx.fillStyle='#454c48'; ctx.fillRect(dx+1,y+3,3,1);
      }
    } else if (type === 'cloth') {
      ctx.strokeStyle='rgba(17,16,12,.2)'; ctx.lineWidth=1;
      for(let x=-64;x<128;x+=8) {ctx.beginPath();ctx.moveTo(x,0);ctx.lineTo(x+64,64);ctx.stroke();}
    } else if (type === 'steel') {
      for(let i=0;i<22;i++) {ctx.fillStyle='rgba(220,222,198,.24)';ctx.fillRect(random()*64,random()*64,1,random()*8+1);}
      ctx.fillStyle='rgba(26,33,30,.18)';ctx.fillRect(0,56,64,8);
    }
    const texture=new THREE.CanvasTexture(canvas);
    texture.magFilter=THREE.NearestFilter; texture.minFilter=THREE.NearestMipmapNearestFilter;
    texture.colorSpace=THREE.SRGBColorSpace;
    const m=new THREE.MeshStandardMaterial({name,map:texture,roughness:metalness?.7:.96,metalness,flatShading:true});
    return m;
  };
  const m = {
    steel:material('Worn iron','#7f8984','steel',.48),
    edge:material('Polished edges','#a7aea1','steel',.5),
    dark:material('Recesses','#252b2a'),
    mail:material('Riveted chainmail','#555f5b','mail',.28),
    cloth:material('Ox blood wool','#683b39','cloth'),
    linen:material('Old ivory heraldry','#b7ad8e','cloth'),
    leather:material('Dark leather','#493b2c','cloth'),
    brass:material('Aged brass','#9d8352','steel',.35),
    wood:material('Oak shield backing','#6b5539','cloth')
  };
  function joint(name,parent,x,y,z) {
    const b=new THREE.Bone(); b.name=name;b.position.set(x,y,z);parent.add(b);
    bones.push(b);joints[name]=b;return b;
  }
  const hips=joint('Hips',root,0,.92,0), spine=joint('Spine',hips,0,.18,0);
  const chest=joint('Chest',spine,0,.20,0), neck=joint('Neck',chest,0,.23,0);
  const head=joint('Head',neck,0,.07,0);
  const arms={},legs={};
  for(const [side,sign] of [['R',1],['L',-1]]) {
    const upper=joint('UpperArm_'+side,chest,sign*.255,.15,0);
    const fore=joint('Forearm_'+side,upper,sign*.026,-.265,0);
    const hand=joint('Hand_'+side,fore,0,-.25,0);
    arms[side]={upper,fore,hand};
    const thigh=joint('Thigh_'+side,hips,sign*.112,-.03,0);
    const shin=joint('Shin_'+side,thigh,0,-.395,0);
    const foot=joint('Foot_'+side,shin,0,-.37,0);
    legs[side]={thigh,shin,foot};
  }
  function piece(name,geometry,mat,bone,pos=[0,0,0],rot=[0,0,0]) {
    const mesh=new THREE.Mesh(geometry,mat);mesh.name=name;mesh.position.set(...pos);mesh.rotation.set(...rot);
    bone.add(mesh);pieces.push({mesh,bone});return mesh;
  }
  const box=(name,size,mat,bone,pos,rot)=>piece(name,new THREE.BoxGeometry(...size),mat,bone,pos,rot);
  function ellipsoid(name,radii,mat,bone,pos,segments=8) {
    const g=new THREE.SphereGeometry(1,segments,5);g.scale(...radii);return piece(name,g,mat,bone,pos);
  }
  // Chamfered rings retain a human silhouette at the small on-screen game scale.
  function rings(name,levels,mat,bone,pos=[0,0,0],segments=8) {
    const p=[],uv=[],idx=[];
    levels.forEach(([y,rx,rz,zc=0],j)=>{
      for(let i=0;i<=segments;i++) {
        const a=(i/segments)*Math.PI*2+Math.PI/8;
        p.push(Math.cos(a)*rx,y,Math.sin(a)*rz+zc);uv.push(i/segments,j/(levels.length-1));
      }
    });
    for(let j=0;j<levels.length-1;j++)for(let i=0;i<segments;i++) {
      const a=j*(segments+1)+i,b=a+segments+1;idx.push(a,b,a+1,b,b+1,a+1);
    }
    for(let i=1;i<segments-1;i++){idx.push(0,i,i+1);const k=(levels.length-1)*(segments+1);idx.push(k,k+i+1,k+i);}
    const g=new THREE.BufferGeometry();g.setAttribute('position',new THREE.Float32BufferAttribute(p,3));g.setAttribute('uv',new THREE.Float32BufferAttribute(uv,2));g.setIndex(idx);g.computeVertexNormals();
    return piece(name,g.toNonIndexed(),mat,bone,pos);
  }
  function panel(name,points,depth,mat,bone,pos=[0,0,0],bevel=.007) {
    const shape=new THREE.Shape(points.map(p=>new THREE.Vector2(...p)));
    const g=new THREE.ExtrudeGeometry(shape,{depth,bevelEnabled:bevel>0,bevelThickness:bevel,bevelSize:bevel,bevelSegments:1,steps:1,curveSegments:1});
    // Explicit planar UVs keep the little surface marks at useful game scale.
    const p=g.attributes.position,u=g.attributes.uv;
    for(let i=0;i<p.count;i++)u.setXY(i,p.getX(i)*1.7+.5,p.getY(i)*1.7+.5);
    return piece(name,g,mat,bone,pos);
  }
  const rivet=(bone,x,y,z,r=.008)=>ellipsoid('Rivet',[r,r,r*.65],m.brass,bone,[x,y,z],6);
  rings('Gambeson',[[0,.16,.105],[.21,.17,.105],[.40,.215,.115],[.46,.19,.10]],m.cloth,hips,[0,-.025,0]);
  rings('Mail skirt',[[0,.22,.15],[.22,.17,.12]],m.mail,hips,[0,-.20,0]);
  rings('Breastplate',[[0,.167,.115],[.16,.19,.14],[.28,.224,.15],[.34,.18,.11]],m.steel,chest,[0,-.245,0]);
  rings('Gorget',[[0,.105,.091],[.057,.095,.08],[.077,.074,.071]],m.edge,neck,[0,-.045,0]);
  for(const s of [-1,1]) {
    box('Tabard shoulder strap',[.075,.31,.012],m.linen,chest,[s*.13,.02,.142],[0,0,s*.07]);
    for(let i=0;i<3;i++) rivet(chest,s*.175,-.055+i*.08,.136);
  }
  panel('Chest tabard',[[-.115,.12],[.115,.12],[.12,-.16],[-.12,-.16]],.012,m.cloth,chest,[0,-.01,.151]);
  box('Chest cross upright',[.03,.19,.007],m.linen,chest,[0,-.027,.174]);
  box('Chest cross arm',[.11,.03,.008],m.linen,chest,[0,.014,.176]);
  rings('Waist belt',[[0,.182,.14],[.049,.18,.135]],m.leather,hips,[0,.02,0]);
  box('Buckle',[.061,.052,.022],m.brass,hips,[.016,.042,.144]);
  box('Buckle inset',[.036,.025,.01],m.dark,hips,[.016,.042,.16]);
  box('Belt tongue',[.032,.17,.012],m.leather,hips,[.086,-.046,.16],[0,0,-.12]);
  for(let i=0;i<3;i++)rivet(hips,.082+i*.004,-.015-i*.04,.17,.004);
  for(const s of [-1,1]) {
    panel('Split tabard',[[-.095,.0],[.095,.0],[.11,-.27],[.02,-.29],[-.095,-.26]],.006,m.cloth,hips,[s*.105,.015,.159],.001);
    box('Hem stitching',[.183,.014,.009],m.linen,hips,[s*.105,-.239,.174],[0,0,s*.035]);
  }
  ellipsoid('Belt pouch',[.065,.087,.045],m.leather,hips,[-.197,-.035,.016]);
  box('Pouch strap',[.025,.085,.012],m.brass,hips,[-.197,-.025,.06]);
  // The coif is visible between the helmet and collar, including the back of the neck.
  rings('Mail coif',[[-.11,.103,.095],[-.03,.128,.112],[.08,.134,.12]],m.mail,head);
  rings('Helmet shell',[[-.025,.144,.137],[.09,.151,.145],[.19,.137,.132],[.247,.08,.09],[.263,.012,.03]],m.steel,head);
  rings('Helmet lower rim',[[-.027,.147,.14],[-.011,.148,.142]],m.edge,head);
  panel('Visor',[[-.132,.064],[-.12,-.071],[0,-.115],[.12,-.071],[.132,.064]],.025,m.steel,head,[0,.055,.127]);
  box('Eye slit',[.239,.018,.012],m.dark,head,[0,.11,.164]);
  box('Visor brow',[.265,.018,.027],m.edge,head,[0,.13,.154]);
  panel('Nose ridge',[[-.012,.103],[.012,.103],[.017,-.086],[0,-.11],[-.017,-.086]],.019,m.edge,head,[0,.027,.167],.002);
  for(const s of [-1,1]) {
    for(let row=0;row<2;row++)for(let col=0;col<3;col++)box('Breathing hole',[.009,.012,.006],m.dark,head,[s*(.048+col*.027),.042-row*.023,.161]);
    ellipsoid('Visor hinge',[.018,.018,.011],m.brass,head,[s*.139,.078,.113],6);
    for(let i=0;i<3;i++)rivet(head,s*(.04+i*.035),-.017,.153,.005);
  }
  box('Helmet crest',[.023,.013,.255],m.edge,head,[0,.238,.002]);
  for(const [side,sign] of [['R',1],['L',-1]]) {
    const {upper,fore,hand}=arms[side],{thigh,shin,foot}=legs[side];
    rings('Mail sleeve',[[0,.079,.085],[.24,.092,.089]],m.mail,upper,[0,-.24,0]);
    ellipsoid('Pauldron',[.128,.09,.13],m.steel,upper,[sign*.013,.005,0]);
    for(let i=0;i<2;i++)rings('Shoulder lames',[[0,.106-i*.005,.108-i*.003],[.043,.12-i*.008,.116-i*.004]],i?m.steel:m.edge,upper,[sign*.018,-.08-i*.04,0]);
    ellipsoid('Elbow cop',[.084,.081,.083],m.steel,fore,[0,.001,.009]);
    panel('Elbow wing',[[-.03,.05],[.035,.025],[.06,-.025],[0,-.065]],.012,m.edge,fore,[sign*.065,0,0]);
    rings('Vambrace',[[-.205,.058,.063],[-.07,.077,.082],[0,.07,.071]],m.steel,fore);
    rings('Cuff',[[-.025,.071,.068],[.014,.073,.069]],m.edge,hand,[0,.032,0]);
    ellipsoid('Leather glove',[.058,.076,.06],m.leather,hand,[0,-.033,.014]);
    box('Gauntlet plate',[.095,.075,.035],m.steel,hand,[0,-.015,.052]);
    for(let k=0;k<3;k++)box('Finger plate',[.092,.012,.018],m.edge,hand,[0,-.032-k*.017,.067]);
    rings('Padded leg',[[-.36,.076,.08],[-.20,.092,.096],[0,.103,.106]],m.cloth,thigh);
    rings('Cuisses',[[-.29,.077,.082],[-.055,.10,.107]],m.steel,thigh,[0,0,.005]);
    ellipsoid('Knee cop',[.092,.078,.085],m.edge,shin,[0,.005,.035]);
    rings('Greave',[[-.32,.054,.057],[-.17,.068,.071],[-.035,.083,.078]],m.steel,shin,[0,0,.007]);
    for(const y of [-.09,-.27])rings('Greave strap',[[0,.076,.078],[.022,.077,.079]],m.leather,shin,[0,y,0]);
    box('Boot sole',[.143,.039,.265],m.dark,foot,[0,-.071,.056]);
    ellipsoid('Sabatons',[.078,.068,.145],m.steel,foot,[0,-.035,.066]);
    for(let i=0;i<3;i++)box('Toe lames',[.132-i*.008,.018,.029],m.edge,foot,[0,-.011-i*.011,.095+i*.035]);
  }
  // The heater shield has a real wooden back and grip; it reads from all eight views.
  const shield=joint('Shield',arms.L.hand,0,.13,.12);shield.rotation.set(-.08,-.16,-.08);
  const outline=[[-.22,.30],[.22,.30],[.22,-.015],[.145,-.205],[0,-.34],[-.145,-.205],[-.22,-.015]];
  panel('Shield rim',outline,.048,m.edge,shield,[0,0,0],.009);
  panel('Shield oak',outline.map(([x,y])=>[x*.94,y*.94]),.009,m.wood,shield,[0,0,-.017],.002);
  panel('Shield paint',outline.map(([x,y])=>[x*.9,y*.9]),.009,m.cloth,shield,[0,0,.055],.002);
  panel('Shield cross upright',[[-.033,.27],[.033,.27],[.033,-.27],[0,-.305],[-.033,-.27]],.002,m.linen,shield,[0,0,.067],0);
  box('Shield cross arms',[.396,.060,.005],m.linen,shield,[0,.088,.069]);
  for(const s of [-1,1])for(let i=0;i<3;i++)rivet(shield,s*.199,.259-i*.135,.06,.008);
  rivet(shield,0,-.295,.062,.008);
  for(let i=0;i<10;i++)box('Shield scar',[.002+random()*.004,.015+random()*.028,.003],m.wood,shield,[(random()-.5)*.34,(random()-.5)*.42,.072],[0,0,-.45]);
  for(const x of [-.095,.095])box('Shield back strap',[.045,.23,.024],m.leather,shield,[x,.03,-.036]);
  box('Shield handle',[.16,.032,.034],m.leather,shield,[0,0,-.066]);
  const sword=joint('Sword',arms.R.hand,0,-.025,.025);sword.rotation.x=-Math.PI/2;
  rings('Leather grip',[[-.067,.021,.021],[.062,.021,.021]],m.leather,sword);
  for(let i=0;i<5;i++)rings('Grip binding',[[0,.022,.022],[.005,.022,.022]],m.brass,sword,[0,-.045+i*.022,0]);
  ellipsoid('Pommel',[.033,.039,.027],m.brass,sword,[0,.093,0],6);
  box('Crossguard',[.239,.027,.039],m.brass,sword,[0,-.088,0]);
  for(const s of [-1,1])ellipsoid('Guard end',[.026,.023,.024],m.brass,sword,[s*.115,-.096,0],6);
  panel('Sword blade',[[-.026,-.11],[.026,-.11],[.021,-.64],[0,-.77],[-.021,-.64]],.012,m.edge,sword,[0,0,-.006],.001);
  panel('Blade fuller',[[-.007,-.12],[.007,-.12],[.005,-.61],[0,-.685],[-.005,-.61]],.002,m.steel,sword,[0,0,.007],0);
  // Weighted as rigid armour segments. A single skinned mesh batches each material.
  root.updateMatrixWorld(true);
  const buckets=new Map();
  for(const {mesh,bone} of pieces) {
    let g=mesh.geometry.index?mesh.geometry.toNonIndexed():mesh.geometry.clone();
    g.applyMatrix4(mesh.matrixWorld);
    g.deleteAttribute('normal');g.computeVertexNormals();
    const count=g.attributes.position.count, ids=new Uint16Array(count*4),weights=new Float32Array(count*4);
    for(let i=0;i<count;i++){ids[i*4]=bones.indexOf(bone);weights[i*4]=1;}
    g.setAttribute('skinIndex',new THREE.Uint16BufferAttribute(ids,4));g.setAttribute('skinWeight',new THREE.Float32BufferAttribute(weights,4));
    if(!buckets.has(mesh.material))buckets.set(mesh.material,[]);
    buckets.get(mesh.material).push(g);bone.remove(mesh);
  }
  const materials=[...buckets.keys()],merged=materials.map(mat=>mergeGeometries(buckets.get(mat)));
  const geometry=mergeGeometries(merged,true);
  const skin=new THREE.SkinnedMesh(geometry,materials);skin.name='Knight_Armor';skin.castShadow=true;skin.receiveShadow=true;
  root.add(skin);root.updateMatrixWorld(true);skin.bind(new THREE.Skeleton(bones));
  const animation=createAnimationRig({root,bones,hips,spine,chest,neck,head,arms,legs,shield,sword});
  root.animations=animation.clips;
  return {root,skin,...animation,materials,triangles:geometry.attributes.position.count/3,bones:bones.length};
}
