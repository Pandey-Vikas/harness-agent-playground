import { createServer } from 'node:http';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, extname, join, normalize } from 'node:path';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const rootDir = join(scriptDir, '..');
const publicDir = join(rootDir, 'public');
const port = Number(process.env.PORT) || 3000;

const mimeTypes = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg'
};

function sendJson(response, status, body) {
  response.writeHead(status, { 'content-type': 'application/json; charset=utf-8' });
  response.end(JSON.stringify(body));
}

function serveStatic(response, urlPath) {
  const filePath = normalize(join(publicDir, urlPath === '/' ? 'index.html' : urlPath));
  if (!filePath.startsWith(publicDir) || !existsSync(filePath)) {
    response.writeHead(404); response.end('Not found'); return;
  }
  const type = mimeTypes[extname(filePath).toLowerCase()] || 'application/octet-stream';
  response.writeHead(200, { 'content-type': type, 'cache-control': 'no-cache' });
  response.end(readFileSync(filePath));
}

const server = createServer((request, response) => {
  const url = new URL(request.url, `http://localhost:${port}`);
  if (url.pathname === '/api/config' && request.method === 'GET') {
    return sendJson(response, 200, {
      provider: process.env.MODEL_PROVIDER || 'mock',
      foundry: {
        name: process.env.AZURE_FOUNDRY_RESOURCE_NAME || null,
        resourceGroup: process.env.AZURE_FOUNDRY_RESOURCE_GROUP || null,
        endpoint: process.env.AZURE_OPENAI_ENDPOINT || null
      }
    });
  }
  if (url.pathname === '/api/setup/launch' && request.method === 'POST') {
    // TODO: spawn scripts/setup-server.js if not already running (see model-router-playground for pattern).
    return sendJson(response, 200, { url: 'http://localhost:3100/' });
  }
  serveStatic(response, url.pathname);
});

server.listen(port, () => {
  console.log(`Harness Agent Playground: http://localhost:${port}`);
});
