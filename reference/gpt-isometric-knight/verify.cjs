const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'chrome',args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
 const page=await browser.newPage({viewport:{width:1440,height:1000},deviceScaleFactor:1});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
 await page.goto('http://127.0.0.1:8766');
 await page.waitForFunction(()=>!!window.knightStudio,{timeout:30000});
 await page.evaluate(()=>window.knightStudio.setPose('Idle',0));
 await page.screenshot({path:'preview.png'});
 await page.evaluate(()=>window.knightStudio.setView(225));
 await page.locator('.viewport').screenshot({path:'rear-view.png'});
 await page.evaluate(()=>window.knightStudio.setView(45));
 const action=page.getByRole('button',{name:'Шаг',exact:true});await action.click();
 if(!(await action.getAttribute('class')).includes('active'))throw Error('Walk action did not activate');
 await page.getByLabel('Сетка полигонов',{exact:true}).check();
 await page.getByLabel('Сетка полигонов',{exact:true}).uncheck();
 await page.getByRole('button',{name:'С',exact:true}).click();
 if(!(await page.locator('#angle-label').innerText()).includes('180°'))throw Error('Rear direction did not activate');
 await page.getByRole('button',{name:'ЮВ',exact:true}).click();
 await page.getByRole('button',{name:'¼×',exact:true}).click();
 if(!(await page.getByRole('button',{name:'¼×',exact:true}).getAttribute('class')).includes('active'))throw Error('Slow motion did not activate');
 await page.getByLabel('Кадр анимации',{exact:true}).fill('500');
 if(!await page.getByLabel('Пауза',{exact:true}).isChecked())throw Error('Scrubbing did not pause');
 await page.getByRole('button',{name:'1×',exact:true}).click();
 console.log(JSON.stringify({stats:await page.evaluate(()=>window.knightStudio.stats),errors}));
 if(process.argv.includes('--export')){
  const bytes=await page.evaluate(()=>window.knightStudio.exportGLB());fs.writeFileSync('knight.glb',Buffer.from(bytes));
  const roundTrip=await page.evaluate(()=>window.knightStudio.validateGLB());
  console.log('GLB round trip:',roundTrip);fs.writeFileSync('export-check.json',JSON.stringify(roundTrip,null,2));
  for(const [action,frames] of [['Idle',1],['Walk',16],['Attack',24]]){
   const png=await page.evaluate(([action,frames])=>window.knightStudio.spriteSheet(action,frames),[action,frames]);
   fs.writeFileSync('knight-'+action.toLowerCase()+'.png',Buffer.from(png.split(',')[1],'base64'));
  }
  const sheet=await page.evaluate(async()=>{
   const img=new Image();img.src=await window.knightStudio.spriteSheet();await img.decode();
   const c=document.createElement('canvas');c.width=1000;c.height=610;const ctx=c.getContext('2d');
   ctx.fillStyle='#252d26';ctx.fillRect(0,0,c.width,c.height);ctx.fillStyle='#e0dfc9';ctx.font='24px Georgia';ctx.fillText('ПОСЛЕДНИЙ КАРАУЛ',30,43);
   ctx.fillStyle='#9fae95';ctx.font='12px monospace';ctx.fillText('8 НАПРАВЛЕНИЙ / ОДНА 3D-МОДЕЛЬ / 4 316 ТРЕУГОЛЬНИКОВ',30,69);
   ctx.imageSmoothingEnabled=false;
   ['Ю · 0°','ЮВ · 45°','В · 90°','СВ · 135°','С · 180°','СЗ · 225°','З · 270°','ЮЗ · 315°'].forEach((label,d)=>{
    const x=(d%4)*250,y=90+Math.floor(d/4)*250;ctx.drawImage(img,0,d*192,192,192,x,y,250,250);
    ctx.fillStyle='#a7b29a';ctx.font='12px monospace';ctx.textAlign='center';ctx.fillText(label,x+125,y+230);
   });
   return c.toDataURL('image/png');
  });
  fs.writeFileSync('turnaround.png',Buffer.from(sheet.split(',')[1],'base64'));
 }
 await page.setViewportSize({width:500,height:760});
 await page.evaluate(()=>window.knightStudio.setPose('Idle',0));
 await page.screenshot({path:'qa-narrow.png'});
 const bounds=await page.locator('#directions').boundingBox();if(bounds.y+bounds.height>760)throw Error('Direction controls are below the narrow viewport');
 console.log('Viewer controls and narrow viewport: passed');
 if(errors.length)process.exitCode=1;
 await browser.close();
})().catch(e=>{console.error(e);process.exitCode=1;});
