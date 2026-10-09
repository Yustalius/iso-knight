// Exports the concept's soldier models and painted textures for the Godot viewer: node tools/export-art.cjs [outDir]
// Opens game/art-src/export.html in headless Chromium (Playwright, like tools/shots.cjs), runs the three.js builders and
// GLTFExporter there and writes what they return into game/art/ (.glb files and tex/*.png). Re-run after changing art-src.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs'), path = require('node:path'), http = require('node:http');
const root = path.resolve(__dirname, '..');
const out = path.resolve(process.argv[2] || path.join(root, 'game', 'art'));
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript' };

const server = http.createServer((req, res) => {
  if (req.url === '/favicon.ico') { res.writeHead(204); res.end(); return; }
  const file = path.resolve(root, '.' + decodeURIComponent(new URL(req.url, 'http://x').pathname));
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
});
server.listen(0, '127.0.0.1', async () => {
  const port = server.address().port;
  const channel = process.env.PW_CHANNEL !== undefined ? process.env.PW_CHANNEL : process.platform === 'win32' ? 'chrome' : '';
  const browser = await chromium.launch({ headless: true, ...(channel ? { channel } : {}), args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  const page = await browser.newPage();
  const errors = []; page.on('pageerror', e => errors.push(e.message)); page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  let code = 0;
  try {
    await page.goto(`http://127.0.0.1:${port}/game/art-src/export.html`);
    await page.waitForFunction(() => window.__ready, { timeout: 60000 });
    const files = await page.evaluate(() => window.__export());
    for (const [name, data] of Object.entries(files)) {
      const p = path.join(out, name); fs.mkdirSync(path.dirname(p), { recursive: true });
      fs.writeFileSync(p, Buffer.from(data, 'base64'));
      console.log(`${name}  ${(fs.statSync(p).size/1024).toFixed(0)} KB`);
    }
  } catch (e) { errors.push(e.message); }
  if (errors.length) { console.log('ERRORS', errors); code = 1; }
  await browser.close(); server.close(); process.exit(code);
});
