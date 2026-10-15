'use strict';
const $ = id => document.getElementById(id);
const state = { session: null, generation: 0, pollId: 0, controller: null, timer: null, expiry: null, screen: 'Cameras', config: null, events: [], cameras: [], executions: [], completed: [] };
const results = ['Queued','Running','Accepted','HTTP failure','Transport failure','Timeout','Cancelled','Stale','Expired','Overflow'];
function node(tag, text, parent) { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (parent) parent.append(n); return n; }
function button(text, parent, action) { const b = node('button', text, parent); b.type = 'button'; b.onclick = () => run(action); return b; }
function feedback(text) { $('feedback').textContent = text; }
function stop() { clearTimeout(state.timer); clearTimeout(state.expiry); state.controller?.abort(); state.generation++; state.pollId++; }
function signedOut(message) {
  stop(); state.session = null; state.config = null; state.events = []; state.cameras = []; state.executions = []; state.completed = [];
  $('workspace').hidden = true; $('login').hidden = false; $('screen').replaceChildren(); $('details').textContent = ''; $('draft-fields').replaceChildren(); $('fields').replaceChildren();
  $('editor').close(); $('inspection').close(); feedback(message);
}
async function api(path, method = 'GET', body, signal) {
  const headers = {}; if (method !== 'GET') headers['X-CSRF-Token'] = state.session?.csrfToken ?? '';
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch('/api/' + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), credentials: 'same-origin', cache: 'no-store', signal });
  if (response.status === 401 && path !== 'login') { signedOut('Session expired. Sign in again. Unsaved edits were discarded.'); throw new Error('Sign in is required.'); }
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    if (error.code === 'RevisionConflict') throw new Error('Configuration changed. Cancel this edit, refresh, then review and resubmit. Your edit has not been saved.');
    throw new Error(error.message || `Request failed (${response.status}).`);
  }
  return response.status === 204 || response.status === 202 ? null : response.json();
}
async function run(action) { try { await action(); } catch (e) { if (e.name !== 'AbortError') feedback(e.message); } }
function activate(session) {
  stop(); state.session = session; $('login').hidden = true; $('workspace').hidden = false;
  state.expiry = setTimeout(() => signedOut('Session expired. Sign in again.'), Math.max(0, Date.parse(session.expires) - Date.now()));
  refresh();
}
async function refresh() {
  clearTimeout(state.timer); if (!state.session) return;
  const generation = state.generation, pollId = ++state.pollId; state.controller?.abort(); const controller = new AbortController(); state.controller = controller;
  const deadline = setTimeout(() => controller.abort(), 12000);
  try {
    // Sequential, bounded requests. Schedule the next cycle only after this one finishes.
    const config = await api('configuration', 'GET', undefined, controller.signal);
    const cameras = await api('camera-status', 'GET', undefined, controller.signal);
    const events = await api('events', 'GET', undefined, controller.signal);
    const executions = await api('execution-status', 'GET', undefined, controller.signal);
    const completed = await api('executions', 'GET', undefined, controller.signal);
    if (generation !== state.generation || pollId !== state.pollId) return;
    Object.assign(state, { config, cameras, events, executions, completed }); render(); $('connection').textContent = 'Connected · refresh every 3 seconds · latest 100 events within the history byte limit';
  } catch (e) { if (generation === state.generation && state.session) $('connection').textContent = 'Disconnected or refresh failed. Retrying in 3 seconds. Displayed data may be stale.'; }
  finally { clearTimeout(deadline); if (generation === state.generation && pollId === state.pollId && state.session) state.timer = setTimeout(refresh, 3000); }
}
$('login').onsubmit = e => { e.preventDefault(); run(async () => {
  const b = $('login').querySelector('button'); b.disabled = true;
  try { const form = new FormData($('login')); const session = await api('login', 'POST', { username: form.get('username'), password: form.get('password') }); $('login').reset(); feedback('Signed in.'); activate(session); }
  finally { b.disabled = false; }
}); };
$('logout').onclick = () => run(async () => {
  stop();
  try { await api('logout', 'POST'); signedOut('Logged out. Polling stopped.'); }
  catch (e) { signedOut('Local screen cleared and polling stopped. Logout could not be confirmed; sign in and retry to revoke the server session.'); }
});
for (const b of document.querySelectorAll('[data-screen]')) b.onclick = () => { state.screen = b.dataset.screen; render(); };
$('cancel').onclick = () => { $('editor').close(); $('fields').replaceChildren(); };
$('editor').addEventListener('close', () => $('fields').replaceChildren());
$('close-inspection').onclick = () => $('inspection').close();
function statusText(s) { return s ? `${results[s.result] ?? 'Unknown'} · ${s.time} · attempts ${s.attempts} · HTTP ${s.statusCode ?? '—'} · duration ${s.duration}` : 'No execution in this process'; }
async function mutate(path, method, body) { await api(path, method, body); feedback('Saved.'); await refresh(); }
function render() {
  if (!state.config || !state.session) return;
  for (const b of document.querySelectorAll('[data-screen]')) b.setAttribute('aria-current', b.dataset.screen === state.screen ? 'page' : 'false');
  const area = $('screen'); area.replaceChildren(); node('h2', state.screen, area);
  button('Refresh now', area, refresh);
  if (state.screen === 'Cameras') {
    button('Add camera', area, () => editCamera());
    for (const c of state.config.cameras) {
      const a = node('article', undefined, area); node('h3', c.name, a); node('p', c.endpoint, a);
      const s = state.cameras.find(x => x.cameraId === c.id);
      node('p', `${c.enabled ? s?.state ?? 'Connecting' : 'Disabled'} · last contact ${s?.lastContact ?? 'none'} · last event ${s?.lastEvent ?? 'none'} · failures ${s?.failures ?? 0}`, a);
      const actions = node('div', undefined, a); actions.className = 'actions';
      button('Edit camera', actions, () => editCamera(c));
      button(c.enabled ? 'Disable camera' : 'Enable camera', actions, () => mutate(`cameras/${encodeURIComponent(c.id)}/enabled`, 'POST', { revision: state.config.revision, enabled: !c.enabled }));
      button('Test connectivity', actions, async () => { const r = await api(`cameras/${encodeURIComponent(c.id)}/test`, 'POST'); feedback(`Connectivity: ${r.result}. Reachable: ${r.reachable}. This does not prove event subscription readiness.`); });
      button('Delete camera', actions, async () => { if (confirm('Delete this camera? Remove its rules first.')) await mutate(`cameras/${encodeURIComponent(c.id)}`, 'DELETE', { revision: state.config.revision }); });
    }
  } else if (state.screen === 'Rules') {
    button('Add rule', area, () => editRule());
    node('p', 'Manual HTTP tests send real requests to the saved destination, including for disabled rules. A camera must be enabled. Retries may duplicate effects.', area);
    for (const r of state.config.rules) {
      const a = node('article', undefined, area); node('h3', r.name, a); node('p', `${r.match.enabled ? 'Enabled' : 'Disabled'} · ${r.method} · ${r.match.topic}`, a);
      node('p', 'Current: ' + statusText(state.executions.find(x => x.ruleId === r.match.id)), a);
      node('p', 'Last completed: ' + statusText(state.completed.find(x => x.ruleId === r.match.id)), a);
      const actions = node('div', undefined, a); actions.className = 'actions';
      button('Edit rule', actions, () => editRule(r));
      button(r.match.enabled ? 'Disable rule' : 'Enable rule', actions, () => mutate(`rules/${encodeURIComponent(r.match.id)}/enabled`, 'POST', { revision: state.config.revision, enabled: !r.match.enabled }));
      button('Send manual HTTP test…', actions, async () => { if (confirm('Send the saved HTTP action now? This bypasses matching/cooldown and can cause an external action. Retries can duplicate effects.')) { await api(`rules/${encodeURIComponent(r.match.id)}/test`, 'POST'); feedback('Manual test queued. Inspect execution status; queue acceptance does not prove delivery or destination effects.'); await refresh(); } });
      button('Delete rule', actions, async () => { if (confirm('Delete this rule?')) await mutate(`rules/${encodeURIComponent(r.match.id)}`, 'DELETE', { revision: state.config.revision }); });
    }
  } else {
    node('p', 'All retained notifications, including false, unmatched, Initialized and Deleted. History is transient and bounded.', area);
    for (const e of [...state.events].reverse()) {
      const a = node('article', undefined, area); node('h3', e.cameraName, a); node('p', `${e.receivedAt} · ${e.propertyOperation ?? 'No operation'}${e.truncated ? ' · TRUNCATED' : ''}`, a); node('p', e.topic, a);
      button('Inspect notification / create rule', a, () => inspect(e));
    }
  }
}
function input(parent, label, value = '', type = 'text', required = false) {
  const l = node('label', label, parent); const i = node(type === 'textarea' ? 'textarea' : 'input', undefined, l);
  if (type !== 'textarea') i.type = type;
  if (type === 'checkbox') i.checked = Boolean(value); else i.value = value;
  i.required = required; i.autocomplete = 'off'; return i;
}
function select(parent, label, choices, value) {
  const l = node('label', label, parent); const s = node('select', undefined, l);
  for (const [v, text] of choices) { const o = node('option', text, s); o.value = v; } s.value = String(value); return s;
}
function secret(parent, label, present, kind = 'password', required = false) {
  const box = node('fieldset', undefined, parent); node('legend', `${label} · ${present ? 'stored (masked)' : 'not stored'}`, box);
  const choices = [['0','Preserve stored value'],['1','Replace with value below']]; if (!required) choices.push(['2','Clear stored value']);
  const op = select(box, 'Operation', choices, required && !present ? 1 : 0);
  const value = input(box, 'Replacement value', '', kind); value.hidden = op.value !== '1';
  op.onchange = () => { value.hidden = op.value !== '1'; value.required = op.value === '1'; if (op.value !== '1') value.value = ''; }; op.onchange();
  return () => { const edit = { operation: Number(op.value) }; if (edit.operation === 1) { edit.value = kind === 'textarea' && label === 'HTTP headers' ? JSON.parse(value.value) : value.value; } return edit; };
}
function editor(title, help, build) {
  $('fields').replaceChildren(); $('edit-title').textContent = title; $('edit-help').textContent = help; $('edit-error').textContent = '';
  const generation = state.generation; const submit = build($('fields')); $('editor').showModal();
  $('edit-form').onsubmit = async e => {
    e.preventDefault(); $('save').disabled = true; $('edit-error').textContent = '';
    try { await submit(); if (generation === state.generation && state.session) { $('editor').close(); feedback('Saved.'); await refresh(); } }
    catch (error) { if (state.session) $('edit-error').textContent = error instanceof SyntaxError ? 'Headers must be a JSON array of name/value objects.' : error.message; }
    finally { $('save').disabled = false; }
  };
}
function editCamera(c) {
  const revision = state.config.revision, id = c?.id ?? crypto.randomUUID();
  editor(c ? 'Edit camera' : 'Add camera', 'Credentials are never read back. Choose Preserve, Replace or Clear explicitly. Clear both username and password for unauthenticated access.', p => {
    const name = input(p, 'Name', c?.name ?? '', 'text', true), endpoint = input(p, 'Device-service URL', c?.endpoint ?? '', 'url', true), enabled = input(p, 'Enabled', c?.enabled ?? false, 'checkbox');
    const username = secret(p, 'Camera username', c?.hasUsername), password = secret(p, 'Camera password', c?.hasPassword);
    return () => api(`cameras/${encodeURIComponent(id)}`, 'PUT', { revision, name: name.value, endpoint: endpoint.value, enabled: enabled.checked, username: username(), password: password() });
  });
}
function seconds(text) { const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(text ?? '00:00:00'); return m ? Number(m[1] ?? 0)*86400 + Number(m[2])*3600 + Number(m[3])*60 + Number(m[4]) : 0; }
function editRule(r, draft) {
  if (!state.config.cameras.length) { feedback('Add a camera before creating a rule.'); return; }
  const revision = state.config.revision, id = r?.match.id ?? crypto.randomUUID(), m = r?.match ?? draft ?? {};
  editor(r ? 'Edit rule' : 'Review new disabled rule', 'Review the exact matching topic and Source/Data selector. New rules save disabled. Transition needs a known nonmatching-to-matching change; the first observation establishes a baseline. Retries can duplicate effects. Masked action values cannot be read back.', p => {
    const name = input(p, 'Rule name', r?.name ?? '', 'text', true);
    const camera = select(p, 'Camera', state.config.cameras.map(c => [c.id,c.name]), m.cameraId ?? state.config.cameras[0].id);
    const topic = input(p, 'Exact matching topic', m.topic ?? '', 'text', true);
    const section = select(p, 'Field condition', [['','Topic only'],['0','Source'],['1','Data']], m.condition?.section ?? '');
    const field = input(p, 'Field name', m.condition?.name ?? ''), expected = input(p, 'Expected value (exact text)', m.condition?.expectedValue ?? '');
    const comparison = select(p, 'Comparison', [[0,'Text: ordinal exact'],[1,'Boolean: true/1 and false/0']], m.condition?.comparison ?? 0);
    const mode = select(p, 'Mode', [[0,'Occurrence'],[1,'Transition']], m.mode ?? 0);
    const cooldown = input(p, 'Cooldown seconds', seconds(m.cooldown), 'number'); cooldown.min = '0'; cooldown.step = 'any';
    const enabled = input(p, 'Enabled', r ? m.enabled : false, 'checkbox'); if (!r) enabled.disabled = true;
    const method = select(p, 'HTTP method', [['GET','GET'],['POST','POST']], r?.method ?? 'GET');
    const attempts = select(p, 'Additional attempts (may duplicate effects)', [[0,'0 — no retries'],[1,'1 retry'],[2,'2 retries']], r?.additionalAttempts ?? 0);
    const url = secret(p, 'Action URL', r?.hasUrl, 'password', true);
    node('p', 'Header replacement format: [{"name":"X-Example","value":"<synthetic-value>"}]. Body replacement must be literal JSON for POST.', p);
    const headers = secret(p, 'HTTP headers', r?.hasHeaders, 'textarea'), jsonBody = secret(p, 'JSON body', r?.hasJsonBody, 'textarea');
    return () => {
      if (section.value !== '' && !field.value) throw new Error('A field condition requires a field name.');
      if (mode.value === '1' && section.value === '') throw new Error('Transition mode requires a field condition.');
      return api(`rules/${encodeURIComponent(id)}`, 'PUT', { revision, name: name.value, cameraId: camera.value, topic: topic.value, condition: section.value === '' ? null : { section: Number(section.value), name: field.value, expectedValue: expected.value, comparison: Number(comparison.value) }, mode: Number(mode.value), cooldownSeconds: Number(cooldown.value), enabled: r ? enabled.checked : false, method: method.value, additionalAttempts: Number(attempts.value), url: url(), headers: headers(), jsonBody: jsonBody() });
    };
  });
}
function inspect(e) {
  $('details').textContent = JSON.stringify(e, null, 2); const fields = $('draft-fields'); fields.replaceChildren();
  const draft = condition => { $('inspection').close(); editRule(null, { cameraId: e.cameraId, topic: e.topic, condition }); };
  if (e.truncated) node('p', 'Truncated notification: wait for a complete observation before creating an exact rule.', fields);
  else {
    button('Draft topic-only rule', fields, () => draft(null));
    for (const [section, name] of [[0,'source'],[1,'data']]) for (const f of e[name]) {
      if (f.kind === 'SimpleItem' && f.name != null && f.value != null) button(`Draft from ${name}: ${f.name} = ${f.value}`, fields, () => draft({ section, name: f.name, expectedValue: f.value, comparison: 0 }));
    }
  }
  $('inspection').showModal();
}
run(async () => { try { activate(await api('session')); } catch { signedOut('Sign in to manage cameras, notifications and rules.'); } });
