// Renders check frames headlessly: node tools/shots.cjs [outDir]
// PAGE=soldier.html picks the page; the game exposes window.__game (the knight also window.__knight).
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs'), path = require('node:path');
const out = process.argv[2] || 'shots'; fs.mkdirSync(out, { recursive: true });
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.PW_CHANNEL !== undefined ? (process.env.PW_CHANNEL ? { channel: process.env.PW_CHANNEL } : {}) : process.platform === 'win32' ? { channel: 'chrome' } : {}), args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  const page = await browser.newPage({ viewport: { width: 1100, height: 760 }, deviceScaleFactor: 1 });
  const errors = []; page.on('pageerror', e => errors.push(e.message)); page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('requestfailed', r => { if (!/fonts\.(googleapis|gstatic)/.test(r.url())) errors.push('request failed: ' + r.url()); });
  // Deterministic frames: the game only advances through step(), Math.random is seeded. LIVE=1 keeps the real loop.
  if (!process.env.LIVE) await page.addInitScript(() => {
    window.requestAnimationFrame = () => 0;
    let s = 20240611; Math.random = () => ((s = (1664525*s + 1013904223) >>> 0) / 4294967296);
  });
  await page.goto('http://127.0.0.1:' + (process.env.PORT || 8777) + '/' + (process.env.PAGE || ''));
  await page.waitForFunction(() => !!(window.__game || window.__knight), { timeout: 60000 }).catch(() => {});
  if (!(await page.evaluate(() => !!(window.__game || window.__knight)))) { console.log('NO GAME', errors); await browser.close(); process.exit(1); }
  const K = (fn, ...a) => page.evaluate(fn, ...a);
  await K(() => { const G = window.__game || window.__knight; G.pause(); G.place(0.3, 1.4, 45); G.input({ mx: 0, mz: 0 }); G.step(0.6); });
  const shot = async (name, clip) => page.screenshot({ path: path.join(out, name + '.png'), clip });
  const plan = JSON.parse(process.env.PLAN || '[]');
  for (const s of plan) {
    await K(s => {
      const G = window.__game || window.__knight;
      if (s.reset) { G.place(s.reset[0], s.reset[1], s.reset[2]); G.input({ mx: 0, mz: 0, run: false, block: false }); G.step(0.8); }
      if (s.view) G.view(s.view[0], s.view[1]);
      if (s.input) G.input(s.input);
      // any other key naming a hook is called with its value: {"aim":[x,y,z]}, {"reload":1}, {"trigger":true}
      for (const k in s) if (!/^(reset|view|input|step|name|clip)$/.test(k) && typeof G[k] === 'function') G[k](...(Array.isArray(s[k]) ? s[k] : [s[k]]));
      if (s.step) G.step(s.step);
      G.render();
    }, s);
    if (s.name) await shot(s.name, s.clip);
  }
  // EVAL='expr' prints a value computed in the page after the plan, for numeric checks
  const extra = process.env.EVAL ? { eval: await K(e => { try { return eval(e); } catch (x) { return 'EVAL ERROR: ' + x.message; } }, process.env.EVAL) } : {};
  console.log(JSON.stringify({ errors, state: await K(() => (window.__game || window.__knight).state), ...extra }));
  await browser.close();
})();
