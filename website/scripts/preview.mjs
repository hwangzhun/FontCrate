import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { resolve, extname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../dist/', import.meta.url));
const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.png': 'image/png', '.webp': 'image/webp', '.xml': 'application/xml', '.txt': 'text/plain; charset=utf-8' };
const port = Number(process.env.PORT || 4173);
createServer(async (request, response) => {
  try {
    const path = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    let file = resolve(root, `.${path}`);
    if (file !== resolve(root) && !file.startsWith(resolve(root) + sep)) { response.writeHead(403).end(); return; }
    if ((await stat(file)).isDirectory()) {
      if (!path.endsWith('/')) { response.writeHead(301, { Location: `${path}/` }).end(); return; }
      file = resolve(file, 'index.html');
    }
    const body = await readFile(file);
    response.writeHead(200, { 'Content-Type': mime[extname(file)] || 'application/octet-stream' }).end(body);
  } catch {
    const body = await readFile(resolve(root, '404.html')).catch(() => 'Build the site first: npm run build');
    response.writeHead(404, { 'Content-Type': 'text/html; charset=utf-8' }).end(body);
  }
}).listen(port, '127.0.0.1', () => console.log(`FontCrate preview: http://127.0.0.1:${port}`));
