// Copyright (c) Microsoft. All rights reserved.
// Six-step setup wizard: signs into Azure, provisions Foundry, writes .env, launches the app.

import { createServer } from 'node:http';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const execFileAsync = promisify(execFile);
const scriptDir = dirname(fileURLToPath(import.meta.url));
const rootDir = join(scriptDir, '..', '..');
const envPath = join(rootDir, '.env');
const htmlPath = join(scriptDir, 'setup.html');
const azCommand = process.platform === 'win32' ? 'az.cmd' : 'az';
const useShell = process.platform === 'win32';

async function az(args, { timeoutMs = 120_000 } = {}) {
  const { stdout } = await execFileAsync(azCommand, [...args, '--only-show-errors', '--output', 'json'], {
    shell: useShell,
    windowsHide: true,
    maxBuffer: 32 * 1024 * 1024,
    timeout: timeoutMs
  });
  return stdout.trim() ? JSON.parse(stdout) : null;
}

async function ensureFoundryProject({ subscriptionId, resourceGroup, account, projectName, location }) {
  const tokenObj = await az(['account', 'get-access-token', '--resource', 'https://management.azure.com']);
  const token = tokenObj?.accessToken;
  if (!token) throw new Error('Could not acquire ARM access token for project creation');
  const base = `https://management.azure.com/subscriptions/${encodeURIComponent(subscriptionId)}/resourceGroups/${encodeURIComponent(resourceGroup)}/providers/Microsoft.CognitiveServices/accounts/${encodeURIComponent(account)}/projects`;
  const apiVersion = '2025-04-01-preview';
  const listRes = await fetch(`${base}?api-version=${apiVersion}`, { headers: { authorization: `Bearer ${token}` } });
  if (listRes.ok) {
    const listBody = await listRes.json();
    const existing = (listBody.value || [])[0];
    if (existing) return { name: existing.name, id: existing.id, created: false };
  }
  const target = projectName || 'default-project';
  const putRes = await fetch(`${base}/${encodeURIComponent(target)}?api-version=${apiVersion}`, {
    method: 'PUT',
    headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: JSON.stringify({ location, identity: { type: 'SystemAssigned' }, properties: {} })
  });
  const putBody = await putRes.json().catch(() => ({}));
  if (!putRes.ok) throw new Error(putBody.error?.message || `Project create failed (${putRes.status})`);
  return { name: putBody.name?.split('/').pop() || target, id: putBody.id, created: true };
}

let activeLoginChild = null;
let loginState = { state: 'idle' };

function killActiveLogin() {
  if (!activeLoginChild) return;
  try { activeLoginChild.kill(); } catch { /* ignore */ }
  activeLoginChild = null;
}

function startBrowserLogin(tenantId) {
  killActiveLogin();
  loginState = { state: 'pending' };
  const args = ['login', '--only-show-errors'];
  if (tenantId) args.push('--tenant', tenantId);
  return new Promise((resolve, reject) => {
    const child = spawn(azCommand, args, { shell: useShell, windowsHide: true });
    activeLoginChild = child;
    let buffer = '';
    let urlReturned = false;
    const finishUrl = (url) => { if (!urlReturned) { urlReturned = true; resolve({ url }); } };
    const onData = (chunk) => {
      buffer += chunk.toString();
      const match = buffer.match(/https:\/\/login\.microsoftonline\.com\/[^\s"'<>]+/);
      if (match) finishUrl(match[0]);
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);
    child.on('error', (err) => {
      activeLoginChild = null;
      loginState = { state: 'failed', error: err.message };
      if (!urlReturned) reject(err);
    });
    child.on('exit', (code) => {
      activeLoginChild = null;
      if (code === 0) {
        loginState = { state: 'success' };
        if (!urlReturned) finishUrl('');
      } else {
        const message = buffer.trim() || `az login exited with code ${code}`;
        loginState = { state: 'failed', error: message };
        if (!urlReturned) reject(new Error(message));
      }
    });
  });
}

function startDeviceCodeLogin(tenantId) {
  killActiveLogin();
  loginState = { state: 'pending' };
  const args = ['login', '--use-device-code', '--only-show-errors'];
  if (tenantId) args.push('--tenant', tenantId);
  return new Promise((resolve, reject) => {
    const child = spawn(azCommand, args, { shell: useShell, windowsHide: true });
    activeLoginChild = child;
    let buffer = '';
    let codeReturned = false;
    const finishCode = (value) => { if (!codeReturned) { codeReturned = true; resolve(value); } };
    const onData = (chunk) => {
      buffer += chunk.toString();
      const codeMatch = buffer.match(/enter the code\s+([A-Z0-9-]{6,})\s+to authenticate/i);
      if (codeMatch) {
        const urlMatch = buffer.match(/https?:\/\/\S+devicelogin/i);
        finishCode({ code: codeMatch[1], url: urlMatch ? urlMatch[0] : 'https://microsoft.com/devicelogin' });
      }
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);
    child.on('error', (err) => {
      activeLoginChild = null;
      loginState = { state: 'failed', error: err.message };
      if (!codeReturned) reject(err);
    });
    child.on('exit', (code) => {
      activeLoginChild = null;
      if (code === 0) {
        loginState = { state: 'success' };
      } else {
        const message = buffer.trim() || `az login exited with code ${code}`;
        loginState = { state: 'failed', error: message };
        if (!codeReturned) reject(new Error(message));
      }
    });
  });
}

async function readJson(request) {
  let body = '';
  for await (const chunk of request) {
    body += chunk;
    if (Buffer.byteLength(body) > 100_000) throw new Error('Request body is too large');
  }
  return body ? JSON.parse(body) : {};
}

function sendJson(response, status, value) {
  response.writeHead(status, { 'content-type': 'application/json; charset=utf-8' });
  response.end(JSON.stringify(value));
}

function buildProjectEndpoint(accountName, projectName) {
  if (!accountName || !projectName) return '';
  return `https://${accountName}.services.ai.azure.com/api/projects/${projectName}`;
}

function writeEnv(values) {
  const projectEndpoint = values.projectEndpoint
    || buildProjectEndpoint(values.foundryName, values.projectName || 'default-project');
  const lines = [
    `MODEL_PROVIDER=${values.provider || 'foundry'}`,
    `PORT=${values.port || 3000}`,
    `HARNESS_PORT=${values.harnessPort || 5099}`,
    'DATABASE_PATH=./data/harness-playground.db',
    '',
    '# --- Foundry (used by the .NET HarnessAgentHost) ---',
    `FOUNDRY_PROJECT_ENDPOINT=${projectEndpoint}`,
    `FOUNDRY_MODEL=${values.deployment || 'gpt-4o-mini'}`,
    '',
    '# --- Foundry resource metadata (for the UI status pill) ---',
  ];
  if (values.foundryName) lines.push(`AZURE_FOUNDRY_RESOURCE_NAME=${values.foundryName}`);
  if (values.foundryResourceGroup) lines.push(`AZURE_FOUNDRY_RESOURCE_GROUP=${values.foundryResourceGroup}`);
  if (values.endpoint) lines.push(`AZURE_OPENAI_ENDPOINT=${values.endpoint}`);
  writeFileSync(envPath, lines.join('\n') + '\n', 'utf8');
}

let launchedNode = null;
let launchedDotnet = null;
let launchedPort = 3000;

function launchApp(port) {
  launchedPort = port;
  mkdirSync(join(rootDir, 'data'), { recursive: true });
  if (!launchedDotnet) {
    launchedDotnet = spawn('dotnet', ['run', '--project', 'agent/HarnessAgentHost', '--no-launch-profile'], {
      cwd: rootDir,
      detached: true,
      windowsHide: true,
      stdio: 'ignore',
      shell: process.platform === 'win32'
    });
    launchedDotnet.unref();
  }
  if (!launchedNode) {
    launchedNode = spawn(process.execPath, [
      '--env-file-if-exists=.env',
      '--disable-warning=ExperimentalWarning',
      'ui/server.js'
    ], { cwd: rootDir, detached: true, windowsHide: true, stdio: 'ignore' });
    launchedNode.unref();
  }
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  try {
    if (url.pathname === '/' && request.method === 'GET') {
      response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
      return response.end(readFileSync(htmlPath, 'utf8'));
    }

    if (url.pathname === '/api/status' && request.method === 'GET') {
      const result = { signedIn: false, account: null };
      try {
        result.account = await az(['account', 'show']);
        result.signedIn = !!result.account;
      } catch { /* not signed in */ }
      return sendJson(response, 200, result);
    }

    if (url.pathname === '/api/login-start' && request.method === 'POST') {
      const body = await readJson(request);
      const info = await startBrowserLogin(body.tenantId);
      return sendJson(response, 200, info);
    }

    if (url.pathname === '/api/login-device-code' && request.method === 'POST') {
      const body = await readJson(request);
      const info = await startDeviceCodeLogin(body.tenantId);
      return sendJson(response, 200, info);
    }

    if (url.pathname === '/api/login-poll' && request.method === 'GET') {
      if (loginState.state === 'success') {
        try {
          const account = await az(['account', 'show']);
          return sendJson(response, 200, { state: 'success', account });
        } catch (error) {
          return sendJson(response, 200, { state: 'failed', error: String(error.message) });
        }
      }
      return sendJson(response, 200, loginState);
    }

    if (url.pathname === '/api/login-cancel' && request.method === 'POST') {
      killActiveLogin();
      loginState = { state: 'idle' };
      return sendJson(response, 200, { ok: true });
    }

    if (url.pathname === '/api/prerequisites' && request.method === 'GET') {
      const result = await checkPrerequisites();
      return sendJson(response, 200, result);
    }

    if (url.pathname === '/api/prerequisites/install' && request.method === 'POST') {
      const { tool } = await readJson(request);
      try {
        await installPrerequisite(tool);
        const status = await checkPrerequisites();
        return sendJson(response, 200, { ok: true, status });
      } catch (error) {
        return sendJson(response, 500, { error: String(error?.stderr || error?.message || error) });
      }
    }

    if (url.pathname === '/api/subscriptions' && request.method === 'GET') {
      const subs = await az(['account', 'list']);
      return sendJson(response, 200, subs || []);
    }

    if (url.pathname === '/api/subscription' && request.method === 'POST') {
      const { subscriptionId } = await readJson(request);
      await az(['account', 'set', '--subscription', subscriptionId]);
      return sendJson(response, 200, { ok: true });
    }

    if (url.pathname === '/api/foundry-accounts' && request.method === 'GET') {
      const accounts = await az(['cognitiveservices', 'account', 'list']);
      const filtered = (accounts || [])
        .filter(a => ['AIServices', 'OpenAI'].includes(a.kind))
        .map(a => {
          // AIServices routes AOAI-compatible calls through .cognitiveservices.azure.com; legacy OpenAI uses .openai.azure.com.
          const suffix = a.kind === 'OpenAI' ? 'openai.azure.com' : 'cognitiveservices.azure.com';
          const endpoint = `https://${a.name}.${suffix}`;
          return {
            name: a.name,
            kind: a.kind,
            endpoint,
            resourceGroup: a.id.split('/')[4],
            location: a.location,
            disableLocalAuth: !!a.properties?.disableLocalAuth
          };
        });
      return sendJson(response, 200, filtered);
    }

    if (url.pathname === '/api/resource-groups' && request.method === 'GET') {
      const groups = await az(['group', 'list']);
      const mapped = (groups || []).map(g => ({ name: g.name, location: g.location }));
      return sendJson(response, 200, mapped);
    }

    if (url.pathname === '/api/foundry-locations' && request.method === 'GET') {
      const preferred = ['eastus', 'eastus2', 'westus', 'westus2', 'westus3', 'southcentralus', 'northcentralus', 'westeurope', 'northeurope', 'swedencentral', 'switzerlandnorth', 'uksouth', 'francecentral', 'australiaeast', 'japaneast', 'canadaeast'];
      try {
        const locs = await az(['account', 'list-locations']);
        if (!Array.isArray(locs) || !locs.length) return sendJson(response, 200, preferred.map((n) => ({ name: n, displayName: n, preferred: true })));
        const preferredSet = new Set(preferred);
        const rich = locs
          .filter((l) => l && l.metadata && (l.metadata.regionType || '').toLowerCase() === 'physical')
          .map((l) => ({ name: l.name, displayName: l.displayName || l.name, preferred: preferredSet.has(l.name) }))
          .sort((a, b) => {
            if (a.preferred !== b.preferred) return a.preferred ? -1 : 1;
            return a.displayName.localeCompare(b.displayName);
          });
        return sendJson(response, 200, rich);
      } catch {
        return sendJson(response, 200, preferred.map((n) => ({ name: n, displayName: n, preferred: true })));
      }
    }

    if (url.pathname === '/api/create-resource-group' && request.method === 'POST') {
      const { name, location } = await readJson(request);
      if (!name || !location) {
        return sendJson(response, 400, { error: 'name and location are required' });
      }
      try {
        const existing = await az(['group', 'exists', '--name', name]);
        if (existing === true || String(existing).toLowerCase() === 'true') {
          return sendJson(response, 200, { name, location, existed: true });
        }
        const rg = await az(['group', 'create', '--name', name, '--location', location]);
        return sendJson(response, 200, { name: rg?.name || name, location: rg?.location || location, existed: false });
      } catch (error) {
        return sendJson(response, 500, { error: String(error.stderr || error.message) });
      }
    }

    if (url.pathname === '/api/create-foundry' && request.method === 'POST') {
      const { name, resourceGroup, location, createGroup, sku } = await readJson(request);
      if (!name || !resourceGroup || !location) {
        return sendJson(response, 400, { error: 'name, resourceGroup and location are required' });
      }
      try {
        if (createGroup) {
          await az(['group', 'create', '--name', resourceGroup, '--location', location]);
        }
        const created = await az([
          'cognitiveservices', 'account', 'create',
          '--name', name,
          '--resource-group', resourceGroup,
          '--kind', 'AIServices',
          '--sku', sku || 'S0',
          '--location', location,
          '--custom-domain', name,
          '--yes'
        ]);
        let project = null;
        try {
          const sub = await az(['account', 'show']);
          if (sub?.id) {
            project = await ensureFoundryProject({
              subscriptionId: sub.id,
              resourceGroup,
              account: name,
              projectName: 'default-project',
              location: created.location || location
            });
          }
        } catch (e) { /* project best-effort */ }
        const grantedRoles = [];
        const skippedRoles = [];
        try {
          const me = await az(['ad', 'signed-in-user', 'show']);
          const oid = me?.id;
          const sub = await az(['account', 'show']);
          const accountScope = `/subscriptions/${sub?.id}/resourceGroups/${resourceGroup}/providers/Microsoft.CognitiveServices/accounts/${name}`;
          const projectScope = project?.name ? `${accountScope}/projects/${project.name}` : null;
          if (oid && sub?.id) {
            const grants = [
              { role: 'Cognitive Services User', scope: accountScope },
              { role: 'Cognitive Services OpenAI User', scope: accountScope },
              ...(projectScope ? [
                { role: 'Azure AI User', scope: projectScope },
                { role: 'Azure AI Project Manager', scope: projectScope }
              ] : [])
            ];
            for (const { role, scope } of grants) {
              try {
                await az(['role', 'assignment', 'create', '--assignee-object-id', oid, '--assignee-principal-type', 'User', '--role', role, '--scope', scope]);
                grantedRoles.push(role);
              } catch (e) {
                const reason = String(e.stderr || e.message).split('\n')[0].slice(0, 200);
                if (/already exists|role assignment.*exists/i.test(reason)) grantedRoles.push(role);
                else skippedRoles.push({ role, reason });
              }
            }
          } else {
            skippedRoles.push({ role: 'ALL', reason: 'Could not resolve signed-in user or subscription id' });
          }
        } catch (e) {
          skippedRoles.push({ role: 'ALL', reason: String(e.stderr || e.message).slice(0, 200) });
        }
        const hasDataPlane = grantedRoles.includes('Cognitive Services User') || grantedRoles.includes('Cognitive Services OpenAI User');
        if (!hasDataPlane) {
          return sendJson(response, 500, {
            error: 'Account was created but no data-plane role could be granted. First skipped role: ' +
              (skippedRoles[0]?.role || 'unknown') + ' — ' + (skippedRoles[0]?.reason || 'no details') +
              '. Fix: grant "Cognitive Services User" and "Cognitive Services OpenAI User" manually, then click Refresh.',
            name: created.name,
            resourceGroup,
            skippedRoles
          });
        }
        return sendJson(response, 200, {
          name: created.name,
          kind: created.kind,
          resourceGroup,
          location: created.location || location,
          project: project?.name || null,
          grantedRoles,
          skippedRoles,
          endpoint: `https://${created.name || name}.cognitiveservices.azure.com`,
          projectEndpoint: buildProjectEndpoint(created.name || name, project?.name || 'default-project')
        });
      } catch (error) {
        return sendJson(response, 500, { error: String(error.stderr || error.message) });
      }
    }

    if (url.pathname === '/api/grant-access' && request.method === 'POST') {
      const { account, resourceGroup } = await readJson(request);
      if (!account || !resourceGroup) {
        return sendJson(response, 400, { error: 'account and resourceGroup are required' });
      }
      try {
        const me = await az(['ad', 'signed-in-user', 'show']);
        const oid = me?.id;
        const sub = await az(['account', 'show']);
        if (!oid || !sub?.id) throw new Error('Could not resolve signed-in user or subscription');
        const accountScope = `/subscriptions/${sub.id}/resourceGroups/${resourceGroup}/providers/Microsoft.CognitiveServices/accounts/${account}`;
        let project = null;
        try {
          const acct = await az(['cognitiveservices', 'account', 'show', '--name', account, '--resource-group', resourceGroup]);
          if (acct?.properties?.allowProjectManagement) {
            project = await ensureFoundryProject({
              subscriptionId: sub.id,
              resourceGroup,
              account,
              projectName: 'default-project',
              location: acct.location
            });
          }
        } catch { /* leave project unset */ }
        const projectScope = project?.name ? `${accountScope}/projects/${project.name}` : null;
        const grants = [
          { role: 'Cognitive Services User', scope: accountScope },
          { role: 'Cognitive Services OpenAI User', scope: accountScope },
          ...(projectScope ? [
            { role: 'Azure AI User', scope: projectScope },
            { role: 'Azure AI Project Manager', scope: projectScope }
          ] : [])
        ];
        const granted = [];
        const skipped = [];
        for (const { role, scope } of grants) {
          try {
            await az(['role', 'assignment', 'create', '--assignee-object-id', oid, '--assignee-principal-type', 'User', '--role', role, '--scope', scope]);
            granted.push(role);
          } catch (e) {
            skipped.push({ role, reason: String(e.stderr || e.message).split('\n')[0].slice(0, 120) });
          }
        }
        return sendJson(response, 200, {
          granted, skipped,
          project: project?.name || null,
          projectEndpoint: buildProjectEndpoint(account, project?.name || 'default-project'),
          upn: me?.userPrincipalName || me?.mail || null
        });
      } catch (error) {
        return sendJson(response, 500, { error: String(error.stderr || error.message) });
      }
    }

    if (url.pathname === '/api/deployments' && request.method === 'GET') {
      const name = url.searchParams.get('name');
      const rg = url.searchParams.get('resourceGroup');
      const deps = await az(['cognitiveservices', 'account', 'deployment', 'list', '--name', name, '--resource-group', rg]);
      const mapped = (deps || []).map(d => ({
        name: d.name,
        model: d.properties?.model?.name,
        version: d.properties?.model?.version,
        sku: d.sku?.name
      }));
      return sendJson(response, 200, mapped);
    }

    if (url.pathname === '/api/save-and-launch' && request.method === 'POST') {
      const values = await readJson(request);
      writeEnv(values);
      launchApp(values.port || 3000);
      return sendJson(response, 200, { url: `http://localhost:${values.port || 3000}/` });
    }

    if (url.pathname === '/api/app-ready' && request.method === 'GET') {
      try {
        const r = await fetch(`http://127.0.0.1:${launchedPort}/api/config`, { signal: AbortSignal.timeout(1000) });
        return sendJson(response, 200, { ready: r.ok });
      } catch {
        return sendJson(response, 200, { ready: false });
      }
    }

    if (url.pathname === '/api/shutdown' && request.method === 'POST') {
      sendJson(response, 200, { ok: true });
      setTimeout(() => process.exit(0), 200);
      return;
    }

    return sendJson(response, 404, { error: 'Not found' });
  } catch (error) {
    const message = (error.stderr && String(error.stderr)) || error.message || 'Unknown error';
    sendJson(response, 500, { error: message });
    console.error(error);
  }
});

const prerequisites = [
  {
    key: 'node',
    label: 'Node.js',
    required: '22 or later',
    detect: async () => {
      const { stdout } = await execFileAsync(process.execPath, ['--version']);
      return { installed: true, version: stdout.trim().replace(/^v/, '') };
    },
    winget: null
  },
  {
    key: 'dotnet',
    label: '.NET SDK',
    required: '10.0 or later (Agent Framework Harness targets net10.0)',
    detect: async () => {
      const { stdout } = await execFileAsync('dotnet', ['--list-sdks'], { shell: useShell, windowsHide: true, timeout: 15_000 });
      const versions = stdout.split(/\r?\n/).map((l) => l.trim().split(' ')[0]).filter(Boolean);
      const highest = versions.sort((a, b) => a.localeCompare(b, undefined, { numeric: true })).pop();
      return { installed: !!highest, version: highest };
    },
    winget: 'Microsoft.DotNet.SDK.10'
  },
  {
    key: 'azcli',
    label: 'Azure CLI',
    required: 'any recent version',
    detect: async () => {
      const cmd = process.platform === 'win32' ? 'az.cmd' : 'az';
      const { stdout } = await execFileAsync(cmd, ['version', '--output', 'json'], { shell: useShell, windowsHide: true, timeout: 15_000 });
      const j = JSON.parse(stdout);
      return { installed: true, version: j['azure-cli'] };
    },
    winget: 'Microsoft.AzureCLI'
  },
  {
    key: 'python',
    label: 'Python',
    required: '3.9 or later (only for Auto Evaluation toolkit)',
    detect: async () => {
      const cmd = process.platform === 'win32' ? 'python' : 'python3';
      const { stdout } = await execFileAsync(cmd, ['--version'], { shell: useShell, windowsHide: true, timeout: 10_000 });
      return { installed: true, version: stdout.trim().replace(/^Python /i, '') };
    },
    winget: 'Python.Python.3.12'
  },
  {
    key: 'git',
    label: 'Git',
    required: 'any recent version',
    detect: async () => {
      const { stdout } = await execFileAsync('git', ['--version'], { shell: useShell, windowsHide: true, timeout: 10_000 });
      return { installed: true, version: stdout.trim().replace(/^git version /i, '') };
    },
    winget: 'Git.Git'
  }
];

async function checkPrerequisites() {
  const results = {};
  for (const p of prerequisites) {
    try {
      const r = await p.detect();
      results[p.key] = {
        label: p.label,
        required: p.required,
        installed: !!r.installed,
        version: r.version || null,
        canInstall: process.platform === 'win32' && !!p.winget
      };
    } catch {
      results[p.key] = {
        label: p.label,
        required: p.required,
        installed: false,
        version: null,
        canInstall: process.platform === 'win32' && !!p.winget
      };
    }
  }
  return { tools: results, platform: process.platform };
}

async function installPrerequisite(key) {
  const spec = prerequisites.find((p) => p.key === key);
  if (!spec) throw new Error(`Unknown tool: ${key}`);
  if (!spec.winget) throw new Error(`${spec.label} cannot be installed automatically from the wizard.`);
  if (process.platform !== 'win32') throw new Error('Automatic install is only supported on Windows (via winget).');
  await execFileAsync('winget', [
    'install', '--id', spec.winget,
    '--exact', '--source', 'winget',
    '--accept-package-agreements', '--accept-source-agreements',
    '--silent'
  ], { shell: useShell, windowsHide: true, timeout: 15 * 60 * 1000, maxBuffer: 32 * 1024 * 1024 });
  return true;
}

const port = Number(process.env.SETUP_PORT) || 3100;
server.listen(port, '127.0.0.1', () => console.log(`Setup wizard listening on http://localhost:${port}/`));
