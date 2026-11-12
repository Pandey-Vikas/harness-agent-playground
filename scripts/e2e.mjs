// Headless end-to-end test that mimics the UI: plan -> approve -> execute (auto-continue) -> tool approvals.
// Usage: node scripts/e2e.mjs [financial-analyst|incident-commander|launch-studio] ["prompt"]
// Env: TEST_BASE (default http://127.0.0.1:5099), TEST_APPROVE=yes|no (answer to tool-approval requests), TEST_FULL=1 (print every message)

const BASE = process.env.TEST_BASE || 'http://127.0.0.1:5099';
const SCENARIO = process.argv[2] || 'financial-analyst';
const APPROVE = (process.env.TEST_APPROVE || 'yes') === 'yes';
const FULL = process.env.TEST_FULL === '1';

const DEFAULT_PROMPTS = {
  'financial-analyst': 'Build me an investment thesis for Microsoft (MSFT) with a 12-month horizon.',
  'incident-commander': 'Alert: checkout-service p95 latency spiked at 14:12 UTC. Investigate and propose a fix.',
  'launch-studio': 'Design a 4-week launch campaign for our AI notes app targeting college students in India.'
};
const PROMPT = process.argv[3] || DEFAULT_PROMPTS[SCENARIO];

const t0 = Date.now();
const log = (...a) => console.log(`[${String(Math.round((Date.now() - t0) / 1000)).padStart(3)}s]`, ...a);
const post = (path, body) => fetch(`${BASE}${path}`, { method: 'POST', headers: { 'content-type': 'application/json', accept: 'text/event-stream' }, body: JSON.stringify(body) });

async function consume(res, tag, report) {
  if (!res.ok || !res.body) { log(`${tag}: HTTP ${res.status} ${await res.text()}`); report.failures.push(`${tag} HTTP ${res.status}`); return null; }
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '', done = null, current = '';
  const approvals = [];
  const flush = () => { if (current.trim()) { report.messages.push(current); log(`${tag} message (${current.length} chars, after ${report.tools.length} tool calls): ${current.replace(/\s+/g, ' ').slice(0, 90)}`); } current = ''; };
  for (; ;) {
    const { value, done: eof } = await reader.read();
    if (eof) break;
    buffer += decoder.decode(value, { stream: true });
    let i;
    while ((i = buffer.indexOf('\n\n')) !== -1) {
      const raw = buffer.slice(0, i); buffer = buffer.slice(i + 2);
      let evt = 'message', data = '';
      for (const l of raw.split('\n')) { if (l.startsWith('event:')) evt = l.slice(6).trim(); else if (l.startsWith('data:')) data += l.slice(5).trim(); }
      if (!data) continue;
      const d = JSON.parse(data);
      if (evt === 'message_start') flush();
      else if (evt === 'text') current += d.text || '';
      else if (evt === 'tool_call') { report.tools.push(d.name); log(`${tag} tool: ${d.name}`); }
      else if (evt === 'approval_request') { approvals.push(d); log(`${tag} APPROVAL REQUEST: ${d.tool} ${JSON.stringify(d.arguments)}`); }
      else if (evt === 'error') {
        log(`${tag} ERROR: ${d.message} ${d.type || ''}`);
        if (/rate.?limit|429/i.test(`${d.type} ${d.message}`)) report.rateLimited = true; else report.failures.push(`${tag} error: ${d.message}`);
      }
      else if (evt === 'done') done = d;
    }
  }
  flush();
  return { done, approvals };
}

async function main() {
  const report = { tools: [], messages: [], failures: [] };
  log(`=== ${SCENARIO} ===`);
  const start = await (await fetch(`${BASE}/api/session/start`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ scenarioId: SCENARIO }) })).json();
  log(`started: mode=${start.mode}`);
  if (SCENARIO === 'incident-commander') {
    // The UI injects the live fault when the scenario starts; wait so the probes record degraded samples.
    const chaos = await (await post('/api/chaos', { enable: true })).json();
    log(`injected fault ${chaos.changeId}; collecting degraded samples for 45s`);
    await new Promise((ok) => setTimeout(ok, 45000));
  }

  let r = await consume(await post('/api/run', { prompt: PROMPT }), 'plan', report);
  const planTodos = r?.done?.todos?.length ?? 0;
  log(`plan todos: ${planTodos}`);
  if (planTodos === 0) report.failures.push('no plan todos created');

  await fetch(`${BASE}/api/mode`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ mode: 'execute' }) });
  let res = await post('/api/run', { prompt: 'Approved. Execute the plan now.' });
  for (let turn = 1; turn <= 12; turn++) {
    r = await consume(res, `exec${turn}`, report);
    if (!r?.done) break;
    if (report.rateLimited) {
      report.rateLimited = false;
      log('rate limited -> waiting 25s then continuing');
      await new Promise((ok) => setTimeout(ok, 25000));
      res = await post('/api/run', { prompt: 'Continue where you left off. Do not repeat tool calls that already succeeded.' });
      continue;
    }
    if (r.approvals.length) {
      const decisions = r.approvals.map((a) => ({ id: a.id, approved: APPROVE, reason: APPROVE ? undefined : 'Rejected by tester' }));
      log(`responding to ${decisions.length} approval(s): approved=${APPROVE}`);
      res = await post('/api/approval', { decisions });
      continue;
    }
    const open = r.done.todos.filter((t) => !t.isComplete);
    if (open.length === 0) break;
    log(`${open.length} todo(s) still open -> auto-continue`);
    res = await post('/api/run', { prompt: `Continue. Call the next tool for open todo ids: ${open.map((t) => t.id).join(', ')}. After each tool result, call todos_complete for that id.` });
  }

  const status = await (await fetch(`${BASE}/api/session/status`)).json();
  const summary = await (await fetch(`${BASE}/api/session/summary`)).json();
  const trace = await (await fetch(`${BASE}/api/session/trace`)).json();
  const files = await (await fetch(`${BASE}/api/artifacts`)).json();
  const kinds = {};
  for (const s of trace.spans) kinds[s.kind] = (kinds[s.kind] || 0) + 1;

  console.log('\n=== RESULT ===');
  console.log('todos:', `${status.todos.filter((t) => t.isComplete).length}/${status.todos.length}`, '| pending approvals:', status.pendingApprovals.length);
  console.log('distinct tools:', [...new Set(report.tools)].join(', '));
  console.log('exercised:', summary.capabilities.filter((c) => c.exercised).map((c) => c.id).join(', '));
  console.log('highlighted but NOT exercised:', summary.capabilities.filter((c) => c.highlighted && !c.exercised).map((c) => c.id).join(', ') || '(none)');
  console.log('trace spans:', trace.spans.length, JSON.stringify(kinds));
  console.log('artifacts:', files.files.map((f) => `${f.path} (${f.size}B)`).join(', ') || '(none)');
  console.log('assistant messages:', report.messages.map((m) => m.length).join(', '));
  const last = report.messages[report.messages.length - 1] || '';
  console.log(FULL ? report.messages.join('\n\n----\n\n') : `last message preview:\n${last.slice(0, 700)}`);
  if (report.failures.length) { console.log('\nFAILURES:', report.failures); process.exit(1); }
}
main().catch((e) => { console.error('FATAL', e); process.exit(1); });
