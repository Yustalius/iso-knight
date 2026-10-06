// Renders check frames headlessly: node tools/shots.cjs [outDir]
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs'), path = require('node:path');
const out = process.argv[2] || 'shots'; fs.mkdirSync(out, { recursive: true });
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.PW_CHANNEL !== undefined ? (process.env.PW_CHANNEL ? { channel: process.env.PW_CHANNEL } : {}) : process.platform === 'win32' ? { channel: 'chrome' } : {}), args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  const page = await browser.newPage({ viewport: { width: 1100, height: 760 }, deviceScaleFactor: 1 });
  const errors = []; page.on('pageerror', e => errors.push(e.message)); page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('requestfailed', r => errors.push('request failed: ' + r.url()));
  await page.goto('http://127.0.0.1:' + (process.env.PORT || 8777));
  await page.waitForFunction(() => !!window.__knight, { timeout: 60000 }).catch(() => {});
  if (!(await page.evaluate(() => !!window.__knight))) { console.log('NO GAME', errors); await browser.close(); process.exit(1); }
  const K = (fn, ...a) => page.evaluate(fn, ...a);
  await K(() => { __knight.pause(); __knight.place(0.3, 1.4, 45); __knight.input({ mx: 0, mz: 0 }); __knight.step(0.6); });
  const shot = async (name, clip) => page.screenshot({ path: path.join(out, name + '.png'), clip });
  const plan = JSON.parse(process.env.PLAN || '[]');
  for (const s of plan) {
    await K(s => {
      if (s.reset) { __knight.place(s.reset[0], s.reset[1], s.reset[2]); __knight.input({ mx: 0, mz: 0, run: false, block: false }); __knight.step(0.8); }
      if (s.view) __knight.view(s.view[0], s.view[1]);
      if (s.input) __knight.input(s.input);
      if (s.attack) __knight.attack();
      if (s.step) __knight.step(s.step);
      __knight.render();
    }, s);
    if (s.name) await shot(s.name, s.clip);
  }
  console.log(JSON.stringify({ errors, state: await K(() => __knight.state) }));
  await browser.close();
})();
