import * as THREE from 'three';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { createKnight } from './model.js';
import { DURATIONS, WALK_SPEED } from './animation.js';

const canvas=document.querySelector('#stage');
const renderer=new THREE.WebGLRenderer({canvas,antialias:false,alpha:true,preserveDrawingBuffer:true});
renderer.setPixelRatio(1);renderer.shadowMap.enabled=true;renderer.shadowMap.type=THREE.PCFSoftShadowMap;
renderer.outputColorSpace=THREE.SRGBColorSpace;renderer.toneMapping=THREE.ACESFilmicToneMapping;renderer.toneMappingExposure=1.3;
const scene=new THREE.Scene();
const camera=new THREE.OrthographicCamera(-2,2,2,-2,.1,50);
const knight=createKnight();scene.add(knight.root);
document.querySelector('#tris').textContent=knight.triangles.toLocaleString('ru');
document.querySelector('#bones').textContent=knight.bones;
scene.add(new THREE.HemisphereLight(0xdfe7d5,0x4d4639,2.2));
const sun=new THREE.DirectionalLight(0xffe8bf,3.2);sun.position.set(-3,6,4);sun.castShadow=true;
sun.shadow.mapSize.set(2048,2048);sun.shadow.camera.left=-3;sun.shadow.camera.right=3;sun.shadow.camera.top=3;sun.shadow.camera.bottom=-3;sun.shadow.normalBias=.018;sun.shadow.bias=-.0002;scene.add(sun);
const rim=new THREE.DirectionalLight(0xacc3ca,1.9);rim.position.set(2,3,-4);scene.add(rim);
const ground=new THREE.Group();scene.add(ground);
let seed=77;const random=()=>((seed=(seed*1664525+1013904223)>>>0)/4294967296);
const stoneColors=[0x656858,0x5a6252,0x70715f,0x595b4e,0x6e6c5b];
const stones=stoneColors.map(color=>new THREE.MeshStandardMaterial({color,roughness:1,flatShading:true}));
const base=new THREE.Mesh(new THREE.CylinderGeometry(1.39,1.45,.15,8),new THREE.MeshStandardMaterial({color:0x353a30,roughness:1}));
base.position.y=-.16;base.receiveShadow=true;ground.add(base);
for(let row=-4;row<=4;row++)for(let col=-4;col<=4;col++) {
  const x=col*.285+(row%2)*.1425,z=row*.276;
  if(x*x+z*z>1.64)continue;
  const h=.055+random()*.024;
  const block=new THREE.Mesh(new THREE.BoxGeometry(.266,h,.255),stones[Math.floor(random()*stones.length)]);
  block.position.set(x,-.083+h/2,z);block.rotation.y=(random()-.5)*.045;block.receiveShadow=true;ground.add(block);
}
const grassMat=new THREE.MeshStandardMaterial({color:0x646d44,side:THREE.DoubleSide,roughness:1});
for(let i=0;i<45;i++){
  const a=random()*Math.PI*2,r=1.10+random()*.22;
  const x=Math.cos(a)*r,z=Math.sin(a)*r;
  const tuft=new THREE.Mesh(new THREE.ConeGeometry(.014,.075+random()*.07,3),grassMat);
  tuft.position.set(x,-.001,z);tuft.rotation.z=(random()-.5)*.7;ground.add(tuft);
}
const shadow=new THREE.Mesh(new THREE.PlaneGeometry(200,200),new THREE.ShadowMaterial({opacity:.18}));shadow.rotation.x=-Math.PI/2;shadow.position.y=-.242;shadow.receiveShadow=true;ground.add(shadow);

let mode='Idle',time=0,angle=Math.PI/4,scale=2,viewHeight=3.4,auto=false,paused=false,exporting=false,speed=1,transition=null;
const target=new THREE.Vector3(0,1.0,0),labels=['Ю','ЮВ','В','СВ','С','СЗ','З','ЮЗ'];
function updateCamera(){
  const w=canvas.clientWidth,h=canvas.clientHeight,aspect=w/h;
  camera.left=-viewHeight*aspect/2;camera.right=viewHeight*aspect/2;camera.top=viewHeight/2;camera.bottom=-viewHeight/2;
  camera.position.set(Math.sin(angle)*7,target.y+4.95,Math.cos(angle)*7);camera.lookAt(target);camera.updateProjectionMatrix();
  renderer.setSize(Math.max(1,Math.round(w/scale)),Math.max(1,Math.round(h/scale)),false);
  const direction=((Math.round(angle/(Math.PI/4))%8)+8)%8;
  document.querySelector('#angle-label').textContent=`${labels[direction]} / ${Math.round(THREE.MathUtils.radToDeg(angle)%360+360)%360}°`;
  document.querySelectorAll('#directions button').forEach((b,i)=>b.classList.toggle('active',i===direction));
}
for(let i=0;i<8;i++){
  const b=document.createElement('button');b.textContent=labels[i];b.title=`Направление ${i*45}°`;b.onclick=()=>{angle=i*Math.PI/4;updateCamera();};document.querySelector('#directions').append(b);
}
const chooseAction=name=>{
  transition={elapsed:0,pose:knight.skin.skeleton.bones.map(b=>({position:b.position.clone(),quaternion:b.quaternion.clone()}))};
  mode=name;time=0;document.querySelectorAll('#actions button').forEach(b=>b.classList.toggle('active',b.dataset.action===name));
};
document.querySelectorAll('#actions button').forEach(b=>b.onclick=()=>chooseAction(b.dataset.action));
document.querySelectorAll('#quality button').forEach(b=>b.onclick=()=>{scale=+b.dataset.scale;document.querySelectorAll('#quality button').forEach(x=>x.classList.toggle('active',x===b));updateCamera();});
document.querySelector('#rotate').onchange=e=>auto=e.target.checked;
document.querySelector('#pause').onchange=e=>paused=e.target.checked;
document.querySelectorAll('#speed button').forEach(b=>b.onclick=()=>{speed=+b.dataset.speed;document.querySelectorAll('#speed button').forEach(x=>x.classList.toggle('active',x===b));});
const timeline=document.querySelector('#timeline'),phaseLabel=document.querySelector('#phase');
timeline.oninput=e=>{time=+e.target.value/1000*DURATIONS[mode];paused=true;transition=null;document.querySelector('#pause').checked=true;};
document.querySelector('#wire').onchange=e=>knight.materials.forEach(m=>m.wireframe=e.target.checked);
document.querySelector('#ground').onchange=e=>ground.visible=e.target.checked;
document.querySelector('#zoom').oninput=e=>{viewHeight=+e.target.value;updateCamera();};
let drag=null;
canvas.onpointerdown=e=>{drag=e.clientX;canvas.setPointerCapture(e.pointerId);};
canvas.onpointermove=e=>{if(drag!==null){angle-=(e.clientX-drag)*.011;drag=e.clientX;updateCamera();}};
canvas.onpointerup=canvas.onpointercancel=()=>drag=null;
canvas.addEventListener('wheel',e=>{e.preventDefault();viewHeight=THREE.MathUtils.clamp(viewHeight+e.deltaY*.002,2.5,6.2);document.querySelector('#zoom').value=viewHeight;updateCamera();},{passive:false});
window.onkeydown=e=>{if(e.target.tagName==='INPUT')return;if(e.code==='Space'){e.preventDefault();paused=!paused;document.querySelector('#pause').checked=paused;}if(['1','2','3'].includes(e.key))chooseAction(['Idle','Walk','Attack'][+e.key-1]);};
new ResizeObserver(updateCamera).observe(canvas.parentElement);updateCamera();
let last=performance.now();
function frame(now){
  requestAnimationFrame(frame);const dt=THREE.MathUtils.clamp((now-last)/1000,0,.05);last=now;if(exporting)return;
  if(!paused)time+=dt*speed;if(auto&&!paused){angle+=dt*.27;updateCamera();}
  const duration=DURATIONS[mode],t=paused&&time===duration?duration:time%duration;
  knight.pose(mode,t);timeline.value=t/duration*1000;
  if(transition){
    transition.elapsed+=dt;const u=Math.min(1,transition.elapsed/.18),weight=u*u*(3-2*u);
    knight.skin.skeleton.bones.forEach((bone,i)=>{const previous=transition.pose[i];bone.position.lerpVectors(previous.position,bone.position,weight);bone.quaternion.slerpQuaternions(previous.quaternion,bone.quaternion,weight);});
    knight.root.updateMatrixWorld(true);if(u===1)transition=null;
  }
  const phase=mode==='Attack'?(t<.18?'Стойка':t<.61?'Замах':t<.76?'Разгон':t<.90?'Диагональный удар':t<1.15?'Доведение':'Возврат'):mode==='Walk'?'Перекат · перенос · опора':'Дыхание';
  phaseLabel.textContent=`${phase} · ${t.toFixed(2)} с`;
  renderer.render(scene,camera);
}
requestAnimationFrame(frame);
function download(blob,name){const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),5000);}
async function exportGLB(){
  exporting=true;
  try{
    knight.pose('Idle',0);
    const buffer=await new GLTFExporter().parseAsync(knight.root,{binary:true,animations:knight.clips,onlyVisible:true});
    return buffer;
  }finally{exporting=false;}
}
// Orthographic sprite frames all share the same framing and pivot, so no per-frame offsets are required.
async function spriteSheet(action='Idle',frames=1){
  exporting=true;
  const previous={angle,viewHeight,ground:ground.visible,scale};
  try{
    const size=192,out=document.createElement('canvas');out.width=size*frames;out.height=size*8;const ctx=out.getContext('2d');
    ground.visible=false;renderer.setSize(size,size,false);
    camera.left=-1.6;camera.right=1.6;camera.top=1.6;camera.bottom=-1.6;camera.updateProjectionMatrix();
    for(let d=0;d<8;d++)for(let f=0;f<frames;f++){
      const a=d*Math.PI/4;camera.position.set(Math.sin(a)*7,1.05+4.95,Math.cos(a)*7);camera.lookAt(0,1.05,0);
      knight.pose(action,f/frames*DURATIONS[action]);renderer.render(scene,camera);ctx.drawImage(canvas,f*size,d*size);
    }
    return out.toDataURL('image/png');
  }finally{ground.visible=previous.ground;updateCamera();exporting=false;}
}
document.querySelector('#export').onclick=async()=>{const s=document.querySelector('#status');s.textContent='Собираю модель…';try{download(new Blob([await exportGLB()],{type:'model/gltf-binary'}),'knight.glb');s.textContent='GLB готов: модель + 3 анимации';}catch(e){s.textContent='Не удалось экспортировать: '+e.message;}};
document.querySelector('#sheet').onclick=async()=>{const s=document.querySelector('#status');s.textContent='Рендерю восемь направлений…';try{const data=await spriteSheet();const a=document.createElement('a');a.href=data;a.download='knight-8-directions.png';a.click();s.textContent='PNG готов · 192 × 192 на направление';}catch(e){s.textContent=e.message;}};
window.knightStudio={
  exportGLB:async()=>Array.from(new Uint8Array(await exportGLB())),spriteSheet,
  setPose:(name,t=0)=>{chooseAction(name);transition=null;time=t;paused=true;document.querySelector('#pause').checked=true;knight.pose(name,t);renderer.render(scene,camera);},
  motionSample:(name,t)=>{knight.pose(name,t);return knight.diagnostics();},
  durations:DURATIONS,walkSpeed:WALK_SPEED,
  poseSheet:async(name,times,degrees=45)=>{
    exporting=true;const oldGround=ground.visible;
    try{
      ground.visible=false;const out=document.createElement('canvas');out.width=256*times.length;out.height=350;
      const ctx=out.getContext('2d');ctx.fillStyle='#283128';ctx.fillRect(0,0,out.width,out.height);
      renderer.setSize(256,320,false);camera.left=-1.3;camera.right=1.3;camera.top=1.625;camera.bottom=-1.625;camera.updateProjectionMatrix();
      const a=degrees*Math.PI/180;camera.position.set(Math.sin(a)*7,5.95,Math.cos(a)*7);camera.lookAt(0,1,0);
      for(let i=0;i<times.length;i++){knight.pose(name,times[i]);renderer.render(scene,camera);ctx.drawImage(canvas,i*256,0);ctx.fillStyle='#c5ccb5';ctx.font='13px monospace';ctx.textAlign='center';ctx.fillText(`${times[i].toFixed(2)} s`,i*256+128,331);}
      return out.toDataURL('image/png');
    }finally{ground.visible=oldGround;updateCamera();exporting=false;}
  },
  setView:(degrees,height=3.4)=>{angle=degrees*Math.PI/180;viewHeight=height;updateCamera();renderer.render(scene,camera);},
  setQuality:value=>{scale=value;updateCamera();renderer.render(scene,camera);},
  stats:{triangles:knight.triangles,bones:knight.bones,animations:knight.clips.map(x=>x.name)},
  validateGLB:async()=>{
    const bytes=await exportGLB();const loaded=await new GLTFLoader().parseAsync(bytes,'');let skins=0;
    loaded.scene.traverse(n=>{if(n.isSkinnedMesh)skins++;});
    const mixer=new THREE.AnimationMixer(loaded.scene);let maxBoneError=0;
    for(const clip of loaded.animations){
      const action=mixer.clipAction(clip);action.play();
      for(const fraction of [0,.173,.421,.567,.839,.99]){
        const t=clip.duration*fraction;mixer.setTime(t);loaded.scene.updateMatrixWorld(true);knight.pose(clip.name,t);
        for(const name of ['Hips','Head','Foot_R','Foot_L','Hand_R','Sword']){
          const original=knight.root.getObjectByName(name).getWorldPosition(new THREE.Vector3());
          const exported=loaded.scene.getObjectByName(name).getWorldPosition(new THREE.Vector3());
          maxBoneError=Math.max(maxBoneError,original.distanceTo(exported));
        }
      }
      mixer.stopAllAction();
    }
    if(maxBoneError>.005)throw new Error('Exported animation differs by '+maxBoneError+' m');
    return {bytes:bytes.byteLength,skins,animations:loaded.animations.map(x=>({name:x.name,duration:x.duration})),maxBoneError};
  }
};
