'use strict';
const $ = id => document.getElementById(id);
const state = { session: null, generation: 0, pollId: 0, controller: null, timer: null, expiry: null, screen: 'Overview', config: null, events: [], cameras: [], executions: [], completed: [], search: '' };
const results = ['Queued','Running','Accepted','HTTP failure','Transport failure','Timeout','Cancelled','Stale','Expired','Overflow'];
let renderPending = false, selecting = false, selectionTimer = null;
function selectionActive() { return selecting || document.getSelection()?.isCollapsed === false; }
function resumeRendering() {
  clearTimeout(selectionTimer);
  // Wait until the click/keyboard action that cleared selection has finished.
  selectionTimer = setTimeout(() => { if (renderPending && state.session && !selectionActive()) render(true); }, 100);
}
document.addEventListener('selectionchange', resumeRendering);
document.addEventListener('pointerdown', () => { selecting = true; });
document.addEventListener('pointerup', () => { selecting = false; resumeRendering(); });
document.addEventListener('pointercancel', () => { selecting = false; resumeRendering(); });
window.addEventListener('blur', () => { selecting = false; });
function node(tag, text, parent) { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (parent) parent.append(n); return n; }
function button(text, parent, action) { const b = node('button', text, parent); b.type = 'button'; b.onclick = () => run(action); return b; }
function badge(parent, text, tone = '') { const n = node('span', text, parent); n.className = 'badge ' + tone; }
function feedback(text) { $('feedback').textContent = text; }
function stop() { clearTimeout(state.timer); clearTimeout(state.expiry); state.controller?.abort(); state.generation++; state.pollId++; }
function clearEditor() {
  for (const control of $('fields').querySelectorAll('input,textarea')) control.value = '';
  $('fields').replaceChildren(); $('event-reference').replaceChildren(); $('edit-form').onsubmit = null;
}
function signedOut(message) {
  clearTimeout(selectionTimer); renderPending = false; selecting = false;
  expandedExecutions.clear();
  stop(); state.session = null; state.config = null; state.pendingData = null; state.events = []; state.cameras = []; state.executions = []; state.completed = [];
  $('workspace').hidden = true; $('login').hidden = false; $('screen').replaceChildren(); $('details').textContent = ''; $('draft-fields').replaceChildren(); clearEditor();
  $('editor').close(); $('inspection').close(); feedback(message);
  $('event-reference').replaceChildren(); state.search = '';
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
  stop(); state.session = session; $('login').hidden = true; $('login').reset(); $('workspace').hidden = false;
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
    state.pendingData = { config, cameras, events, executions, completed, connection: 'Connected · refresh every 3 seconds · latest 100 events within the history byte limit' }; render(true);
  } catch (e) { if (generation === state.generation && pollId === state.pollId && state.session) { state.pendingData = null; state.connection = 'Disconnected or refresh failed. Retrying in 3 seconds. Displayed data may be stale.'; render(true); } }
  finally { clearTimeout(deadline); if (generation === state.generation && pollId === state.pollId && state.session) state.timer = setTimeout(refresh, 3000); }
}
$('login').onsubmit = e => { e.preventDefault(); run(async () => {
  const b = $('login').querySelector('button'); b.disabled = true;
  try {
    const form = new FormData($('login'));
    await api('login', 'POST', { username: form.get('username'), password: form.get('password') });
    // Leave the submitted fields intact until navigation so password managers can detect success.
    window.location.reload();
  }
  finally { b.disabled = false; }
}); };
$('logout').onclick = () => run(async () => {
  stop();
  try { await api('logout', 'POST'); signedOut('Logged out. Polling stopped.'); }
  catch (e) { signedOut('Local screen cleared and polling stopped. Logout could not be confirmed; sign in and retry to revoke the server session.'); }
});
for (const b of document.querySelectorAll('[data-screen]')) b.onclick = () => { state.screen = b.dataset.screen; render(); };
$('cancel').onclick = () => $('editor').close();
$('editor').addEventListener('close', () => { clearEditor(); resumeRendering(); });
$('inspection').addEventListener('close', resumeRendering);
document.addEventListener('focusout', resumeRendering);
$('close-inspection').onclick = () => $('inspection').close();
function statusText(s) { return s ? `${results[s.result] ?? 'Unknown'} · ${s.time} · attempts ${s.attempts} · HTTP ${s.statusCode ?? '—'} · duration ${s.duration}` : 'No execution in this process'; }
const expandedExecutions = new Set();
function executionDetails(parent, ruleId, execution) {
  if (!execution) return;
  const details = node('details', undefined, parent); details.open = expandedExecutions.has(ruleId);
  node('summary', 'Execution details', details);
  details.ontoggle = () => { if (details.isConnected) { if (details.open) expandedExecutions.add(ruleId); else expandedExecutions.delete(ruleId); } };
  node('p', `HTTP status: ${execution.statusCode ?? 'No HTTP response received'}`, details);
  if (execution.transportError) node('p', `Transport error category: ${execution.transportError}`, details);
  node('p', 'Response from the final attempt only. Response content may contain sensitive information; it is kept in memory until replaced or the process restarts.', details);
  if (execution.responseJson != null) {
    let text = execution.responseJson;
    try { text = JSON.stringify(JSON.parse(text), null, 2); } catch { }
    node('pre', text, details);
  } else {
    const explanations = { NotJson: 'Response body is not labelled as JSON (application/json or +json).', TooLarge: 'JSON response exceeds the 16 KiB limit and was not retained.', Empty: 'Response body is empty.', InvalidJson: 'Response body is invalid JSON or exceeds the nesting limit.', Unavailable: 'Response body could not be read within the request limits.' };
    node('p', execution.statusCode == null ? 'No response body is available because no HTTP response was received.' : explanations[execution.responseBodyStatus] ?? 'Response body was not captured for this execution.', details);
  }
}
async function mutate(path, method, body) { await api(path, method, body); feedback('Saved.'); await refresh(); }
function render(preserveSelection = false) {
  if (!state.session) return;
  // Connection and session state must remain live while forms, focus or selections defer redraw.
  const connection = state.pendingData?.connection ?? state.connection;
  if ($('connection').textContent !== connection) $('connection').textContent = connection ?? '';
  if (preserveSelection && (selectionActive() || $('editor').open || $('inspection').open || ($('screen').contains(document.activeElement) && document.activeElement.matches('input,select,textarea')))) { renderPending = true; return; }
  renderPending = false;
  // Keep actions tied to the displayed configuration/revision until the snapshot is rendered.
  if (state.pendingData) { Object.assign(state, state.pendingData); state.pendingData = null; }
  if ($('connection').textContent !== state.connection) $('connection').textContent = state.connection ?? '';
  if (!state.config) return;
  const displayedRevision = state.config.revision;
  for (const b of document.querySelectorAll('[data-screen]')) b.setAttribute('aria-current', b.dataset.screen === state.screen ? 'page' : 'false');
  const scroll = window.scrollY;
  const focus = $('screen').contains(document.activeElement) ? { text: document.activeElement.textContent, key: document.activeElement.closest('article')?.dataset.key } : null;
  const area = $('screen'); area.dataset.revision = String(displayedRevision); area.replaceChildren(); node('h2', state.screen === 'Events' ? 'Event Explorer' : state.screen, area);
  const toolbar = node('div', undefined, area); toolbar.className = 'actions screen-actions';
  button('Refresh now', toolbar, refresh);
  if (state.screen === 'Overview') {
    node('p', 'Live process snapshot · notification and execution history is bounded and clears on restart.', area);
    const metrics = node('div', undefined, area); metrics.className = 'metrics';
    for (const [label, value] of [['Cameras subscribed', `${state.config.cameras.filter(c => c.enabled && state.cameras.some(s => s.cameraId === c.id && s.state === 'Subscribed')).length}/${state.config.cameras.length}`], ['Enabled rules', state.config.rules.filter(r => r.match.enabled).length], ['Retained notifications', state.events.length]]) {
      const card = node('article', undefined, metrics); node('p', label, card); node('strong', value, card);
    }
    button('Add camera', toolbar, () => editCamera()); button('Add rule', toolbar, () => editRule());
    node('h3', 'Recent completed executions', area);
    for (const e of [...state.completed].sort((a,b) => Date.parse(b.time)-Date.parse(a.time)).slice(0,8)) {
      const card = node('article', undefined, area); node('h3', state.config.rules.find(r => r.match.id === e.ruleId)?.name ?? e.ruleId, card); node('p', statusText(e), card); executionDetails(card, e.ruleId, e);
    }
    if (!state.completed.length) node('p', 'No completed executions retained in this process.', area);
    node('h3', 'Recent notifications', area);
    for (const e of [...state.events].reverse().slice(0,5)) eventCard(area, e);
  } else if (state.screen === 'Cameras') {
    button('Add camera', toolbar, () => editCamera());
    if (!state.config.cameras.length) node('p', 'No cameras configured. Add a device-service endpoint to begin discovering events.', area);
    for (const c of state.config.cameras) {
      const a = node('article', undefined, area); a.dataset.key = c.id; node('h3', c.name, a); node('p', c.endpoint, a);
      const s = state.cameras.find(x => x.cameraId === c.id);
      badge(a, c.enabled ? s?.state ?? 'Connecting' : 'Disabled', c.enabled && s?.state === 'Subscribed' ? 'healthy' : c.enabled ? 'attention' : '');
      node('p', `${c.enabled ? s?.state ?? 'Connecting' : 'Disabled'} · last contact ${s?.lastContact ?? 'none'} · last event ${s?.lastEvent ?? 'none'} · failures ${s?.failures ?? 0}`, a);
      const actions = node('div', undefined, a); actions.className = 'actions';
      button('Edit camera', actions, () => editCamera(c, displayedRevision));
      button(c.enabled ? 'Disable camera' : 'Enable camera', actions, () => mutate(`cameras/${encodeURIComponent(c.id)}/enabled`, 'POST', { revision: displayedRevision, enabled: !c.enabled }));
      button('Test connectivity', actions, async () => { const r = await api(`cameras/${encodeURIComponent(c.id)}/test`, 'POST'); feedback(`Connectivity: ${r.result}. Reachable: ${r.reachable}. This does not prove event subscription readiness.`); });
      button('Delete camera', actions, async () => { if (confirm('Delete this camera? Remove its rules first.')) await mutate(`cameras/${encodeURIComponent(c.id)}`, 'DELETE', { revision: displayedRevision }); });
    }
  } else if (state.screen === 'Rules') {
    button('Add rule', toolbar, () => editRule());
    if (!state.config.rules.length) node('p', 'No rules configured. Inspect an observed event to draft an exact disabled rule.', area);
    node('p', 'Manual HTTP tests send real requests to the saved destination, including for disabled rules. A camera must be enabled. Retries may duplicate effects.', area);
    for (const r of state.config.rules) {
      const a = node('article', undefined, area); a.dataset.key = r.match.id; node('h3', r.name, a); node('p', `${r.match.enabled ? 'Enabled' : 'Disabled'} · ${r.method} · ${r.match.topic}`, a);
      badge(a, r.match.enabled ? 'Enabled' : 'Disabled', r.match.enabled ? 'healthy' : '');
      const flow = node('div', undefined, a); flow.className = 'rule-flow';
      node('p', `WHEN ${state.config.cameras.find(c => c.id === r.match.cameraId)?.name ?? r.match.cameraId} · ${r.match.condition ? `${r.match.condition.section === 0 ? 'Source' : 'Data'}: ${r.match.condition.name} = ${r.match.condition.expectedValue}` : 'Topic only'} · ${r.match.mode === 1 ? 'Transition' : 'Occurrence'}`, flow);
      node('p', `→ THEN ${r.method} ${r.urlSensitive !== false ? '🔒 Sensitive destination' : r.publicUrl ?? 'Destination unavailable'}`, flow);
      if (r.ignoreCertificateValidation) node('p', 'Warning: webhook certificate validation is disabled.', a);
      node('p', 'Current: ' + statusText(state.executions.find(x => x.ruleId === r.match.id)), a);
      node('p', 'Last completed: ' + statusText(state.completed.find(x => x.ruleId === r.match.id)), a);
      executionDetails(a, r.match.id, state.completed.find(x => x.ruleId === r.match.id));
      const actions = node('div', undefined, a); actions.className = 'actions';
      button('Edit rule', actions, () => editRule(r, undefined, undefined, displayedRevision));
      button(r.match.enabled ? 'Disable rule' : 'Enable rule', actions, () => mutate(`rules/${encodeURIComponent(r.match.id)}/enabled`, 'POST', { revision: displayedRevision, enabled: !r.match.enabled }));
      button('Send manual HTTP test…', actions, async () => { if (confirm('Send the saved HTTP action now? This bypasses matching/cooldown and can cause an external action. Retries can duplicate effects.')) { await api(`rules/${encodeURIComponent(r.match.id)}/test`, 'POST'); feedback('Manual test queued. Inspect execution status; queue acceptance does not prove delivery or destination effects.'); await refresh(); } });
      button('Delete rule', actions, async () => { if (confirm('Delete this rule?')) await mutate(`rules/${encodeURIComponent(r.match.id)}`, 'DELETE', { revision: displayedRevision }); });
    }
  } else {
    node('p', 'All retained notifications, including false, unmatched, Initialized and Deleted. History is transient and bounded.', area);
    const search = input(area, 'Search retained notifications', state.search, 'search', false, 'Search only currently retained notifications by camera, topic, fields, values or operation. No notifications are removed from history. Clear to view all.');
    const listing = node('div', undefined, area);
    const update = () => {
      state.search = search.value; listing.replaceChildren();
      const needle = state.search.toLocaleLowerCase();
      const matches = [...state.events].reverse().filter(e => [e.cameraName, e.cameraId, e.topic, e.propertyOperation,
        ...[...(e.source ?? []), ...(e.data ?? []), ...(e.key ?? [])].flatMap(f => [f.kind, f.name, f.value ?? f.xml])]
        .join(' ').toLocaleLowerCase().includes(needle));
      node('p', `${matches.length} of ${state.events.length} retained notifications`, listing);
      for (const e of matches) eventCard(listing, e);
    };
    search.oninput = update; update();
  }
  if (preserveSelection) {
    if (focus) [...area.querySelectorAll('button,summary')].find(b => b.textContent === focus.text && b.closest('article')?.dataset.key === focus.key)?.focus({ preventScroll: true });
    window.scrollTo(0, scroll);
  }
}
function eventCard(area, e) {
      const a = node('article', undefined, area); node('h3', e.cameraName, a); node('p', `${e.receivedAt} · ${e.propertyOperation ?? 'No operation'}${e.truncated ? ' · TRUNCATED' : ''}`, a); node('p', e.topic, a);
      node('p', [...(e.source ?? []), ...(e.data ?? [])].filter(f => f.kind === 'SimpleItem').map(f => `${f.name} = ${f.value}`).join(' · '), a);
      button('Inspect notification / create rule', a, () => inspect(e));
}
const fieldDocumentation = {
  'Name': 'A friendly name used to identify this camera in the camera list and event inspector.',
  'Device-service URL': 'The full HTTP or HTTPS ONVIF device-service endpoint supplied by your camera. Event-service addresses are discovered from this endpoint.',
  'Enabled': 'For a camera, starts or stops its event subscription. For a rule, allows or prevents automatic HTTP actions. New rules must be saved disabled, then enabled after review.',
  'Camera username': 'The username for ONVIF authentication. Stored values are masked. Clear both username and password to use unauthenticated access.',
  'Camera password': 'The password for ONVIF authentication. Stored values are masked. Clear both username and password to use unauthenticated access.',
  'Rule name': 'A friendly name used to identify this rule and its execution results.',
  'Camera': 'The camera whose notifications this rule evaluates. A disabled camera cannot dispatch actions.',
  'Exact matching topic': 'Copy the exact matching topic from the event inspector. Matching is case-sensitive, including namespace prefixes. Wildcards are not supported.',
  'Field condition': 'Topic only matches without checking a field. Source or Data checks a scalar field in that section of the notification.',
  'Field name': 'The exact, case-sensitive field name in the selected Source or Data section. Missing fields do not match; duplicate names match if any selected value matches.',
  'Expected value (exact text)': 'The value to match in the selected field. Text comparison preserves case and whitespace. Boolean comparison accepts true/1 and false/0.',
  'Comparison': 'Text uses case-sensitive exact equality. Boolean treats true and 1 as equivalent, and false and 0 as equivalent; other values do not match as booleans.',
  'Mode': 'Occurrence evaluates every matching notification. Transition requires a field condition and fires on a known nonmatching-to-matching change. The first observation and Initialized messages establish a baseline; Deleted clears it.',
  'Cooldown seconds': 'Minimum time between accepted automatic actions for this rule, across all event identities. Zero allows every eligible match. Failed delivery still consumes cooldown; suppressed events are not replayed.',
  'HTTP method': 'GET sends a request without a JSON body. POST can send the configured literal JSON body. Either method can cause a real external action.',
  'Additional attempts (may duplicate effects)': 'Zero disables retries. One or two allows retries for transient failures within the job deadline. A retry can repeat an action already performed by the destination.',
  'Action URL': 'Full HTTP or HTTPS destination, for example https://destination.example/action. Sensitive URLs remain write-only after saving. Visible URLs are returned only to authenticated administrators. Never put credentials in a visible URL. Embedded username/password credentials are rejected.',
  'HTTP headers': 'Optional custom request headers, stored write-only. Edit replaces the whole collection using name/value rows, for example X-Example and synthetic-value. Re-enter all required headers. Transport-controlled and duplicate header names are rejected.',
  'JSON body': 'Optional literal JSON sent with POST, stored masked. Enter valid JSON, without templates or expressions. Clear removes the stored body.'
};
let fieldSequence = 0;
function fieldLabel(parent, label, documentation) {
  const row = node('div', undefined, parent); row.className = 'field-row';
  const l = node('label', label, row); l.htmlFor = `field-${++fieldSequence}`;
  const help = node('span', undefined, row); help.className = 'field-help';
  const icon = node('button', 'i', help); icon.type = 'button'; icon.className = 'info-icon'; icon.setAttribute('aria-label', `Help for ${label}`);
  const tooltip = node('span', documentation ?? fieldDocumentation[label], help); tooltip.className = 'field-tooltip'; tooltip.id = `${l.htmlFor}-help`; tooltip.setAttribute('role', 'tooltip');
  icon.setAttribute('aria-describedby', tooltip.id);
  icon.setAttribute('aria-expanded', 'false'); icon.setAttribute('aria-controls', tooltip.id);
  const position = () => {
    const anchor = icon.getBoundingClientRect(), bounds = tooltip.getBoundingClientRect();
    tooltip.style.left = `${Math.max(8, Math.min(anchor.right - bounds.width, innerWidth - bounds.width - 8))}px`;
    tooltip.style.top = `${anchor.bottom + bounds.height + 8 < innerHeight ? anchor.bottom + 4 : Math.max(8, anchor.top - bounds.height - 4)}px`;
  };
  help.onmouseenter = position; icon.onfocus = position;
  icon.onclick = () => { const open = help.classList.toggle('pinned'); help.classList.remove('dismissed'); icon.setAttribute('aria-expanded', String(open)); position(); };
  icon.onkeydown = e => { if (e.key === 'Escape') { help.classList.remove('pinned'); help.classList.add('dismissed'); icon.setAttribute('aria-expanded', 'false'); e.preventDefault(); e.stopPropagation(); } };
  icon.onblur = () => help.classList.remove('dismissed'); help.onmouseleave = () => help.classList.remove('dismissed');
  return { row, l, tooltip };
}
function input(parent, label, value = '', type = 'text', required = false, documentation) {
  const { row, l, tooltip } = fieldLabel(parent, label, documentation); const i = node(type === 'textarea' ? 'textarea' : 'input', undefined, row);
  i.id = l.htmlFor; i.setAttribute('aria-describedby', tooltip.id);
  if (type !== 'textarea') i.type = type;
  if (type === 'checkbox') i.checked = Boolean(value); else i.value = value;
  i.required = required; i.autocomplete = 'off'; return i;
}
function select(parent, label, choices, value, documentation) {
  const { row, l, tooltip } = fieldLabel(parent, label, documentation); const s = node('select', undefined, row);
  s.id = l.htmlFor; s.setAttribute('aria-describedby', tooltip.id);
  for (const [v, text] of choices) { const o = node('option', text, s); o.value = v; } s.value = String(value); return s;
}
function secret(parent, label, present, kind = 'password', required = false, credential, initial = '') {
  const box = node('fieldset', undefined, parent); box.className = 'secret-group'; node('legend', label + ' · ' + (present ? 'stored' : 'not stored'), box);
  const status = node('p', present ? '•••••••• · Stored; unchanged until you edit.' : 'No stored value.', box); status.className = 'secret-status';
  const actions = node('div', undefined, box); actions.className = 'actions';
  let operation = present ? 0 : (required ? 1 : 0);
  const values = node('div', undefined, box);
  const rows = [];
  let value;
  if (label === 'HTTP headers') {
    node('p', 'Editing replaces the entire stored collection. Re-enter every header you need.', values);
    const listing = node('div', undefined, values);
    const add = () => {
      if (rows.length >= 32) return;
      const row = node('div', undefined, listing); row.className = 'header-row';
      const name = input(row, 'Header name', '', 'text', true, 'Exact HTTP header name, for example X-Example. Transport-controlled headers and duplicate names are rejected. Maximum 32 headers.');
      const content = input(row, 'Header value', '', 'password', false, 'Replacement value for this header; empty values are allowed. Stored header values are never read back. All rows replace the entire collection on Save.');
      content.dataset.allowEmpty = 'true';
      const item = { row, name, content }; rows.push(item);
      button('Remove header', row, () => { rows.splice(rows.indexOf(item), 1); row.remove(); });
    };
    button('Add header', values, add); add();
  } else {
    value = input(values, 'Replacement value', initial, kind, false, fieldDocumentation[label]);
    if (credential) {
      value.labels[0].htmlFor = credential.name; value.id = credential.name; value.name = credential.name;
      value.autocomplete = credential.autocomplete; value.autocapitalize = 'none'; value.spellcheck = false;
    }
  }
  const update = () => {
    values.hidden = operation !== 1;
    for (const control of values.querySelectorAll('input,textarea')) { control.disabled = operation !== 1; control.required = operation === 1 && control.dataset.allowEmpty !== 'true'; }
    status.textContent = operation === 2 ? 'Will clear on Save. Undo is available.' : operation === 1 ? 'Replacement will be stored on Save.' : present ? '•••••••• · Stored; unchanged.' : 'No stored value.';
    box.secretChanged?.(operation);
  };
  button('Edit', actions, () => { operation = 1; update(); values.querySelector('input,textarea')?.focus(); });
  button('Undo', actions, () => { operation = present ? 0 : (required ? 1 : 0); if (value) value.value = initial; for (const row of rows) { row.name.value = ''; row.content.value = ''; } update(); });
  if (!required) button('Clear', actions, () => { if (confirm('Clear ' + label + ' on Save? You can undo before saving.')) { operation = 2; if (value) value.value = ''; for (const row of rows) { row.name.value = ''; row.content.value = ''; } update(); } });
  update();
  const read = () => {
    const edit = { operation };
    if (operation === 1) edit.value = label === 'HTTP headers' ? rows.map(r => ({ name: r.name.value, value: r.content.value })) : value.value;
    if (label === 'JSON body' && operation === 1) JSON.parse(edit.value);
    return edit;
  };
  read.box = box; read.input = value; read.controls = actions; read.replace = () => { operation = 1; update(); }; return read;
}
function editor(title, help, build) {
  $('fields').replaceChildren(); $('event-reference').replaceChildren(); $('event-reference').hidden = true; $('editor').classList.remove('rule-editor'); $('edit-title').textContent = title; $('edit-help').textContent = help; $('edit-error').textContent = '';
  const generation = state.generation; const submit = build($('fields')); $('editor').showModal();
  $('edit-form').onsubmit = async e => {
    e.preventDefault(); $('save').disabled = true; $('edit-error').textContent = '';
    try { await submit(); if (generation === state.generation && state.session) { $('editor').close(); feedback('Saved.'); await refresh(); } }
    catch (error) { if (state.session) $('edit-error').textContent = error instanceof SyntaxError ? 'The POST body must be valid literal JSON.' : error.message; }
    finally { $('save').disabled = false; }
  };
}
function editCamera(c, revision = state.config.revision) {
  const id = c?.id ?? crypto.randomUUID();
  editor(c ? 'Edit camera' : 'Add camera', 'Credentials are never read back. Use Edit to replace or Clear to remove; otherwise they remain unchanged. Clear both username and password for unauthenticated access.', p => {
    const name = input(p, 'Name', c?.name ?? '', 'text', true), endpoint = input(p, 'Device-service URL', c?.endpoint ?? '', 'url', true), enabled = input(p, 'Enabled', c?.enabled ?? false, 'checkbox');
    const section = `section-camera-${encodeURIComponent(id)}`;
    const username = secret(p, 'Camera username', c?.hasUsername, 'text', false, { name: 'camera-username', autocomplete: `${section} username` });
    const password = secret(p, 'Camera password', c?.hasPassword, 'password', false, { name: 'camera-password', autocomplete: `${section} current-password` });
    return () => api(`cameras/${encodeURIComponent(id)}`, 'PUT', { revision, name: name.value, endpoint: endpoint.value, enabled: enabled.checked, username: username(), password: password() });
  });
}
function seconds(text) { const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(text ?? '00:00:00'); return m ? Number(m[1] ?? 0)*86400 + Number(m[2])*3600 + Number(m[3])*60 + Number(m[4]) : 0; }
function eventReference(event, fill) {
  const host = $('event-reference'); host.hidden = false;
  const toggle = button('Hide event', host, () => {
    content.hidden = !content.hidden; toggle.textContent = content.hidden ? 'View event' : 'Hide event'; toggle.setAttribute('aria-expanded', String(!content.hidden));
  });
  toggle.setAttribute('aria-expanded', 'true');
  const content = node('div', undefined, host); node('h3', 'Observed event reference', content);
  if (matchMedia('(max-width: 760px)').matches) { content.hidden = true; toggle.textContent = 'View event'; toggle.setAttribute('aria-expanded', 'false'); }
  if (!event) { node('p', 'No retained event available. Event history is transient.', content); return; }
  node('p', `${event.cameraName} · ${event.receivedAt} · ${event.topic}`, content);
  node('p', 'Scalar Source/Data selections fill the condition. Duplicate names match if any selected value equals the expected value. Comparison choice is preserved.', content);
  if (event.truncated) node('p', 'TRUNCATED: inspection only; cannot establish an exact draft.', content);
  const tabs = node('div', undefined, content); tabs.className = 'actions';
  const fields = node('div', undefined, content), raw = node('div', undefined, content); raw.hidden = true;
  const fieldsButton = button('Fields', tabs, () => { fields.hidden = false; raw.hidden = true; fieldsButton.setAttribute('aria-pressed', 'true'); rawButton.setAttribute('aria-pressed', 'false'); });
  const rawButton = button('Raw JSON', tabs, () => { fields.hidden = true; raw.hidden = false; fieldsButton.setAttribute('aria-pressed', 'false'); rawButton.setAttribute('aria-pressed', 'true'); });
  fieldsButton.setAttribute('aria-pressed', 'true'); rawButton.setAttribute('aria-pressed', 'false');
  const json = JSON.stringify(event, null, 2);
  button('Copy retained JSON', raw, async () => { await navigator.clipboard.writeText(json); feedback('Retained event JSON copied. It may contain device information.'); });
  node('pre', json, raw);
  for (const [index, section] of [[0, 'source'], [1, 'data'], [2, 'key']]) {
    node('h4', section.toUpperCase(), fields);
    if (!(event[section]?.length)) node('p', 'None retained', fields);
    for (const [i, field] of (event[section] ?? []).entries()) {
      const box = node('article', undefined, fields);
      node('p', `#${i + 1} · ${field.kind} · ${field.name ?? 'Unnamed'}`, box);
      node('pre', field.value ?? JSON.stringify(field, null, 2), box);
      if (!event.truncated && index < 2 && field.kind === 'SimpleItem' && field.name != null && field.value != null) {
        button(`Use ${section} #${i + 1}: ${field.name} = ${field.value}`, box, () => fill({ section: index, name: field.name, expectedValue: field.value }));
      } else node('p', 'Inspection only: structured, Key, incomplete or unsupported field.', box);
    }
  }
}
function editRule(r, draft, observed, revision = state.config.revision) {
  if (!state.config.cameras.length) { feedback('Add a camera before creating a rule.'); return; }
  const id = r?.match.id ?? crypto.randomUUID(), m = r?.match ?? draft ?? {};
  editor(r ? 'Edit rule' : 'Review new disabled rule', 'Review the exact matching topic and Source/Data selector. New rules save disabled. Transition needs a known nonmatching-to-matching change; the first observation establishes a baseline. Retries can duplicate effects. Masked action values cannot be read back.', p => {
    $('editor').classList.add('rule-editor');
    const group = title => { const box = node('fieldset', undefined, p); node('legend', title, box); return box; };
    const general = group('General'), when = group('WHEN — ONVIF event'), then = group('THEN — HTTP webhook');
    const advanced = node('details', undefined, p); advanced.open = Boolean(m.condition || m.mode || seconds(m.cooldown) || r?.additionalAttempts || r?.hasHeaders || r?.hasJsonBody); node('summary', 'Advanced matching & delivery', advanced);
    const name = input(general, 'Rule name', r?.name ?? '', 'text', true);
    const camera = select(general, 'Camera', state.config.cameras.map(c => [c.id,c.name]), m.cameraId ?? state.config.cameras[0].id);
    const topic = input(when, 'Exact matching topic', m.topic ?? '', 'text', true);
    const section = select(when, 'Field condition', [['','Topic only'],['0','Source'],['1','Data']], m.condition?.section ?? '');
    const field = input(when, 'Field name', m.condition?.name ?? ''), expected = input(when, 'Expected value (exact text)', m.condition?.expectedValue ?? '');
    const comparison = select(advanced, 'Comparison', [[0,'Text: ordinal exact'],[1,'Boolean: true/1 and false/0']], m.condition?.comparison ?? 0);
    const mode = select(advanced, 'Mode', [[0,'Occurrence'],[1,'Transition']], m.mode ?? 0);
    const cooldown = input(advanced, 'Cooldown seconds', seconds(m.cooldown), 'number'); cooldown.min = '0'; cooldown.step = 'any';
    const enabled = input(general, 'Enabled', r ? m.enabled : false, 'checkbox'); if (!r) enabled.disabled = true;
    const method = select(then, 'HTTP method', [['GET','GET'],['POST','POST']], r?.method ?? 'GET');
    const attempts = select(advanced, 'Additional attempts (may duplicate effects)', [[0,'0 — no retries'],[1,'1 retry'],[2,'2 retries']], r?.additionalAttempts ?? 0);
    const ignoreCertificateValidation = input(then, 'Ignore certificate validation', r?.ignoreCertificateValidation ?? false, 'checkbox', false, 'Accepts untrusted, expired or mismatched HTTPS certificates for this webhook only. This can expose tokens and request contents to an impersonated destination. Leave unchecked when possible.');
    const url = secret(then, 'Action URL', r?.hasUrl, 'text', true, undefined, r?.publicUrl ?? '');
    if (r?.publicUrl != null) url.replace();
    const sensitive = { checked: r ? r.urlSensitive !== false : false };
    const visibility = node('p', undefined, url.box); visibility.className = 'secret-status';
    const lock = button('', url.input.parentElement, () => { sensitive.checked = !sensitive.checked; updateLock(); });
    lock.className = 'url-lock'; lock.setAttribute('aria-label', 'Sensitive URL'); lock.setAttribute('aria-describedby', url.input.getAttribute('aria-describedby'));
    const updateLock = () => { lock.textContent = sensitive.checked ? '🔒' : '🔓'; lock.setAttribute('aria-pressed', String(sensitive.checked)); visibility.textContent = sensitive.checked ? 'Sensitive: future reads show presence only. Making a stored sensitive URL visible requires re-entry.' : 'Visible to authenticated administrators after Save. Avoid tokens in visible URLs.'; }; updateLock();
    url.input.parentElement.classList.add('destination-row');
    url.box.secretChanged = operation => { (operation === 1 ? url.input.parentElement : url.controls).append(lock); };
    url.box.secretChanged(url().operation);
    url.input.className = 'maskable-value';
    const eye = button('Hide entered URL', url.input.parentElement, () => { url.input.type = url.input.type === 'password' ? 'text' : 'password'; eye.textContent = url.input.type === 'password' ? 'Show entered URL' : 'Hide entered URL'; }); eye.className = 'mask-toggle url-eye';
    const warning = node('p', 'Warning: unverified HTTPS permits interception and destination impersonation for this rule. Redirects and proxies remain disabled. Camera TLS stays strict.', then); warning.className = 'warning';
    const tlsUpdate = () => { warning.hidden = !ignoreCertificateValidation.checked; }; ignoreCertificateValidation.onchange = tlsUpdate; tlsUpdate();
    const headers = secret(advanced, 'HTTP headers', r?.hasHeaders, 'textarea'), jsonBody = secret(advanced, 'JSON body', r?.hasJsonBody, 'textarea');
    const conditionUpdate = () => { for (const control of [field, expected, comparison]) { control.parentElement.hidden = section.value === ''; control.disabled = section.value === ''; } }; section.onchange = conditionUpdate; conditionUpdate();
    eventReference(observed ?? state.events.slice().reverse().find(e => e.cameraId === m.cameraId && e.topic === m.topic), condition => {
      if ((section.value !== '' || field.value || expected.value) && !confirm('Replace the current field condition with this observed value?')) return;
      section.value = String(condition.section); field.value = condition.name; expected.value = condition.expectedValue; conditionUpdate(); feedback('Observed scalar copied into the condition. Duplicate field names match if any value matches.');
    });
    return () => {
      if (section.value !== '' && !field.value) throw new Error('A field condition requires a field name.');
      if (mode.value === '1' && section.value === '') throw new Error('Transition mode requires a field condition.');
      if (r?.urlSensitive !== false && r?.hasUrl && !sensitive.checked && url().operation !== 1) throw new Error('Re-enter the URL using Edit before making a stored sensitive destination public.');
      return api(`rules/${encodeURIComponent(id)}`, 'PUT', { revision, name: name.value, cameraId: camera.value, topic: topic.value, condition: section.value === '' ? null : { section: Number(section.value), name: field.value, expectedValue: expected.value, comparison: Number(comparison.value) }, mode: Number(mode.value), cooldownSeconds: Number(cooldown.value), enabled: r ? enabled.checked : false, method: method.value, additionalAttempts: Number(attempts.value), ignoreCertificateValidation: ignoreCertificateValidation.checked, urlSensitive: sensitive.checked, url: url(), headers: headers(), jsonBody: jsonBody() });
    };
  });
}
function inspect(e) {
  $('details').textContent = JSON.stringify(e, null, 2); const fields = $('draft-fields'); fields.replaceChildren();
  const draft = condition => { $('inspection').close(); editRule(null, { cameraId: e.cameraId, topic: e.topic, condition }, e); };
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
