// Headless integration test — exercises the full playground stack the same way the browser does.
// Boots the .NET host (via env var), calls /api/scenarios → /api/session/start → /api/run,
// parses the SSE stream, and prints event counts + tool names + final status.

const BASE = process.env.TEST_BASE || 'http://127.0.0.1:5099';
const SCENARIO = process.env.TEST_SCENARIO || 'financial-analyst';
const PROMPT = process.env.TEST_PROMPT || 'Build me an investment thesis for NVDA with a 12-month horizon.';
const AUTO_APPROVE = process.env.TEST_AUTO_APPROVE === '1';

function log(...args) { console.log(new Date().toISOString().slice(11, 19), ...args); }

async function runOnce(prompt, tag) {
  log(`--- ${tag}: POST /api/run "${prompt.slice(0, 60)}${prompt.length > 60 ? '…' : ''}" ---`);
  const t0 = Date.now();
  const res = await fetch(`${BASE}/api/run`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
    body: JSON.stringify({ prompt })
  });
  log(`  HTTP ${res.status}`);
  if (!res.ok || !res.body) {
    log('  ERROR body:', await res.text().catch(() => '<unreadable>'));
    return null;
  }

  const events = {};
  const tools = [];
  let text = '';
  let messageBoundaries = 0;
  let done = null;
  let error = null;

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  while (true) {
    const { value, done: eof } = await reader.read();
    if (eof) break;
    buffer += decoder.decode(value, { stream: true });
    let idx;
    while ((idx = buffer.indexOf('\n\n')) !== -1) {
      const raw = buffer.slice(0, idx);
      buffer = buffer.slice(idx + 2);
      const lines = raw.split('\n');
      let evt = 'message';
      let dataStr = '';
      for (const l of lines) {
        if (l.startsWith('event:')) evt = l.slice(6).trim();
        else if (l.startsWith('data:')) dataStr += l.slice(5).trim();
      }
      if (!dataStr) continue;
      let data; try { data = JSON.parse(dataStr); } catch { data = { raw: dataStr }; }
      events[evt] = (events[evt] || 0) + 1;
      if (evt === 'text') text += data.text || '';
      if (evt === 'tool_call') tools.push({ name: data.name, capability: data.capability, args: data.arguments });
      if (evt === 'message_start') messageBoundaries++;
      if (evt === 'done') done = data;
      if (evt === 'error') error = data;
    }
  }

  const elapsed = ((Date.now() - t0) / 1000).toFixed(1);
  log(`  finished in ${elapsed}s`);
  log(`  event counts: ${JSON.stringify(events)}`);
  log(`  message boundaries: ${messageBoundaries}`);
  log(`  tools called (${tools.length}): ${JSON.stringify(tools.map((t) => t.name))}`);
  if (error) log(`  ERROR event: type=${error.type} message=${error.message} detail=${error.detail || ''}`);
  if (done) {
    log(`  mode after: ${done.mode}`);
    log(`  todos: ${done.todos.filter((t) => t.isComplete).length}/${done.todos.length} complete`);
    log(`  exercised capabilities (${done.exercisedCapabilities.length}): ${done.exercisedCapabilities.join(', ')}`);
  }
  if (text) log(`  text sample (first 240 chars): ${text.slice(0, 240).replace(/\n/g, ' ')}`);
  return done;
}

async function main() {
  log(`=== Testing against ${BASE} scenario=${SCENARIO} ===`);

  // 1. Health
  log('--- GET /health ---');
  const health = await fetch(`${BASE}/health`);
  log(`  HTTP ${health.status}: ${JSON.stringify(await health.json())}`);

  // 2. Scenarios
  log('--- GET /api/scenarios ---');
  const scen = await fetch(`${BASE}/api/scenarios`);
  const scenBody = await scen.json();
  log(`  HTTP ${scen.status} — ${scenBody.scenarios?.length ?? 0} scenarios, ${scenBody.capabilities?.length ?? 0} capabilities`);
  log(`  scenarios: ${scenBody.scenarios?.map((s) => s.id).join(', ')}`);

  // 3. Start scenario
  log(`--- POST /api/session/start { scenarioId: '${SCENARIO}' } ---`);
  const start = await fetch(`${BASE}/api/session/start`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ scenarioId: SCENARIO })
  });
  const startBody = await start.json();
  log(`  HTTP ${start.status} — mode=${startBody.mode}, todos=${startBody.todos?.length ?? 0}, model=${startBody.model}`);

  // 4. Plan-mode run
  let last = await runOnce(PROMPT, 'PLAN');
  if (!last) { log('ABORT: plan run failed'); process.exit(1); }

  // 5. If auto-approve, flip to execute and drive until done or stalled.
  if (AUTO_APPROVE && last.mode === 'plan' && last.todos.some((t) => !t.isComplete)) {
    log('--- POST /api/mode { mode: "execute" } ---');
    const mode = await fetch(`${BASE}/api/mode`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ mode: 'execute' })
    });
    const modeBody = await mode.json();
    log(`  HTTP ${mode.status} — mode=${modeBody.mode}`);
    last = modeBody;

    let prevOpen = last.todos.filter((t) => !t.isComplete).length;
    let stalls = 0;
    let turn = 1;
    while (last && last.todos.some((t) => !t.isComplete) && turn <= 8) {
      const openIds = last.todos.filter((t) => !t.isComplete).map((t) => t.id).join(', ');
      const prompt = turn === 1
        ? 'Approved. Please continue with the checklist and mark each item done as you finish it.'
        : `Continue. Call the next tool for open todo ids: ${openIds}. After each tool result, call todos_complete for that id.`;
      last = await runOnce(prompt, `EXECUTE turn ${turn}`);
      if (!last) break;
      const openNow = last.todos.filter((t) => !t.isComplete).length;
      if (openNow >= prevOpen) {
        stalls++;
        log(`  STALL detected (open ${prevOpen} → ${openNow}). stall count=${stalls}`);
        if (stalls >= 2) { log('STOP: two consecutive stalls'); break; }
      } else {
        stalls = 0;
      }
      prevOpen = openNow;
      turn++;
    }
  }

  // 6. Summary
  log('--- GET /api/session/summary ---');
  const summary = await fetch(`${BASE}/api/session/summary`);
  const summaryBody = await summary.json();
  log(`  HTTP ${summary.status}`);
  log(`  duration: ${summaryBody.durationSeconds}s, runs: ${summaryBody.runCount}, tool calls: ${summaryBody.toolCallCount}`);
  log(`  distinct tools (${summaryBody.distinctToolsCalled?.length ?? 0}): ${summaryBody.distinctToolsCalled?.join(', ')}`);
  log(`  tokens in/out: ${summaryBody.inputTokens}/${summaryBody.outputTokens}`);
  const usedIds = summaryBody.capabilities?.filter((c) => c.exercised).map((c) => c.id);
  log(`  capabilities exercised (${usedIds?.length ?? 0}): ${usedIds?.join(', ')}`);
  log(`  final todos: ${summaryBody.todos?.filter((t) => t.isComplete).length}/${summaryBody.todos?.length} complete`);

  log('=== DONE ===');
}

main().catch((err) => { log('FATAL:', err.message, err.stack); process.exit(2); });
