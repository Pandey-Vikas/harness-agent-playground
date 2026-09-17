// Copyright (c) Microsoft. All rights reserved.
// Front-end for the Harness Agent Playground: 3 scenario cards → per-scenario playground → execution summary.

const $ = (id) => document.getElementById(id);

const state = {
  scenarios: [],
  capabilities: [],
  currentScenario: null,
  streaming: false,
  currentAssistantEl: null,
  toolsById: new Map(),
  totalTokens: 0,
  status: null,
  abortController: null,
  autoRunOpenCountBefore: null,
  consecutiveNoProgress: 0,
  pendingAutoContinue: false
};

async function api(path, options = {}) {
  const res = await fetch(path, {
    method: options.method || 'GET',
    headers: options.body ? { 'content-type': 'application/json' } : undefined,
    body: options.body ? JSON.stringify(options.body) : undefined
  });
  const text = await res.text();
  const data = text ? safeJson(text) : {};
  if (!res.ok) throw new Error(data.error || `Request failed: ${res.status}`);
  return data;
}
function safeJson(text) { try { return JSON.parse(text); } catch { return { raw: text }; } }

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

let _lastThinker = '';
function showThinker(html) {
  const el = $('thinker');
  el.hidden = false;
  const t = el.querySelector('.thinker-text');
  if (html !== _lastThinker) { t.innerHTML = html; _lastThinker = html; }
}
function hideThinker() {
  $('thinker').hidden = true;
  _lastThinker = '';
}

function setPill(id, label, tone) {
  const el = $(id);
  el.querySelector('strong').textContent = label;
  el.dataset.tone = tone || '';
}

// ---------------- Landing view ----------------

function renderCards() {
  const container = $('scenarioCards');
  const capById = Object.fromEntries(state.capabilities.map((c) => [c.id, c]));
  const iconMap = {
    'financial-analyst': '📈',
    'incident-commander': '🚨',
    'launch-studio': '🚀'
  };
  container.innerHTML = state.scenarios.map((s) => `
    <button class="card" type="button" data-id="${escapeHtml(s.id)}" data-scenario="${escapeHtml(s.id)}">
      <div class="card-icon" aria-hidden="true">${iconMap[s.id] || '⚡'}</div>
      <div class="card-inner">
        <div class="card-domain">${escapeHtml(s.domain)}</div>
        <h3 class="card-title">${escapeHtml(s.title)}</h3>
        <p class="card-desc">${escapeHtml(s.shortDescription)}</p>
        <div class="card-caps">
          ${s.highlightedCapabilities.map((id) => `<span class="cap-chip">${escapeHtml(capById[id]?.name || id)}</span>`).join('')}
        </div>
        <div class="card-cta">Start scenario <span class="cta-arrow">→</span></div>
      </div>
    </button>
  `).join('');
  for (const btn of container.querySelectorAll('.card')) {
    btn.addEventListener('click', () => startScenario(btn.dataset.id));
  }
}

async function startScenario(scenarioId) {
  const scenario = state.scenarios.find((s) => s.id === scenarioId);
  if (!scenario) return;
  try {
    const status = await api('/api/agent/start', { method: 'POST', body: { scenarioId } });
    state.currentScenario = scenario;
    state.status = status;
    state.totalTokens = 0;
    showPlayground(scenario, status);
  } catch (error) {
    alert(`Could not start scenario: ${error.message}`);
  }
}

// ---------------- Playground view ----------------

function renderScenarioIntro(scenario) {
  $('chatLog').innerHTML = `
    <div class="empty">
      <p class="scenario-blurb">${escapeHtml(scenario.longDescription)}</p>
      <div class="starter-wrap">
        <span class="starter-label">Try this starter prompt</span>
        <button type="button" class="prompt-button" id="useStarter" title="Tap to use this prompt">
          <span class="prompt-icon" aria-hidden="true">✨</span>
          <span class="prompt-text">${escapeHtml(scenario.starterPrompt)}</span>
          <span class="prompt-arrow" aria-hidden="true">→</span>
        </button>
      </div>
    </div>`;
  document.getElementById('useStarter')?.addEventListener('click', () => {
    $('promptInput').value = scenario.starterPrompt;
    $('promptInput').focus();
  });
  $('toolLog').innerHTML = '<li class="side-empty">Waiting for a run…</li>';
  $('tokenLabel').textContent = '—';
  state.totalTokens = 0;
  state.toolsById.clear();
  state.currentAssistantEl = null;
  hideThinker();
}

function showPlayground(scenario, status) {
  $('landingView').hidden = true;
  $('playgroundView').hidden = false;
  $('scenarioPill').hidden = false;
  $('modePill').hidden = false;
  setPill('scenarioPill', scenario.title, 'ok');
  setPill('modePill', status.mode, status.mode === 'plan' ? 'plan' : 'execute');

  $('scenarioDomain').textContent = scenario.domain;
  $('scenarioTitle').textContent = scenario.title;
  $('scenarioDesc').textContent = scenario.longDescription;

  renderScenarioIntro(scenario);
  $('endpointLabel').textContent = status.endpoint || '—';

  renderTodos(status.todos || []);
  renderCapabilities(status);
}

function backToLanding() {
  if (state.streaming && !confirm('A run is still streaming. Leave and go back to scenarios?')) return;
  state.currentScenario = null;
  state.status = null;
  state.streaming = false;
  state.currentAssistantEl = null;
  state.toolsById.clear();
  state.totalTokens = 0;
  state.autoRunOpenCountBefore = null;
  state.consecutiveNoProgress = 0;
  state.pendingAutoContinue = false;
  $('sendButton').disabled = false;
  hideThinker();
  $('playgroundView').hidden = true;
  $('landingView').hidden = false;
  $('scenarioPill').hidden = true;
  $('modePill').hidden = true;
}

function renderTodos(todos, opts = {}) {
  const list = $('todosList');
  const isRunning = !!opts.isRunning;
  $('todosCount').textContent = String(todos.length);
  if (!todos.length) {
    list.innerHTML = '<li class="side-empty">No todos yet.</li>';
    return;
  }
  const firstOpen = todos.findIndex((t) => !t.isComplete);
  list.innerHTML = todos.map((t, i) => {
    const complete = !!t.isComplete;
    const active = isRunning && !complete && i === firstOpen;
    const cls = complete ? 'complete' : active ? 'active' : 'pending';
    const badge = complete ? '✓' : String(t.id);
    return `
      <li class="flow-node ${cls}" data-id="${t.id}">
        <span class="flow-dot" aria-hidden="true">${escapeHtml(badge)}</span>
        <div class="flow-body">
          <div class="flow-head">
            <span class="flow-title">${escapeHtml(t.title)}</span>
            <span class="flow-id">#${t.id}</span>
          </div>
          ${t.description ? `<div class="flow-desc">${escapeHtml(t.description)}</div>` : ''}
        </div>
      </li>`;
  }).join('');
}

function renderCapabilities(status) {
  const list = $('capsList');
  const highlighted = new Set(status.highlightedCapabilities || []);
  const exercised = new Set(status.exercisedCapabilities || []);
  list.innerHTML = state.capabilities.map((c) => {
    const isHi = highlighted.has(c.id);
    const isEx = exercised.has(c.id);
    if (!isHi && !isEx) return '';
    const cls = [isHi ? 'cap-hi' : '', isEx ? 'cap-ex' : ''].filter(Boolean).join(' ');
    return `<li class="cap ${cls}" title="${escapeHtml(c.description)}"><span class="cap-name">${escapeHtml(c.name)}</span></li>`;
  }).filter(Boolean).join('');
}

function appendChat(role, text = '') {
  const log = $('chatLog');
  log.querySelector('.empty')?.remove();
  const el = document.createElement('div');
  el.className = `msg msg-${role}`;
  el.innerHTML = `<div class="msg-role">${role}</div><div class="msg-body"></div>`;
  el.querySelector('.msg-body').textContent = text;
  log.appendChild(el);
  log.scrollTop = log.scrollHeight;
  return el;
}

function pushToolLog(entry) {
  const list = $('toolLog');
  list.querySelector('.side-empty')?.remove();
  const li = document.createElement('li');
  li.className = `tool tool-${entry.status || 'pending'}`;
  li.innerHTML = `
    <div class="tool-head">
      <span class="tool-name">${escapeHtml(entry.name)}</span>
      ${entry.capability ? `<span class="tool-cap">${escapeHtml(entry.capability)}</span>` : ''}
    </div>
    <pre class="tool-args">${escapeHtml(entry.arguments || '')}</pre>
    <pre class="tool-result" hidden></pre>`;
  list.appendChild(li);
  list.scrollTop = list.scrollHeight;
  return li;
}

// ---------------- SSE stream ----------------

async function streamRun(prompt) {
  const controller = new AbortController();
  state.abortController = controller;

  let res;
  try {
    res = await fetch('/api/agent/run', {
      method: 'POST',
      headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
      body: JSON.stringify({ prompt }),
      signal: controller.signal
    });
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    // Surface the raw browser fetch error verbatim.
    throw error;
  }

  if (!res.ok || !res.body) {
    const body = await res.json().catch(() => null);
    const message = body?.error || `HTTP ${res.status}`;
    const err = new Error(message);
    err.status = res.status;
    if (body?.type) err.exceptionType = body.type;
    if (body?.detail) err.detail = body.detail;
    throw err;
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  const flushEvent = (raw) => {
    const lines = raw.split('\n');
    let evt = 'message';
    let dataStr = '';
    for (const l of lines) {
      if (l.startsWith('event:')) evt = l.slice(6).trim();
      else if (l.startsWith('data:')) dataStr += l.slice(5).trim();
    }
    if (!dataStr) return;
    let data; try { data = JSON.parse(dataStr); } catch { data = { raw: dataStr }; }
    handleEvent(evt, data);
  };

  try {
    for (; ;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let idx;
      while ((idx = buffer.indexOf('\n\n')) !== -1) {
        const raw = buffer.slice(0, idx);
        buffer = buffer.slice(idx + 2);
        if (raw.trim()) flushEvent(raw);
      }
    }
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    // Stream broke mid-way. Include the raw error so it's not hidden.
    const wrapped = new Error(`Stream interrupted: ${error.message || String(error)}`);
    wrapped.cause = error;
    throw wrapped;
  }
}

function handleEvent(evt, data) {
  switch (evt) {
    case 'start':
      state.currentAssistantEl = null;
      setPill('modePill', data.mode || 'plan', data.mode === 'execute' ? 'execute' : 'plan');
      showThinker('Thinking…');
      // Kick the flow chart into "running" state so the first open todo glows.
      if (state.status?.todos) renderTodos(state.status.todos, { isRunning: true });
      break;
    case 'message_start': {
      // Every LoopAgent iteration gets a new MessageId. If the current bubble already has content,
      // start a fresh one so partners can see each iteration as its own turn.
      const existing = state.currentAssistantEl?.querySelector('.msg-body')?.textContent || '';
      if (existing.length > 0) state.currentAssistantEl = null;
      break;
    }
    case 'text': {
      if (!state.currentAssistantEl) state.currentAssistantEl = appendChat('agent', '');
      const el = state.currentAssistantEl.querySelector('.msg-body');
      if (el) { el.textContent += data.text; $('chatLog').scrollTop = $('chatLog').scrollHeight; }
      showThinker('Writing response…');
      break;
    }
    case 'tool_call': {
      const args = typeof data.arguments === 'string' ? data.arguments : JSON.stringify(data.arguments, null, 2);
      const li = pushToolLog({ name: data.name, arguments: args, status: 'pending', capability: data.capability });
      state.toolsById.set(data.id, { li, name: data.name });
      showThinker(`Calling <code>${escapeHtml(data.name)}</code>…`);
      // Optimistically flip nodes to complete as the agent calls todos_complete — the flow chart animates live.
      if (data.name === 'todos_complete' && state.status?.todos) {
        try {
          const parsed = typeof data.arguments === 'string' ? JSON.parse(data.arguments) : data.arguments;
          const items = parsed?.items ?? parsed;
          if (Array.isArray(items)) {
            const ids = new Set(items.map((it) => Number(it?.id)).filter(Number.isFinite));
            let changed = false;
            for (const t of state.status.todos) {
              if (ids.has(t.id) && !t.isComplete) { t.isComplete = true; changed = true; }
            }
            if (changed) renderTodos(state.status.todos, { isRunning: true });
          }
        } catch { /* ignore parse errors */ }
      }
      break;
    }
    case 'tool_result': {
      const entry = state.toolsById.get(data.id);
      if (entry?.li) {
        entry.li.classList.remove('tool-pending'); entry.li.classList.add('tool-done');
        const pre = entry.li.querySelector('.tool-result');
        pre.textContent = data.result ?? '(no result)';
        pre.hidden = false;
      }
      showThinker(`Analyzing result from <code>${escapeHtml(entry?.name || 'tool')}</code>…`);
      break;
    }
    case 'usage':
      state.totalTokens += Number(data.totalTokens || 0);
      $('tokenLabel').textContent = `${state.totalTokens} total`;
      break;
    case 'done':
      state.status = data;
      setPill('modePill', data.mode || 'plan', data.mode === 'execute' ? 'execute' : 'plan');
      renderTodos(data.todos || []);
      renderCapabilities(data);
      $('endpointLabel').textContent = data.endpoint || $('endpointLabel').textContent;
      handlePostRun(data);
      break;
    case 'cancelled':
      appendChat('system', data.message || 'Run cancelled.');
      hideThinker();
      break;
    case 'error':
      appendServerErrorMessage(data);
      hideThinker();
      break;
  }
}

// ---------------- Summary modal ----------------

async function openSummary() {
  try {
    const s = await api('/api/agent/summary');
    if (!s || s.active === false) { alert('No active session yet.'); return; }
    renderSummary(s);
    $('summaryModal').hidden = false;
  } catch (error) {
    alert(`Could not load summary: ${error.message}`);
  }
}

function renderSummary(s) {
  $('summaryDomain').textContent = s.domain || '';
  $('summaryTitle').textContent = `${s.scenarioTitle} — execution summary`;

  const highlighted = s.capabilities.filter((c) => c.highlighted).length;
  const exercised = s.capabilities.filter((c) => c.exercised).length;
  const both = s.capabilities.filter((c) => c.highlighted && c.exercised).length;

  $('summaryStats').innerHTML = `
    <div class="stat"><span class="stat-label">Duration</span><span class="stat-value">${s.durationSeconds}s</span></div>
    <div class="stat"><span class="stat-label">Turns</span><span class="stat-value">${s.runCount}</span></div>
    <div class="stat"><span class="stat-label">Tool calls</span><span class="stat-value">${s.toolCallCount}</span></div>
    <div class="stat"><span class="stat-label">Distinct tools</span><span class="stat-value">${s.distinctToolsCalled.length}</span></div>
    <div class="stat"><span class="stat-label">Tokens in / out</span><span class="stat-value">${s.inputTokens} / ${s.outputTokens}</span></div>
    <div class="stat"><span class="stat-label">Capabilities highlighted</span><span class="stat-value">${highlighted}</span></div>
    <div class="stat"><span class="stat-label">Capabilities exercised</span><span class="stat-value">${exercised}</span></div>
    <div class="stat"><span class="stat-label">Highlighted &amp; exercised</span><span class="stat-value">${both}</span></div>
  `;

  $('summaryCaps').innerHTML = `
    <thead><tr><th>Capability</th><th class="ta-c">Highlighted</th><th class="ta-c">Exercised</th></tr></thead>
    <tbody>${s.capabilities.map((c) => `
      <tr class="${c.highlighted ? 'row-hi' : ''} ${c.exercised ? 'row-ex' : ''}">
        <td><div class="cap-name">${escapeHtml(c.name)}</div><div class="cap-desc">${escapeHtml(c.description)}</div></td>
        <td class="ta-c">${c.highlighted ? '✔' : ''}</td>
        <td class="ta-c">${c.exercised ? '✔' : ''}</td>
      </tr>`).join('')}
    </tbody>`;

  $('summaryTools').innerHTML = s.distinctToolsCalled.length
    ? s.distinctToolsCalled.map((t) => `<span class="cap-chip">${escapeHtml(t)}</span>`).join('')
    : '<span class="side-empty">No tools called yet.</span>';

  $('summaryTodos').innerHTML = s.todos.length
    ? s.todos.map((t) => `
        <li class="todo ${t.isComplete ? 'todo-done' : 'todo-open'}">
          <span class="todo-check">${t.isComplete ? '✔' : '○'}</span>
          <div class="todo-body">
            <div class="todo-title">${escapeHtml(t.title)}</div>
            ${t.description ? `<div class="todo-desc">${escapeHtml(t.description)}</div>` : ''}
          </div>
          <span class="todo-id">#${t.id}</span>
        </li>`).join('')
    : '<li class="side-empty">No todos.</li>';
}

// ---------------- Events ----------------

async function triggerRun(prompt, opts = {}) {
  if (state.streaming) return false;
  // Silent mode: the app-generated continue prompt shouldn't clutter the transcript.
  if (!opts.silent) appendChat('you', prompt);
  state.streaming = true;
  $('sendButton').disabled = true;
  $('sendButton').hidden = true;
  $('stopButton').hidden = false;
  showThinker(opts.silent ? 'Continuing…' : 'Sending prompt…');
  try { await streamRun(prompt); }
  catch (error) {
    if (error.name !== 'AbortError') appendErrorWithRetry(error, prompt);
  }
  finally {
    state.streaming = false;
    state.abortController = null;
    $('sendButton').disabled = false;
    $('sendButton').hidden = false;
    $('stopButton').hidden = true;
    state.currentAssistantEl = null;
    state.toolsById.clear();
    if (!state.pendingAutoContinue) hideThinker();
  }
  return true;
}

/**
 * Render a system error message with an inline Retry button so the user can re-send the last prompt
 * without retyping it. Used for network/transport failures.
 */
function appendErrorWithRetry(error, lastPrompt) {
  const payload = {
    message: error.message || String(error),
    type: error.exceptionType || error.type || error.constructor?.name,
    detail: error.detail,
    stack: error.stack
  };
  appendServerErrorMessage(payload, { retryPrompt: lastPrompt });
}

/**
 * Render an error payload from either an SSE `error` event or a fetch failure. Shows the actual
 * message, exception type, optional detail, and a collapsible stack — no hardcoded hints.
 */
function appendServerErrorMessage(data, opts = {}) {
  const log = $('chatLog');
  log.querySelector('.empty')?.remove();
  const el = document.createElement('div');
  el.className = 'msg msg-system';
  const stackHtml = data.stack
    ? `<details class="error-stack"><summary>Stack trace</summary><pre>${escapeHtml(data.stack)}</pre></details>`
    : '';
  const detailHtml = data.detail
    ? `<div class="error-detail">${escapeHtml(data.detail)}</div>`
    : '';
  const typeHtml = data.type
    ? `<div class="error-type">${escapeHtml(data.type)}</div>`
    : '';
  const retryHtml = opts.retryPrompt !== undefined
    ? `<button type="button" class="retry-btn">↻ Retry last message</button>`
    : '';
  el.innerHTML = `
    <div class="msg-role">system • error</div>
    <div class="msg-body">
      ${typeHtml}
      <div class="error-text">${escapeHtml(data.message || 'Unknown error')}</div>
      ${detailHtml}
      ${stackHtml}
      ${retryHtml}
    </div>`;
  log.appendChild(el);
  log.scrollTop = log.scrollHeight;
  el.querySelector('.retry-btn')?.addEventListener('click', async () => {
    el.remove();
    await triggerRun(opts.retryPrompt);
  });
}

$('composer').addEventListener('submit', async (e) => {
  e.preventDefault();
  const prompt = $('promptInput').value.trim();
  if (!prompt) return;
  $('promptInput').value = '';
  await triggerRun(prompt);
});

$('resetButton').addEventListener('click', async () => {
  if (!state.currentScenario) return;
  if (state.streaming && !confirm('A run is still streaming. Reset anyway?')) return;
  try {
    const status = await api('/api/agent/reset', { method: 'POST' });
    state.status = status;
    state.streaming = false;
    state.autoRunOpenCountBefore = null;
    state.consecutiveNoProgress = 0;
    state.pendingAutoContinue = false;
    $('sendButton').disabled = false;
    renderScenarioIntro(state.currentScenario);
    setPill('modePill', status.mode, status.mode === 'plan' ? 'plan' : 'execute');
    renderTodos(status.todos || []);
    renderCapabilities(status);
  } catch (error) {
    alert(`Reset failed: ${error.message}`);
  }
});

$('clearToolsButton').addEventListener('click', () => {
  $('toolLog').innerHTML = '<li class="side-empty">Waiting for a run…</li>';
});

$('backToLanding').addEventListener('click', backToLanding);
$('summaryButton').addEventListener('click', openSummary);
$('stopButton').addEventListener('click', () => {
  if (state.abortController) {
    state.abortController.abort();
    appendChat('system', 'Run stopped by user.');
  }
});
$('closeSummary').addEventListener('click', () => { $('summaryModal').hidden = true; });
$('summaryContinue').addEventListener('click', () => { $('summaryModal').hidden = true; });
$('summaryBackToLanding').addEventListener('click', () => { $('summaryModal').hidden = true; backToLanding(); });

for (const btn of document.querySelectorAll('.mode-btn')) {
  btn.addEventListener('click', async () => {
    if (state.streaming) { appendChat('system', 'A run is already in progress. Wait for it to finish before switching modes.'); return; }
    const targetMode = btn.dataset.mode;
    try {
      showThinker(`Switching to <code>${escapeHtml(targetMode)}</code> mode…`);
      const status = await api('/api/agent/mode', { method: 'POST', body: { mode: targetMode } });
      setPill('modePill', status.mode, status.mode === 'plan' ? 'plan' : 'execute');
      renderCapabilities(status);
    } catch (error) {
      hideThinker();
      appendChat('system', `Could not switch mode: ${error.message}`);
      return;
    }
    // Mode changes only take effect on the next run — auto-kick a run so partners see progress immediately.
    if (targetMode === 'execute') {
      appendChat('system', 'Approved. Switching harness to execute mode…');
      await triggerRun('Approved. Please continue with the checklist and mark each item done as you finish it.');
    } else {
      hideThinker();
      appendChat('system', 'Switched back to plan mode. Send a message when you want to iterate on the plan.');
    }
  });
}

/**
 * After every plan-mode response, insert Copilot-style Approve/Iterate buttons inside the last
 * agent bubble. Approving flips mode to execute + kicks a run. Iterating just focuses the input.
 */
function maybeShowApproval(status) {
  if (!status || status.mode !== 'plan') return;
  const todos = Array.isArray(status.todos) ? status.todos : [];
  if (todos.length === 0) return; // no plan yet, nothing to approve

  const bubbles = document.querySelectorAll('.msg-agent');
  const last = bubbles[bubbles.length - 1];
  if (!last || last.querySelector('.approval-panel')) return;

  const panel = document.createElement('div');
  panel.className = 'approval-panel';
  panel.innerHTML = `
    <div class="approval-hint">Ready to run this plan?</div>
    <div class="approval-buttons">
      <button type="button" class="approval-approve"><span aria-hidden="true">✔</span> Approve &amp; execute</button>
      <button type="button" class="approval-reject"><span aria-hidden="true">✏</span> Iterate on plan</button>
    </div>`;
  last.appendChild(panel);

  panel.querySelector('.approval-approve').addEventListener('click', async () => {
    disableApprovalPanel(panel);
    await approveAndExecute();
  });
  panel.querySelector('.approval-reject').addEventListener('click', () => {
    disableApprovalPanel(panel);
    $('promptInput').focus();
    $('promptInput').placeholder = 'Tell the agent what to change about the plan…';
  });
}

function disableApprovalPanel(panel) {
  panel.querySelectorAll('button').forEach((b) => { b.disabled = true; });
  panel.classList.add('approval-panel-done');
}

/**
 * After every run, decide what happens next:
 *  - Plan mode with a plan on the board → offer inline Approve / Iterate.
 *  - Execute mode with open todos + progress → silently auto-continue (Copilot-style).
 *  - Two consecutive stalls in execute mode → stop and post a small system nudge.
 *  - All done or nothing open → just hide the thinker.
 */
function handlePostRun(data) {
  const openTodos = (data.todos || []).filter((t) => !t.isComplete);
  const isExecute = data.mode === 'execute';

  if (isExecute && openTodos.length > 0) {
    const madeProgress = state.autoRunOpenCountBefore == null || openTodos.length < state.autoRunOpenCountBefore;
    if (madeProgress) {
      state.consecutiveNoProgress = 0;
    } else {
      state.consecutiveNoProgress++;
    }

    if (state.consecutiveNoProgress >= 2) {
      // Model is truly stuck — stop the loop, subtle nudge, no big panel.
      state.autoRunOpenCountBefore = null;
      state.consecutiveNoProgress = 0;
      state.pendingAutoContinue = false;
      hideThinker();
      appendChat('system', `${openTodos.length} todo${openTodos.length === 1 ? '' : 's'} still open — the agent stalled. Send a message to nudge it.`);
      return;
    }

    // Keep the thinker up while we chain into the next run so it reads as one continuous action.
    state.autoRunOpenCountBefore = openTodos.length;
    state.pendingAutoContinue = true;
    showThinker(`Continuing on ${openTodos.length} remaining todo${openTodos.length === 1 ? '' : 's'}…`);
    const openIds = openTodos.map((t) => t.id).join(', ');
    const prompt = `Continue. Call the next tool for open todo ids: ${openIds}. After each tool result, call todos_complete for that id.`;
    // Fire on a microtask so the current streamRun's finally block runs first.
    setTimeout(() => {
      state.pendingAutoContinue = false;
      triggerRun(prompt, { silent: true });
    }, 120);
    return;
  }

  // Nothing to auto-drive.
  state.autoRunOpenCountBefore = null;
  state.consecutiveNoProgress = 0;
  hideThinker();
  maybeShowApproval(data);
}

async function approveAndExecute() {
  if (state.streaming) return;
  state.autoRunOpenCountBefore = null;
  state.consecutiveNoProgress = 0;
  try {
    showThinker('Switching to execute mode…');
    const status = await api('/api/agent/mode', { method: 'POST', body: { mode: 'execute' } });
    setPill('modePill', status.mode, status.mode === 'execute' ? 'execute' : 'plan');
    renderCapabilities(status);
  } catch (error) {
    hideThinker();
    appendChat('system', `Could not switch mode: ${error.message}`);
    return;
  }
  await triggerRun('Approved. Please continue with the checklist and mark each item done as you finish it.');
}

$('launchSetupButton').addEventListener('click', async () => {
  const btn = $('launchSetupButton');
  const orig = btn.textContent;
  btn.disabled = true;
  btn.textContent = 'starting wizard…';
  try {
    const r = await api('/api/setup/launch', { method: 'POST', body: {} });
    window.open(r.url || 'http://localhost:3100/', '_blank');
  } catch (error) {
    alert('Could not launch setup wizard: ' + error.message);
  } finally {
    btn.disabled = false;
    btn.textContent = orig;
  }
});

$('themeToggle').addEventListener('click', () => {
  const cur = document.documentElement.getAttribute('data-theme') || 'dark';
  const next = cur === 'dark' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', next);
  try { localStorage.setItem('harnessagent.theme', next); } catch { /* ignore */ }
});

$('promptInput')?.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
    e.preventDefault();
    $('composer').requestSubmit();
  }
});

// ---------------- Boot ----------------

async function boot() {
  try {
    const cfg = await api('/api/config');
    setPill('modelPill', cfg.foundry?.model || 'not set', cfg.foundry?.model ? 'ok' : 'warn');
  } catch { setPill('modelPill', 'unavailable', 'error'); }

  try {
    const data = await api('/api/agent/scenarios');
    state.scenarios = data.scenarios || [];
    state.capabilities = data.capabilities || [];
    renderCards();
  } catch (error) {
    $('scenarioCards').innerHTML = `<div class="empty">Could not load scenarios: ${escapeHtml(error.message)}</div>`;
  }
}
boot();
