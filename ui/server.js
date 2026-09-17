// Copyright (c) Microsoft. All rights reserved.
// Node.js server: static UI + thin proxy to the .NET HarnessAgent host on HARNESS_PORT.

import { createServer } from 'node:http';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, extname, join, normalize } from 'node:path';
import { spawn } from 'node:child_process';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const rootDir = join(scriptDir, '..');
const publicDir = join(scriptDir, 'public');
const wizardScript = join(scriptDir, 'setup', 'setup-server.js');
const port = Number(process.env.PORT) || 3000;
const harnessPort = Number(process.env.HARNESS_PORT) || 5099;
const wizardPort = Number(process.env.SETUP_PORT) || 3100;
const harnessBase = `http://127.0.0.1:${harnessPort}`;
const wizardBase = `http://127.0.0.1:${wizardPort}`;

let wizardChild = null;

const mimeTypes = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png'
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

async function readBody(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  return Buffer.concat(chunks);
}

// Forward a JSON request to the .NET host and mirror the response.
async function proxyJson(request, response, path, { method = 'GET' } = {}) {
  try {
    const body = method === 'GET' ? undefined : await readBody(request);
    const upstream = await fetch(`${harnessBase}${path}`, {
      method,
      headers: body?.length ? { 'content-type': 'application/json' } : undefined,
      body: body?.length ? body : undefined
    });
    const text = await upstream.text();
    response.writeHead(upstream.status, { 'content-type': upstream.headers.get('content-type') || 'application/json' });
    response.end(text);
  } catch (error) {
    // Surface the actual transport error, not a rewritten one.
    sendJson(response, 502, {
      error: error.message || String(error),
      type: error.constructor?.name,
      cause: error.cause?.code || error.cause?.message || undefined,
      hint: `Could not reach harness host at ${harnessBase}. Is the .NET process running?`
    });
  }
}

// Stream Server-Sent Events from the .NET host through to the browser.
async function proxySse(request, response, path) {
  let body;
  try { body = await readBody(request); } catch { body = Buffer.alloc(0); }
  let upstream;
  try {
    upstream = await fetch(`${harnessBase}${path}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
      body
    });
  } catch (error) {
    sendJson(response, 502, {
      error: error.message || String(error),
      type: error.constructor?.name,
      cause: error.cause?.code || error.cause?.message || undefined,
      hint: `Could not reach harness host at ${harnessBase}. Is the .NET process running?`
    });
    return;
  }
  if (!upstream.ok || !upstream.body) {
    const text = await upstream.text().catch(() => '');
    // Try to preserve upstream JSON error shape; fall back to raw text.
    let payload;
    try { payload = JSON.parse(text); } catch { payload = { error: text || `Upstream returned HTTP ${upstream.status}` }; }
    sendJson(response, upstream.status || 502, payload);
    return;
  }
  response.writeHead(200, {
    'content-type': 'text/event-stream',
    'cache-control': 'no-cache, no-transform',
    connection: 'keep-alive',
    'x-accel-buffering': 'no'
  });
  const reader = upstream.body.getReader();
  request.on('close', () => reader.cancel().catch(() => {}));
  try {
    for (; ;) {
      const { value, done } = await reader.read();
      if (done) break;
      response.write(value);
    }
  } catch (error) {
    // Client-observable event through SSE so the UI shows the real transport error.
    try {
      const payload = JSON.stringify({ message: error.message || String(error), type: error.constructor?.name });
      response.write(`event: error\ndata: ${payload}\n\n`);
    } catch { /* ignore */ }
  }
  response.end();
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url, `http://localhost:${port}`);

  if (url.pathname === '/api/config' && request.method === 'GET') {
    return sendJson(response, 200, {
      provider: process.env.MODEL_PROVIDER || 'mock',
      foundry: {
        name: process.env.AZURE_FOUNDRY_RESOURCE_NAME || null,
        resourceGroup: process.env.AZURE_FOUNDRY_RESOURCE_GROUP || null,
        endpoint: process.env.FOUNDRY_PROJECT_ENDPOINT || process.env.AZURE_OPENAI_ENDPOINT || null,
        model: process.env.FOUNDRY_MODEL || null
      },
      harness: { base: harnessBase }
    });
  }

  if (url.pathname === '/api/setup/launch' && request.method === 'POST') {
    const ok = await ensureWizardRunning();
    if (ok) return sendJson(response, 200, { url: `http://localhost:${wizardPort}/` });
    return sendJson(response, 502, { error: `Setup wizard failed to start on port ${wizardPort}. Check console output or run .\\start.ps1 -Setup manually.` });
  }

  if (url.pathname === '/api/agent/health' && request.method === 'GET') return proxyJson(request, response, '/health');
  if (url.pathname === '/api/agent/scenarios' && request.method === 'GET') return proxyJson(request, response, '/api/scenarios');
  if (url.pathname === '/api/agent/status' && request.method === 'GET') return proxyJson(request, response, '/api/session/status');
  if (url.pathname === '/api/agent/summary' && request.method === 'GET') return proxyJson(request, response, '/api/session/summary');
  if (url.pathname === '/api/agent/start' && request.method === 'POST') return proxyJson(request, response, '/api/session/start', { method: 'POST' });
  if (url.pathname === '/api/agent/reset' && request.method === 'POST') return proxyJson(request, response, '/api/session/reset', { method: 'POST' });
  if (url.pathname === '/api/agent/mode' && request.method === 'POST') return proxyJson(request, response, '/api/mode', { method: 'POST' });
  if (url.pathname === '/api/agent/run' && request.method === 'POST') return proxySse(request, response, '/api/run');

  serveStatic(response, url.pathname);
});

server.listen(port, () => {
  console.log(`Harness Agent Playground UI: http://localhost:${port}`);
  console.log(`Proxying harness runtime at ${harnessBase}`);
});

async function isWizardUp() {
  try {
    const res = await fetch(wizardBase, { signal: AbortSignal.timeout(600) });
    return res.ok;
  } catch { return false; }
}

async function ensureWizardRunning() {
  if (await isWizardUp()) return true;
  if (!wizardChild) {
    if (!existsSync(wizardScript)) {
      console.error(`Wizard script not found at ${wizardScript}`);
      return false;
    }
    wizardChild = spawn(process.execPath, ['--disable-warning=ExperimentalWarning', wizardScript], {
      cwd: rootDir,
      detached: true,
      windowsHide: true,
      stdio: 'ignore',
      env: { ...process.env, SETUP_PORT: String(wizardPort), AZURE_LOGIN_EXPERIENCE_V2: 'off' }
    });
    wizardChild.unref();
    wizardChild.on('exit', () => { wizardChild = null; });
    console.log(`Spawned setup wizard (pid ${wizardChild.pid}) on port ${wizardPort}`);
  }
  for (let i = 0; i < 40; i++) {
    if (await isWizardUp()) return true;
    await new Promise((r) => setTimeout(r, 300));
  }
  return false;
}
