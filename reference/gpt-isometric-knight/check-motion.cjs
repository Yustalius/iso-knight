const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'chrome',args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
 const page=await browser.newPage({viewport:{width:1440,height:1000}});
 page.on('pageerror',error=>{throw error;});
 await page.goto('http://127.0.0.1:8766');await page.waitForFunction(()=>!!window.knightStudio);
 const report=await page.evaluate(()=>{
  const api=window.knightStudio,results={};
  const distance=(a,b)=>Math.hypot(...a.map((v,i)=>v-b[i]));
  for(const [name,duration] of Object.entries(api.durations)){
   const result={lowestSole:Infinity,maxPlantedSoleError:0,maxFootTargetError:0,maxWristTargetError:0,highestSwingFoot:0,maxKneeBackward:0,maxWristAngularSpeed:0};
   let previousQuaternion=null;
   for(let i=0;i<=240;i++){
    const sample=api.motionSample(name,duration*i/240);
    if(previousQuaternion){const dot=Math.abs(previousQuaternion.reduce((sum,v,j)=>sum+v*sample.wristQuaternion[j],0));result.maxWristAngularSpeed=Math.max(result.maxWristAngularSpeed,2*Math.acos(Math.min(1,dot))/(duration/240));}
    previousQuaternion=sample.wristQuaternion;
    result.maxWristTargetError=Math.max(result.maxWristTargetError,distance(sample.wrist,sample.wristTarget));
    for(const foot of Object.values(sample.feet)){
     result.lowestSole=Math.min(result.lowestSole,foot.minY);
     if(foot.stance)result.maxPlantedSoleError=Math.max(result.maxPlantedSoleError,Math.abs(foot.minY));
     result.maxFootTargetError=Math.max(result.maxFootTargetError,distance(foot.ankle,foot.target));
     result.highestSwingFoot=Math.max(result.highestSwingFoot,foot.minY);
     const middle=(foot.hip[2]+foot.ankle[2])/2;
     result.maxKneeBackward=Math.max(result.maxKneeBackward,middle-foot.knee[2]);
    }
   }
   if(name==='Walk'){
    const a=api.motionSample('Walk',duration*.16).feet.R.ankle;
    const b=api.motionSample('Walk',duration*.40).feet.R.ankle;
    result.plantedForwardDrift=Math.abs((b[2]-a[2])+api.walkSpeed*duration*.24);
   }
   const first=api.motionSample(name,0),last=api.motionSample(name,duration);
   result.loopTipError=distance(first.swordTip,last.swordTip);
   result.loopFeetError=Math.max(...['R','L'].map(side=>distance(first.feet[side].ankle,last.feet[side].ankle)));
   results[name]=result;
  }
  return results;
 });
 console.log(JSON.stringify(report,null,2));fs.writeFileSync('motion-check.json',JSON.stringify(report,null,2));
 for(const [name,times,angle,file] of [
  ['Walk',[0,.145,.29,.435,.58,.725,.87,1.015],90,'walk-poses.png'],
  ['Attack',[0,.25,.50,.62,.76,.89,1.08,1.5],20,'attack-poses.png'],
  ['Attack',[0,.25,.50,.62,.76,.89,1.08,1.5],90,'qa-attack-side.png']
 ]){
  const data=await page.evaluate(([name,times,angle])=>window.knightStudio.poseSheet(name,times,angle),[name,times,angle]);
  fs.writeFileSync(file,Buffer.from(data.split(',')[1],'base64'));
 }
 await browser.close();
 for(const [name,r] of Object.entries(report)){
  if(r.lowestSole<-.002||r.maxFootTargetError>.004||r.maxWristTargetError>.025||r.loopTipError>.001||r.loopFeetError>.001||(r.plantedForwardDrift??0)>.001||r.maxWristAngularSpeed>35)throw new Error(name+' motion constraints failed');
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
