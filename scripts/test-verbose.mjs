// Detailed test that prints the FULL text of every assistant message so we can inspect
// whether the model is now producing one clean memo (or still narrating each step).

const BASE = process.env.TEST_BASE || 'http://127.0.0.1:5099';
const SCENARIO = process.env.TEST_SCENARIO || 'financial-analyst';
const PROMPT = process.env.TEST_PROMPT || 'Build me an investment thesis for NVDA with a 12-month horizon.';

function log(...a) { console.log(new Date().toISOString().slice(11, 19), ...a); }

async function runOnce(prompt, tag) {
  log(`--- ${tag} ---`);
  log(`prompt: ${prompt.slice(0, 100)}${prompt.length > 100 ? '…' : ''}`);
  const res = await fetch(`${BASE}/api/run`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
    body: JSON.stringify({ prompt })
  });
  log(`HTTP ${res.status}`);
  if (!res.ok || !res.body) return null;

  const events = {};
  const tools = [];
  const messages = [];
  let currentMessage = '';
  let done = null;

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
      if (evt === 'message_start') {
        if (currentMessage.trim()) messages.push(currentMessage);
        currentMessage = '';
      }
      if (evt === 'text') currentMessage += data.text || '';
      if (evt === 'tool_call') tools.push(`${data.name}(${JSON.stringify(data.arguments).slice(0, 100)})`);
      if (evt === 'done') { if (currentMessage.trim()) messages.push(currentMessage); done = data; }
    }
  }

  log(`event counts: ${JSON.stringify(events)}`);
  log(`tool calls (${tools.length}):`);
  tools.forEach((t, i) => log(`  ${i + 1}. ${t}`));
  log(`assistant messages (${messages.length}):`);
  messages.forEach((m, i) => {
    log(`  === message ${i + 1} (${m.length} chars) ===`);
    console.log(m);
    log(`  === end message ${i + 1} ===`);
  });
  if (done) log(`todos: ${done.todos.filter((t) => t.isComplete).length}/${done.todos.length} complete`);
  return done;
}

async function main() {
  log(`=== ${SCENARIO} @ ${BASE} ===`);
  await fetch(`${BASE}/api/session/start`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ scenarioId: SCENARIO }) });
  const plan = await runOnce(PROMPT, 'PLAN');
  if (!plan || !plan.todos.some((t) => !t.isComplete)) return;
  await fetch(`${BASE}/api/mode`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ mode: 'execute' }) });
  await runOnce('Approved. Please continue with the checklist and mark each item done as you finish it.', 'EXECUTE turn 1');
}
main().catch((e) => { log('FATAL:', e.message); process.exit(1); });
