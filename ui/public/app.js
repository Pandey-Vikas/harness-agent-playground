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
  pendingAutoContinue: false,
  approvalCard: null,
  approvalItems: new Map(),
  rateLimitRetries: 0,
  rateLimitHit: false,
  seenArtifacts: null
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

/**
 * Detects when an agent bubble is just paraphrasing the LoopEvaluator's re-injection message
 * ("You still have incomplete todo items. Continue working…"). Those bubbles are useless noise
 * — the tools still fire and the todos still flip, but partners shouldn't see them.
 */
const LOOP_ECHO_MARKERS = [
  'you still have incomplete todo item',
  'continue working until every item is complete',
  'marking each item as complete when finished',
  "i'm sorry, but i cannot assist with"
];
function looksLikeLoopEcho(text) {
  if (!text || text.length < 30) return false;
  const head = text.slice(0, 200).toLowerCase();
  return LOOP_ECHO_MARKERS.some((m) => head.includes(m));
}
/**
 * Compact one-line summary of a tool-call args object for the live thinker.
 * For simple scalars we emit "key=value"; for arrays like {items:[{id,reason}]} we emit "ids=[1,2,3]".
 * Full JSON always goes into the Tool Activity panel unchanged.
 */
function summarizeArgs(args) {
  if (args == null) return '';
  let obj = args;
  if (typeof obj === 'string') { try { obj = JSON.parse(obj); } catch { return obj.length > 80 ? obj.slice(0, 77) + '…' : obj; } }
  if (typeof obj !== 'object') return String(obj);
  const parts = [];
  for (const [k, v] of Object.entries(obj)) {
    if (v == null) continue;
    if (Array.isArray(v)) {
      if (v.length === 0) { parts.push(`${k}=[]`); continue; }
      if (v.every((it) => it && typeof it === 'object' && ('id' in it))) {
        parts.push(`ids=[${v.map((it) => it.id).join(', ')}]`);
      } else if (v.every((it) => typeof it !== 'object')) {
        parts.push(`${k}=[${v.slice(0, 4).join(', ')}${v.length > 4 ? ', …' : ''}]`);
      } else {
        parts.push(`${k}=[${v.length} items]`);
      }
    } else if (typeof v === 'object') {
      parts.push(`${k}={…}`);
    } else {
      const s = String(v);
      parts.push(`${k}=${s.length > 40 ? s.slice(0, 37) + '…' : s}`);
    }
  }
  const joined = parts.join(', ');
  return joined.length > 140 ? joined.slice(0, 137) + '…' : joined;
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
  // Pipeline defaults are always on; the card highlights what makes each scenario different.
  const baseline = new Set(['function-invocation', 'history-persistence', 'compaction', 'looping']);
  container.innerHTML = state.scenarios.map((s) => `
    <button class="card" type="button" data-id="${escapeHtml(s.id)}" data-scenario="${escapeHtml(s.id)}">
      <div class="card-icon" aria-hidden="true">${iconMap[s.id] || '⚡'}</div>
      <div class="card-inner">
        <div class="card-domain">${escapeHtml(s.domain)}</div>
        <h3 class="card-title">${escapeHtml(s.title)}</h3>
        <p class="card-desc">${escapeHtml(s.shortDescription)}</p>
        <div class="card-caps">
          ${s.highlightedCapabilities.filter((id) => !baseline.has(id)).map((id) => `<span class="cap-chip">${escapeHtml(capById[id]?.name || id)}</span>`).join('')}
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
  $('filesList').innerHTML = '<li class="side-empty">No files saved yet.</li>';
  $('traceList').innerHTML = '<li class="side-empty">No spans yet.</li>';
  $('traceSummary').textContent = '';
  $('tokenLabel').textContent = '—';
  state.approvalCard = null;
  state.approvalItems = new Map();
  state.rateLimitRetries = 0;
  state.rateLimitHit = false;
  seedArtifacts();
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

// Minimal, XSS-safe markdown: input is escaped first, then a small whitelist of constructs is re-introduced.
function renderMarkdown(src) {
  const inline = (s) => escapeHtml(s)
    .replace(/`([^`\n]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*])\*([^*\n]+)\*(?!\*)/g, '$1<em>$2</em>');
  const isList = (l) => /^\s*([-*•]|\d+[.)])\s+/.test(l);
  const isRow = (l) => /^\s*\|.*\|\s*$/.test(l);
  const lines = String(src).replace(/\r\n/g, '\n').split('\n');
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (/^```/.test(line)) {
      const buf = [];
      i++;
      while (i < lines.length && !/^```/.test(lines[i])) buf.push(lines[i++]);
      i++;
      out.push(`<pre class="md-code"><code>${escapeHtml(buf.join('\n'))}</code></pre>`);
    } else if (isRow(line) && /^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1] || '')) {
      const cells = (r) => r.trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim());
      const head = cells(line);
      i += 2;
      const rows = [];
      while (i < lines.length && isRow(lines[i])) rows.push(cells(lines[i++]));
      out.push(`<div class="md-table-wrap"><table class="md-table"><thead><tr>${head.map((c) => `<th>${inline(c)}</th>`).join('')}</tr></thead><tbody>${rows.map((r) => `<tr>${r.map((c) => `<td>${inline(c)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`);
    } else if (/^#{1,6}\s/.test(line)) {
      const level = Math.min(line.match(/^#+/)[0].length + 1, 5);
      out.push(`<h${level} class="md-h">${inline(line.replace(/^#+\s+/, ''))}</h${level}>`);
      i++;
    } else if (isList(line)) {
      const ordered = /^\s*\d+[.)]/.test(line);
      const items = [];
      while (i < lines.length && isList(lines[i])) items.push(lines[i++].replace(/^\s*([-*•]|\d+[.)])\s+/, ''));
      const tag = ordered ? 'ol' : 'ul';
      out.push(`<${tag} class="md-list">${items.map((t) => `<li>${inline(t)}</li>`).join('')}</${tag}>`);
    } else if (/^\s*(---+|\*\*\*+)\s*$/.test(line)) {
      out.push('<hr class="md-hr">');
      i++;
    } else if (line.trim() === '') {
      i++;
    } else {
      const buf = [lines[i++]];
      while (i < lines.length && lines[i].trim() !== '' && !/^(```|#{1,6}\s)/.test(lines[i]) && !isList(lines[i]) && !isRow(lines[i])) buf.push(lines[i++]);
      out.push(`<p>${inline(buf.join(' '))}</p>`);
    }
  }
  return out.join('');
}

function setAgentText(bubble, text) {
  const body = bubble.querySelector('.msg-body');
  body.dataset.raw = text;
  body.innerHTML = renderMarkdown(text);
}

function appendChat(role, text = '') {
  const log = $('chatLog');
  log.querySelector('.empty')?.remove();
  const el = document.createElement('div');
  el.className = `msg msg-${role}`;
  el.innerHTML = `<div class="msg-role">${role}</div><div class="msg-body"></div>`;
  if (role === 'agent') {
    setAgentText(el, text);
    const copy = document.createElement('button');
    copy.className = 'msg-copy';
    copy.textContent = 'Copy';
    copy.addEventListener('click', async () => {
      await navigator.clipboard.writeText(el.querySelector('.msg-body').dataset.raw || '');
      copy.textContent = 'Copied';
      setTimeout(() => { copy.textContent = 'Copy'; }, 1500);
    });
    el.querySelector('.msg-role').appendChild(copy);
  } else {
    el.querySelector('.msg-body').textContent = text;
  }
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

async function streamRun(endpoint, body) {
  const controller = new AbortController();
  state.abortController = controller;

  let res;
  try {
    res = await fetch(endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
      body: JSON.stringify(body),
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
      const existing = state.currentAssistantEl?.querySelector('.msg-body')?.dataset.raw || '';
      if (existing.length > 0) state.currentAssistantEl = null;
      break;
    }
    case 'text': {
      if (!state.currentAssistantEl) state.currentAssistantEl = appendChat('agent', '');
      const el = state.currentAssistantEl.querySelector('.msg-body');
      if (el) {
        const raw = (el.dataset.raw || '') + data.text;
        setAgentText(state.currentAssistantEl, raw);
        // Suppress loop-echo bubbles from the chat log — tools/todos still update in the side panels.
        if (looksLikeLoopEcho(raw)) {
          state.currentAssistantEl.hidden = true;
          showThinker('Working through remaining todos…');
        } else {
          $('chatLog').scrollTop = $('chatLog').scrollHeight;
          showThinker('Writing response…');
        }
      }
      break;
    }
    case 'tool_call': {
      const args = typeof data.arguments === 'string' ? data.arguments : JSON.stringify(data.arguments, null, 2);
      const li = pushToolLog({ name: data.name, arguments: args, status: 'pending', capability: data.capability });
      state.toolsById.set(data.id, { li, name: data.name });
      // Compact one-line summary of the args for the thinker (full JSON goes in the tool panel).
      const argsSummary = summarizeArgs(data.arguments);
      showThinker(`Calling <code>${escapeHtml(data.name)}</code>${argsSummary ? ` with <code>${escapeHtml(argsSummary)}</code>` : ''}…`);
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
    case 'approval_request':
      addApprovalRequest(data);
      showThinker('Waiting for your approval…');
      break;
    case 'cancelled':
      appendChat('system', data.message || 'Run cancelled.');
      hideThinker();
      break;
    case 'error':
      if (/rate.?limit|429/i.test(`${data.type || ''} ${data.message || ''}`)) {
        state.rateLimitHit = true;
        showThinker('Model deployment is rate limited. Waiting to retry…');
        break;
      }
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
  // Silent mode: the app-generated continue prompt shouldn't clutter the transcript.
  if (!opts.silent && !opts.approval) appendChat('you', prompt);
  return runStream(opts.approval ? '/api/agent/approval' : '/api/agent/run', opts.approval ? { decisions: opts.approval } : { prompt }, prompt, opts);
}

async function runStream(endpoint, body, retryPrompt, opts = {}) {
  if (state.streaming) return false;
  state.streaming = true;
  state.rateLimitHit = false;
  $('sendButton').disabled = true;
  $('sendButton').hidden = true;
  $('stopButton').hidden = false;
  showThinker(opts.approval ? 'Resuming with your decision…' : opts.silent ? 'Continuing…' : 'Sending prompt…');
  try { await streamRun(endpoint, body); }
  catch (error) {
    if (error.name !== 'AbortError') appendErrorWithRetry(error, retryPrompt);
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
  if (state.rateLimitHit) scheduleRateLimitRetry();
  return true;
}

// The model deployment's tokens-per-minute budget refills quickly; wait and continue instead of failing the demo.
function scheduleRateLimitRetry() {
  state.rateLimitHit = false;
  if (state.rateLimitRetries >= 5) {
    state.rateLimitRetries = 0;
    appendChat('system', 'The model deployment is still rate limited. Raise its tokens-per-minute quota in Foundry, then send a message to continue.');
    return;
  }
  state.rateLimitRetries++;
  const seconds = 20;
  state.pendingAutoContinue = true;
  showThinker(`Model rate limit reached. Retrying in ${seconds}s (attempt ${state.rateLimitRetries} of 5)…`);
  setTimeout(() => {
    state.pendingAutoContinue = false;
    triggerRun('Continue where you left off. Do not repeat tool calls that already succeeded.', { silent: true });
  }, seconds * 1000);
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
$('refreshFilesButton').addEventListener('click', refreshInsights);

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
      await triggerRun('Approved. Execute the plan now.');
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

  const bubbles = document.querySelectorAll('#chatLog .msg-agent:not(.msg-report):not([hidden])');
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
  refreshInsights();
  if (state.rateLimitHit) return; // scheduleRateLimitRetry takes over once the stream closes
  state.rateLimitRetries = 0;

  if (Array.isArray(data.pendingApprovals) && data.pendingApprovals.length > 0) {
    state.autoRunOpenCountBefore = null;
    state.consecutiveNoProgress = 0;
    hideThinker();
    return;
  }

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

// ---------------- Human-in-the-loop tool approval ----------------

function formatArgs(args) {
  let obj = args;
  if (typeof obj === 'string') { try { obj = JSON.parse(obj); } catch { return escapeHtml(obj); } }
  if (!obj || typeof obj !== 'object') return escapeHtml(String(obj ?? ''));
  return Object.entries(obj).map(([k, v]) => `<div class="approval-arg"><span>${escapeHtml(k)}</span><code>${escapeHtml(typeof v === 'object' ? JSON.stringify(v) : String(v))}</code></div>`).join('');
}

function addApprovalRequest(req) {
  if (!state.approvalCard) {
    const el = document.createElement('div');
    el.className = 'msg msg-approval';
    el.innerHTML = `
      <div class="msg-role">human approval required</div>
      <div class="approval-card">
        <div class="approval-card-hint">The harness paused before running a protected tool. Review the exact call, then decide.</div>
        <ul class="approval-items"></ul>
      </div>`;
    $('chatLog').appendChild(el);
    state.approvalCard = el;
    state.approvalItems = new Map();
  }
  const li = document.createElement('li');
  li.className = 'approval-item';
  li.innerHTML = `
    <div class="approval-tool"><span class="approval-shield" aria-hidden="true">🛡</span><code>${escapeHtml(req.tool)}</code></div>
    <div class="approval-args">${formatArgs(req.arguments)}</div>
    <div class="approval-actions">
      <button type="button" class="approval-approve">✔ Approve</button>
      <button type="button" class="approval-reject">✖ Reject</button>
      <span class="approval-verdict"></span>
    </div>`;
  state.approvalCard.querySelector('.approval-items').appendChild(li);
  state.approvalItems.set(req.id, { li, approved: null });
  const decide = (approved) => {
    const item = state.approvalItems.get(req.id);
    item.approved = approved;
    li.querySelectorAll('button').forEach((b) => { b.disabled = true; });
    li.classList.add(approved ? 'decided-approved' : 'decided-rejected');
    li.querySelector('.approval-verdict').textContent = approved ? 'Approved' : 'Rejected';
    submitApprovalsWhenComplete();
  };
  li.querySelector('.approval-approve').addEventListener('click', () => decide(true));
  li.querySelector('.approval-reject').addEventListener('click', () => decide(false));
  $('chatLog').scrollTop = $('chatLog').scrollHeight;
}

async function submitApprovalsWhenComplete() {
  const items = [...state.approvalItems.entries()];
  if (items.some(([, v]) => v.approved === null)) return;
  const decisions = items.map(([id, v]) => ({ id, approved: v.approved, reason: v.approved ? undefined : 'Rejected by the human reviewer.' }));
  state.approvalCard.classList.add('approval-done');
  state.approvalCard = null;
  state.approvalItems = new Map();
  // The approval request ends the current stream; wait for it to close before resuming.
  while (state.streaming) await new Promise((r) => setTimeout(r, 100));
  state.autoRunOpenCountBefore = null;
  state.consecutiveNoProgress = 0;
  await triggerRun('', { approval: decisions });
}

// ---------------- Trace + files panels ----------------

async function refreshInsights() {
  try {
    const [trace, files] = await Promise.all([api('/api/agent/trace'), api('/api/agent/artifacts')]);
    renderTrace(trace.spans || []);
    renderFiles(files.files || []);
    if (state.seenArtifacts) {
      for (const f of files.files || []) {
        if (state.seenArtifacts.get(f.path) === f.modifiedUtc) continue;
        state.seenArtifacts.set(f.path, f.modifiedUtc);
        appendReport(f.path);
      }
    }
  } catch { /* panels are best-effort */ }
}

// Files that already existed before this session starts are not announced as new reports.
async function seedArtifacts() {
  state.seenArtifacts = new Map();
  try {
    const files = await api('/api/agent/artifacts');
    for (const f of files.files || []) state.seenArtifacts.set(f.path, f.modifiedUtc);
  } catch { /* ignore */ }
}

async function appendReport(path) {
  const res = await fetch(`/api/agent/artifact?path=${encodeURIComponent(path)}`);
  if (!res.ok) return;
  const el = appendChat('agent', await res.text());
  el.classList.add('msg-report');
  el.querySelector('.msg-role').firstChild.textContent = `saved report · ${path}`;
  // A pending approval is the thing the human must act on, so it stays last.
  if (state.approvalCard) $('chatLog').insertBefore(el, state.approvalCard);
  $('chatLog').scrollTop = $('chatLog').scrollHeight;
}

function renderTrace(spans) {
  const list = $('traceList');
  const summary = $('traceSummary');
  if (!spans.length) {
    summary.textContent = '';
    list.innerHTML = '<li class="side-empty">No spans yet.</li>';
    return;
  }
  const total = Math.max(...spans.map((s) => s.startMs + s.durationMs), 1);
  const count = (k) => spans.filter((s) => s.kind === k).length;
  summary.textContent = `${spans.length} spans · ${count('model')} model · ${count('tool')} tool · ${count('agent')} agent · ${(total / 1000).toFixed(1)}s`;
  list.innerHTML = spans.slice(-80).map((s) => {
    const left = Math.min(98, (s.startMs / total) * 100);
    const width = Math.max(1.5, Math.min(100 - left, (s.durationMs / total) * 100));
    return `<li class="span span-${escapeHtml(s.kind)}" title="${escapeHtml(s.source)}">
      <div class="span-head"><span class="span-kind">${escapeHtml(s.kind)}</span><span class="span-name">${escapeHtml(s.name)}</span><span class="span-ms">${Math.round(s.durationMs)} ms</span></div>
      ${s.detail ? `<div class="span-detail">${escapeHtml(s.detail)}</div>` : ''}
      <div class="span-bar"><i style="left:${left.toFixed(1)}%;width:${width.toFixed(1)}%"></i></div>
    </li>`;
  }).join('');
}

function renderFiles(files) {
  const list = $('filesList');
  if (!files.length) { list.innerHTML = '<li class="side-empty">No files saved yet.</li>'; return; }
  list.innerHTML = files.map((f) => `
    <li class="file-row"><button type="button" class="link file-open" data-path="${escapeHtml(f.path)}">${escapeHtml(f.path)}</button><span class="file-size">${f.size < 1024 ? f.size + ' B' : (f.size / 1024).toFixed(1) + ' KB'}</span></li>`).join('');
  for (const b of list.querySelectorAll('.file-open')) b.addEventListener('click', () => openArtifact(b.dataset.path));
}

async function openArtifact(path) {
  const url = `/api/agent/artifact?path=${encodeURIComponent(path)}`;
  try {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const text = await res.text();
    $('fileTitle').textContent = path;
    $('fileBody').innerHTML = renderMarkdown(text);
    $('fileDownload').href = url;
    $('fileDownload').setAttribute('download', path.split('/').pop());
    $('fileModal').hidden = false;
  } catch (error) {
    alert(`Could not open ${path}: ${error.message}`);
  }
}
$('closeFile').addEventListener('click', () => { $('fileModal').hidden = true; });

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
  await triggerRun('Approved. Execute the plan now.');
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
