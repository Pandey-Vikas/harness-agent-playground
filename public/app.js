async function api(path, options = {}) {
  const res = await fetch(path, {
    method: options.method || 'GET',
    headers: options.body ? { 'content-type': 'application/json' } : {},
    body: options.body ? (typeof options.body === 'string' ? options.body : JSON.stringify(options.body)) : undefined
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `Request failed: ${res.status}`);
  return data;
}

async function refreshStatus() {
  try {
    const c = await api('/api/config');
    document.getElementById('providerLabel').textContent = c.provider || 'mock';
    document.getElementById('providerDot').classList.add(c.provider === 'foundry' ? 'ok' : 'warn');
    if (c.foundry?.name) {
      document.getElementById('foundryLabel').textContent = `${c.foundry.name} (${c.foundry.resourceGroup || 'unknown rg'})`;
      document.getElementById('foundryDot').classList.add('ok');
    } else {
      document.getElementById('foundryLabel').textContent = 'not configured — run the setup wizard';
      document.getElementById('foundryDot').classList.add('warn');
    }
  } catch (error) {
    document.getElementById('providerLabel').textContent = 'error: ' + error.message;
  }
}

document.getElementById('launchSetupButton')?.addEventListener('click', async () => {
  try {
    const r = await api('/api/setup/launch', { method: 'POST', body: {} });
    setTimeout(() => window.open(r.url || 'http://localhost:3100/', '_blank'), 400);
  } catch (error) {
    alert('Could not launch setup wizard: ' + error.message);
  }
});

refreshStatus();
