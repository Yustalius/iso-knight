// Local preview server. Wraps index.html in the same document skeleton the artifact host adds.
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, dirname, extname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const port = +(process.env.PORT || 8777);
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.json': 'application/json', '.png': 'image/png', '.css': 'text/css' };
const skeleton = '<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"></head><body>';
http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://localhost');
    if (url.pathname === '/favicon.ico') { res.writeHead(204); res.end(); return; }
    const file = resolve(root, '.' + decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname));
    if (!file.startsWith(root + sep)) { res.writeHead(403); res.end(); return; }
    let data = await readFile(file);
    if (file.endsWith('.html')) {
      data = skeleton + data + '</body></html>';
      // offline/cloud: serve the vendored three.js instead of the CDN the published artifact uses
      if (!process.env.USE_CDN) data = data.replaceAll('https://cdn.jsdelivr.net/npm/three@0.180.0/', '/vendor/three/');
    }
    res.writeHead(200, { 'Content-Type': types[extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache' }); res.end(data);
  } catch { res.writeHead(404); res.end('Not found'); }
}).listen(port, '127.0.0.1', () => console.log(`Iso knight: http://127.0.0.1:${port}\nSoldier range: http://127.0.0.1:${port}/soldier.html`));
