'use strict';

/* ======================================================================
   Bridge: WebView2 messages in the desktop app, HTTP in a browser.
   ====================================================================== */

const host = window.chrome && window.chrome.webview;
const pending = new Map();
let seq = 0;
if (host) {
  host.addEventListener('message', e => {
    const m = e.data;
    const p = pending.get(m.id);
    if (!p) return;
    pending.delete(m.id);
    m.error ? p.reject(new Error(m.error)) : p.resolve(m.result);
  });
}

function call(method, params = {}) {
  if (host) {
    return new Promise((resolve, reject) => {
      const id = ++seq;
      pending.set(id, { resolve, reject });
      host.postMessage({ id, method, params });
    });
  }
  return fetch('api/rpc', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-DbEmulator-Action': '1' },
    body: JSON.stringify({ method, params }),
  }).then(async r => {
    let body = {};
    try { body = await r.json(); } catch { /* empty */ }
    if (!r.ok || body.error) throw new Error(body.error || r.statusText);
    return body.result;
  });
}

/* ======================================================================
   Helpers
   ====================================================================== */

const $ = sel => document.querySelector(sel);
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const num = n => Number(n || 0).toLocaleString();
const plural = (n, one, many) => `${num(n)} ${n === 1 ? one : (many || one + 's')}`;

function ago(iso) {
  if (!iso) return 'never';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 60) return 'just now';
  if (s < 3600) return plural(Math.round(s / 60), 'minute') + ' ago';
  if (s < 86400 * 1.5) return plural(Math.round(s / 3600), 'hour') + ' ago';
  return plural(Math.round(s / 86400), 'day') + ' ago';
}
function hoursSince(iso) { return iso ? (Date.now() - new Date(iso).getTime()) / 3600000 : Infinity; }
function dateTime(iso) {
  return new Date(iso).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}
function bytes(b) {
  if (b >= 1e9) return (b / 1e9).toFixed(2) + ' GB';
  if (b >= 1e6) return (b / 1e6).toFixed(1) + ' MB';
  return Math.max(1, Math.round(b / 1e3)) + ' KB';
}
function freshness(iso) {
  const h = hoursSince(iso);
  return h < 24 ? 'good' : h < 168 ? 'warn' : 'bad';
}

const ICONS = {
  home: '<path d="M3 11l9-8 9 8v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/>',
  data: '<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>',
  backups: '<rect x="3" y="4" width="18" height="5" rx="1"/><path d="M5 9v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9"/><path d="M10 13h4"/>',
  activity: '<path d="M3 12h4l3-8 4 16 3-8h4"/>',
  settings: '<path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>',
  fetch: '<path d="M12 3v12"/><path d="M7 10l5 5 5-5"/><path d="M4 21h16"/>',
  copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
  shield: '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/><path d="M9 12l2 2 4-4"/>',
  alert: '<path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18h.01"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7h.01"/>',
  play: '<path d="M7 4l13 8-13 8z"/>',
  plug: '<path d="M9 7V3M15 7V3M6 7h12v4a6 6 0 0 1-12 0zM12 17v4"/>',
  undo: '<path d="M3 7v6h6"/><path d="M21 17a9 9 0 0 0-15-6.7L3 13"/>',
  chev: '<path d="M9 6l6 6-6 6"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M17 6l3 3"/>',
  check: '<path d="M5 12l5 5 9-10"/>',
  x: '<path d="M6 6l12 12M18 6L6 18"/>',
  eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
  restore: '<path d="M4 12a8 8 0 1 0 2.3-5.6"/><path d="M4 4v4h4"/>',
};
const icon = name => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${ICONS[name] || ''}</svg>`;

function toast(message, kind = '') {
  const t = document.createElement('div');
  t.className = 'toast ' + kind;
  t.textContent = message;
  $('#toasts').appendChild(t);
  setTimeout(() => t.remove(), kind === 'bad' ? 6000 : 2600);
}

async function copy(text, what = 'Copied') {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const ta = document.createElement('textarea');
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand('copy');
    ta.remove();
  }
  toast(what + ' to the clipboard');
}

async function act(fn, success) {
  try {
    const r = await fn();
    if (success) toast(success);
    await refresh();
    return r;
  } catch (e) {
    toast(e.message, 'bad');
    return undefined;
  }
}

function confirmDialog({ title, body, ok = 'OK', danger = false }) {
  return new Promise(resolve => {
    const modal = $('#modal');
    modal.innerHTML = `<div class="overlay"><div class="dialog" role="dialog">
      <h2>${esc(title)}</h2><p>${body}</p>
      <div class="row"><button class="btn" data-r="0">Cancel</button>
      <button class="btn ${danger ? 'danger' : 'primary'}" data-r="1">${esc(ok)}</button></div></div></div>`;
    modal.querySelectorAll('[data-r]').forEach(b => b.onclick = () => { modal.innerHTML = ''; resolve(b.dataset.r === '1'); });
    modal.querySelector('[data-r="1"]').focus();
  });
}

function highlight(value) {
  const json = esc(JSON.stringify(value, null, 2));
  return json.replace(/(&quot;(?:\\.|[^&\\]|&(?!quot;))*?&quot;)(\s*:)?|\b(true|false|null)\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g,
    (m, str, colon, lit) => {
      if (str) return colon ? `<span class="j-key">${str}</span>${colon}` : `<span class="j-str">${str}</span>`;
      if (lit) return `<span class="j-lit">${m}</span>`;
      return `<span class="j-num">${m}</span>`;
    });
}

function csharpSnippet(cs) {
  return `var client = new CosmosClient(\n    "${cs}",\n    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });`;
}

/* ======================================================================
   App state & shell
   ====================================================================== */

let S = null;
let currentPage = 'overview';
try { currentPage = localStorage.getItem('page') || 'overview'; } catch { /* storage unavailable */ }
const PAGES = [
  ['overview', 'Overview', 'home'],
  ['data', 'Data', 'data'],
  ['backups', 'Backups', 'backups'],
  ['activity', 'Activity', 'activity'],
  ['settings', 'Settings', 'settings'],
];

function renderShell() {
  $('#root').innerHTML = `<div class="app">
    <aside class="sidebar">
      <div class="brand"><img src="app.ico" alt=""><div><b>DbEmulator</b><span>Cosmos DB, locally</span></div></div>
      <nav class="nav">${PAGES.map(([id, label, ic]) =>
        `<button data-page="${id}" class="${id === currentPage ? 'active' : ''}">${icon(ic)}<span>${label}</span><span class="count" id="count-${id}"></span></button>`).join('')}
      </nav>
      <div class="sidebar-foot"><div class="server-status" id="server-status"></div></div>
    </aside>
    <main class="main"><div class="page" id="page"></div></main>
  </div>`;
  document.querySelectorAll('[data-page]').forEach(b => b.onclick = () => showPage(b.dataset.page));
}

function showPage(id) {
  currentPage = id;
  try { localStorage.setItem('page', id); } catch { /* ignore */ }
  document.querySelectorAll('[data-page]').forEach(b => b.classList.toggle('active', b.dataset.page === id));
  lastRendered = '';
  pages[id].mount();
  $('.main').scrollTop = 0;
}

function updateShell() {
  const s = S.server;
  $('#server-status').innerHTML = s.running
    ? `<span class="dot good"></span><div><b>Running</b><br>localhost:${s.port}</div>`
    : `<span class="dot bad"></span><div><b>Not running</b><br>${S.phase === 'starting' ? 'starting…' : 'see Overview'}</div>`;
  const docs = S.databases.reduce((n, d) => n + d.containers.reduce((m, c) => m + c.documents, 0), 0);
  $('#count-data').textContent = docs ? num(docs) : '';
}

let lastRendered = '';
async function refresh() {
  try {
    S = await call('getState');
  } catch {
    return;
  }
  if (!$('.app')) {
    renderShell();
    pages[currentPage].mount();
    maybeStartWizard();
  }
  updateShell();
  pages[currentPage].update?.();
  if (wizard) wizardTick();
}

async function poll() {
  await refresh();
  setTimeout(poll, S && S.fetch.running ? 400 : 1500);
}

/* ======================================================================
   Overview
   ====================================================================== */

const overview = {
  mount() { lastRendered = ''; this.update(); },
  update() {
    const key = JSON.stringify({ ...S, server: { ...S.server, requests: 0 } }) + Math.floor(Date.now() / 30000);
    if (key === lastRendered) return;
    lastRendered = key;
    $('#page').innerHTML = this.html();
  },
  html() {
    const last = S.lastFetch;
    const notices = [];
    if (S.phase === 'starting') notices.push(notice('info', 'info', 'Loading your local copy…', 'This takes a moment for large copies.'));
    if (S.startupError) notices.push(notice('bad', 'alert', 'DbEmulator could not start', esc(S.startupError)));
    if (!S.server.running && S.server.error) {
      notices.push(notice('bad', 'alert', 'Your app cannot connect yet', esc(S.server.error),
        `<button class="btn small" data-act="goto-settings">Change port</button> <button class="btn small" data-act="restart-server">Try again</button>`));
    }
    if (!S.source.configured) {
      notices.push(notice('info', 'plug', 'Connect your Cosmos DB account', 'Enter a read-only connection string to make your first copy.',
        `<button class="btn small primary" data-act="wizard">Set up</button>`));
    }
    const a = S.lastAttempt;
    if (a && !a.succeeded && a.completedUtc && (!last || new Date(a.completedUtc) > new Date(last.completedUtc))) {
      notices.push(notice('bad', 'alert', `The last fetch failed (${ago(a.completedUtc)})`, esc(a.error) + ' You are still working with the previous copy.'));
    }
    if (S.newer) {
      notices.push(notice('info', 'info', `A newer copy is available (fetched ${ago(S.newer.completedUtc)})`,
        'It was fetched without switching over, so your current data and local changes are untouched.',
        `<button class="btn small primary" data-act="serve" data-name="${esc(S.newer.name)}">Serve it</button>`));
    }
    if (S.server.running && !S.server.certificateTrusted) {
      notices.push(notice('warn', 'alert', 'The HTTPS certificate is not trusted by Windows',
        'The connection string below includes <code>DisableServerCertificateValidation=True</code> so the SDK accepts it. Run <code>dotnet dev-certs https --trust</code> to avoid this.'));
    }

    const fetching = S.fetch.running;
    const p = S.fetch.progress;
    const pct = p && p.containerExpected ? Math.min(100, 100 * p.containerDocuments / p.containerExpected) : null;
    const servingOlder = S.active && last && S.active.name !== last.name;
    const totalDocs = S.databases.reduce((n, d) => n + d.containers.reduce((m, c) => m + c.documents, 0), 0);
    const totalContainers = S.databases.reduce((n, d) => n + d.containers.length, 0);

    return `
      <div class="page-head"><div><h1>Overview</h1><p>Your local, read-only copy of Azure Cosmos DB.</p></div></div>
      ${notices.join('')}
      <section class="card">
        <div class="hero">
          <div>
            <div class="card-label">${icon('fetch')} Last fetched from Cosmos DB</div>
            <div class="when"><span class="dot ${last ? freshness(last.completedUtc) : 'bad'}"></span>${last ? esc(ago(last.completedUtc)) : 'Never'}</div>
            <div class="detail">${last
              ? `${esc(dateTime(last.completedUtc))} · ${plural(last.documents, 'document')} in ${plural(last.containers, 'container')}${last.source ? ' · from ' + esc(last.source) : ''}`
              : 'Your Cosmos DB data has not been copied yet.'}</div>
            ${servingOlder ? `<div class="detail">You are serving an older backup from ${esc(dateTime(S.active.completedUtc))}.</div>` : ''}
          </div>
          <div class="actions">
            ${fetching
              ? `<button class="btn big" data-act="cancel-fetch">${icon('x')} Cancel</button>`
              : `<button class="btn big primary" data-act="fetch" ${S.source.configured && S.phase === 'ready' ? '' : 'disabled'}>${icon('fetch')} Fetch fresh copy</button>`}
            <span class="small muted">${S.source.configured ? `Reads from ${esc(S.source.host)} — never writes` : 'No source account yet'}</span>
          </div>
        </div>
        ${fetching ? `<div class="fetching">
          <div class="row"><b>${p ? `Copying ${esc(p.database)} / ${esc(p.container)}` : 'Connecting…'}</b><span class="spacer"></span>
            <span class="muted small">${p ? `${num(p.totalDocuments)} documents · ${num(p.requestCharge)} RU · ${Math.round(p.elapsedSeconds)} s` : ''}</span></div>
          <div class="progress ${pct === null ? 'indeterminate' : ''}"><div style="width:${pct ?? 0}%"></div></div>
          ${p && p.containerExpected ? `<div class="small muted" style="margin-top:6px">${num(p.containerDocuments)} of ${num(p.containerExpected)} documents in this container</div>` : ''}
        </div>` : ''}
      </section>

      <div class="grid grid-3" style="margin-top:16px">
        <section class="card">
          <div class="card-label">${icon('plug')} Connect your app</div>
          <div class="row" style="margin-bottom:10px"><code>${esc(S.server.endpoint)}</code>
            ${S.server.running ? '<span class="pill good">Running</span>' : '<span class="pill bad">Not running</span>'}</div>
          <div class="copybox"><code title="${esc(S.server.connectionString)}">${esc(S.server.connectionString)}</code>
            <button class="btn small" data-act="copy-cs" title="Copy connection string">${icon('copy')}</button></div>
          <p class="small muted" style="margin:10px 0 6px">Use it in your app with <b>ConnectionMode.Gateway</b>.</p>
          <a data-act="copy-snippet">${icon('copy').replace('<svg', '<svg style="width:14px;height:14px;vertical-align:-2px"')} Copy C# setup code</a>
        </section>
        <section class="card">
          <div class="card-label">${icon('undo')} Local changes</div>
          <div class="big-number">${num(S.localChanges)}</div>
          <p class="small muted" style="margin:6px 0 12px">Writes your app made to this copy. They stay on this computer and are never sent to Azure.</p>
          <button class="btn small" data-act="reset" ${S.localChanges ? '' : 'disabled'}>${icon('undo')} Discard local changes</button>
        </section>
        <section class="card">
          <div class="card-label">${icon('data')} This copy</div>
          <div class="big-number">${num(totalDocs)}</div>
          <p class="small muted" style="margin:6px 0 12px">${plural(totalDocs, 'document')} in ${plural(totalContainers, 'container')} across ${plural(S.databases.length, 'database')}.</p>
          <div class="row"><button class="btn small" data-act="goto-data">${icon('eye')} Browse data</button>
            <button class="btn small ghost" data-act="goto-backups">${icon('backups')} Backups</button></div>
        </section>
      </div>

      ${S.databases.length ? `<section class="card" style="margin-top:16px">
        <div class="card-label">${icon('data')} Databases</div>
        <div class="db-chips">${S.databases.map(d => `<div class="db-chip" data-act="goto-data">
          <b>${esc(d.id)}</b><span class="small muted">${plural(d.containers.length, 'container')} · ${num(d.containers.reduce((n, c) => n + c.documents, 0))} docs</span></div>`).join('')}</div>
      </section>` : ''}

      ${S.source.configured ? `<div class="promise">${icon('shield')} Read-only: DbEmulator only ever reads from ${esc(S.source.host)}. Your app's writes stay on this computer.</div>` : ''}`;
  },
};

function notice(kind, ic, title, body, actions = '') {
  return `<div class="notice ${kind}">${icon(ic)}<div class="notice-body"><b>${esc(title)}</b><span>${body}</span></div>${actions ? `<div class="row">${actions}</div>` : ''}</div>`;
}

/* ======================================================================
   Data browser
   ====================================================================== */

const data = {
  sel: null,
  results: null,
  error: null,
  mount() {
    $('#page').innerHTML = `
      <div class="page-head"><div><h1>Data</h1><p>Browse the copy your app is using and try queries (Cosmos SQL).</p></div></div>
      <div class="browser">
        <section class="card tree" id="tree"></section>
        <div>
          <section class="card query-box">
            <div class="row" style="margin-bottom:10px"><h2 id="q-title">Select a container</h2><span class="spacer"></span><span class="small muted" id="q-pk"></span></div>
            <textarea id="q" class="code" spellcheck="false">SELECT * FROM c</textarea>
            <div class="row" style="margin-top:10px">
              <button class="btn primary" data-act="run-query">${icon('play')} Run query</button>
              <select id="q-max" style="width:auto"><option value="20">First 20</option><option value="100" selected>First 100</option><option value="1000">First 1000</option></select>
              <span class="small faint">Ctrl+Enter runs the query</span>
              <span class="spacer"></span>
              <button class="btn small ghost" data-act="expand-all">Expand all</button>
              <button class="btn small ghost" data-act="collapse-all">Collapse all</button>
            </div>
          </section>
          <div id="results"></div>
        </div>
      </div>`;
    $('#q').addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); this.run(); } });
    this.treeKey = '';
    this.update();
    if (!this.sel && S.databases.length && S.databases[0].containers.length) {
      this.select(S.databases[0].id, S.databases[0].containers[0].id);
    } else if (this.sel) {
      this.select(this.sel.db, this.sel.coll, false);
    }
    this.renderResults();
  },
  update() {
    const key = JSON.stringify(S.databases) + JSON.stringify(this.sel);
    if (key === this.treeKey) return;
    this.treeKey = key;
    const tree = $('#tree');
    if (!tree) return;
    tree.innerHTML = S.databases.length
      ? S.databases.map(d => `<div class="db">${icon('data')}${esc(d.id)}</div>` + d.containers.map(c =>
          `<button class="${this.sel && this.sel.db === d.id && this.sel.coll === c.id ? 'active' : ''}" data-act="select-container" data-db="${esc(d.id)}" data-coll="${esc(c.id)}">
            <span>${esc(c.id)}</span><span class="n">${num(c.documents)}</span></button>`).join('')).join('')
      : '<div class="empty">No data yet.<br>Fetch a copy first.</div>';
  },
  container() {
    const d = S.databases.find(x => this.sel && x.id === this.sel.db);
    return d && d.containers.find(c => c.id === this.sel.coll);
  },
  select(db, coll, run = true) {
    this.sel = { db, coll };
    const c = this.container();
    $('#q-title').textContent = `${db} / ${coll}`;
    $('#q-pk').textContent = c && c.partitionKey ? `Partition key ${c.partitionKey}` : '';
    this.treeKey = '';
    this.update();
    if (run) { $('#q').value = 'SELECT * FROM c'; this.run(); }
  },
  async run() {
    if (!this.sel) return;
    $('#results').innerHTML = '<div class="empty"><span class="spinner"></span></div>';
    try {
      this.results = await call('query', { database: this.sel.db, container: this.sel.coll, query: $('#q').value, maxItems: Number($('#q-max').value) });
      this.error = null;
    } catch (e) {
      this.results = null;
      this.error = e.message;
    }
    this.renderResults();
  },
  renderResults() {
    const el = $('#results');
    if (!el) return;
    if (this.error) { el.innerHTML = notice('bad', 'alert', 'The query could not run', esc(this.error)); return; }
    const r = this.results;
    if (!r) { el.innerHTML = ''; return; }
    const pkPath = (this.container()?.partitionKey || '').split(',')[0].trim().split('/').filter(Boolean);
    const pkOf = doc => pkPath.reduce((v, k) => (v && typeof v === 'object' ? v[k] : undefined), doc);
    el.innerHTML = `<div class="results-head"><b>${plural(r.items.length, 'result')}</b>
        ${r.truncated ? '<span class="pill warn">more available — raise the limit</span>' : ''}<span class="muted small">${r.elapsedMs} ms</span></div>
      ${r.items.length ? r.items.map((doc, i) => {
        const isObj = doc && typeof doc === 'object' && !Array.isArray(doc);
        const title = isObj && doc.id !== undefined ? doc.id : JSON.stringify(doc);
        const pk = isObj ? pkOf(doc) : undefined;
        return `<details class="doc" ${r.items.length === 1 ? 'open' : ''}><summary>${icon('chev').replace('<svg', '<svg class="chev"')}
          <span class="doc-id">${esc(String(title).slice(0, 200))}</span>${pk !== undefined && pk !== title ? `<span class="pill">${esc(JSON.stringify(pk))}</span>` : ''}
          <span class="spacer"></span><button class="btn small ghost" data-act="copy-doc" data-i="${i}" title="Copy JSON">${icon('copy')}</button></summary>
          <pre>${highlight(doc)}</pre></details>`;
      }).join('') : '<div class="card empty">No results.</div>'}`;
  },
};

/* ======================================================================
   Backups
   ====================================================================== */

const backups = {
  list: null,
  key: '',
  mount() {
    $('#page').innerHTML = `
      <div class="page-head"><div><h1>Backups</h1><p>Every fetch is kept as a complete copy in plain JSON files, so you can go back to any of them.</p></div>
        <span class="spacer"></span><button class="btn" data-act="open-folder" data-what="backups">${icon('folder')} Open backups folder</button></div>
      <section class="card" style="padding:6px 8px" id="backup-list"><div class="empty"><span class="spinner"></span></div></section>
      <p class="small muted" id="backup-note"></p>`;
    this.key = '';
    this.update();
  },
  async update() {
    const key = JSON.stringify([S.lastFetch?.name, S.active?.name, S.settings.keepSnapshots]);
    if (key === this.key) return;
    this.key = key;
    try { this.list = await call('getSnapshots'); } catch (e) { toast(e.message, 'bad'); return; }
    this.render();
  },
  render() {
    const el = $('#backup-list');
    if (!el) return;
    $('#backup-note').innerHTML = `Stored in <code>${esc(S.backupsDirectory)}</code>. ${S.settings.keepSnapshots ? `The newest ${S.settings.keepSnapshots} are kept` : 'All backups are kept'} (change this in Settings).`;
    if (!this.list.length) { el.innerHTML = '<div class="empty">No backups yet. Use “Fetch fresh copy” on the Overview.</div>'; return; }
    el.innerHTML = `<table class="list"><thead><tr><th>Fetched</th><th class="num">Documents</th><th class="num">Containers</th><th class="num">Size</th><th>From</th><th></th></tr></thead><tbody>
      ${this.list.map(b => { const v = b.view; return `<tr>
        <td><b>${esc(dateTime(v.completedUtc))}</b> ${v.active ? '<span class="pill good">Serving</span>' : ''}<br><span class="small muted">${esc(ago(v.completedUtc))} · ${num(v.requestCharge)} RU · ${v.durationSeconds} s</span></td>
        <td class="num">${num(v.documents)}</td><td class="num">${num(v.containers)}</td><td class="num nowrap">${bytes(b.sizeBytes)}</td>
        <td class="small">${esc(v.source || '')}</td>
        <td class="nowrap" style="text-align:right">
          ${v.active ? '' : `<button class="btn small" data-act="serve" data-name="${esc(v.name)}">${icon('restore')} Serve this copy</button>`}
          <button class="btn small ghost" data-act="open-folder" data-what="snapshot" data-name="${esc(v.name)}" title="Open folder">${icon('folder')}</button>
          ${v.active ? '' : `<button class="btn small ghost danger" data-act="delete-backup" data-name="${esc(v.name)}" title="Delete backup">${icon('trash')}</button>`}
        </td></tr>`; }).join('')}
      </tbody></table>`;
  },
};

/* ======================================================================
   Activity
   ====================================================================== */

const activity = {
  lastId: 0,
  timer: null,
  mount() {
    $('#page').innerHTML = `
      <div class="page-head"><div><h1>Activity</h1><p>What DbEmulator is doing, and every fetch it made.</p></div></div>
      <div class="grid" style="grid-template-columns: minmax(0, 3fr) minmax(0, 2fr)">
        <section class="card">
          <div class="row" style="margin-bottom:10px"><h2>Log</h2><span class="spacer"></span>
            <label class="switch"><input type="checkbox" id="log-requests" ${S.settings.logRequests ? 'checked' : ''}><span class="track"></span><span class="label"><b class="small">Show every request from your app</b></span></label></div>
          <div class="log" id="log"></div>
        </section>
        <section class="card">
          <div class="row" style="margin-bottom:10px"><h2>Fetch history</h2><span class="spacer"></span>
            <button class="btn small ghost" data-act="open-folder" data-what="logs">${icon('folder')}</button></div>
          <div id="history"></div>
        </section>
      </div>`;
    this.lastId = 0;
    $('#log-requests').onchange = e => act(() => call('saveSettings', { logRequests: e.target.checked }), e.target.checked ? 'Logging every request' : 'Request logging off');
    this.loadHistory();
    this.pollLog();
  },
  async pollLog() {
    clearTimeout(this.timer);
    if (currentPage !== 'activity') return;
    try {
      const entries = await call('getLog', { after: this.lastId });
      const el = $('#log');
      if (el && entries.length) {
        const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
        el.insertAdjacentHTML('beforeend', entries.map(e =>
          `<div class="log-line ${esc(e.level)}"><time>${new Date(e.time).toLocaleTimeString()}</time><i class="lvl"></i><span>${esc(e.message)}</span></div>`).join(''));
        this.lastId = entries[entries.length - 1].id;
        while (el.children.length > 1000) el.firstChild.remove();
        if (atBottom || entries.length === el.children.length) el.scrollTop = el.scrollHeight;
      } else if (el && !el.children.length) {
        el.innerHTML = '<div class="empty">Nothing logged yet.</div>';
      }
    } catch { /* retry */ }
    this.timer = setTimeout(() => this.pollLog(), 1000);
  },
  async loadHistory() {
    let lines = [];
    try { lines = await call('getFetchHistory'); } catch { /* none */ }
    const el = $('#history');
    if (!el) return;
    el.innerHTML = lines.length ? lines.map(line => {
      const m = line.match(/^(\S+)\s+(OK|FAILED)\s+(.*)$/);
      if (!m) return '';
      const info = Object.fromEntries([...m[3].matchAll(/(\w+)=(\S+)/g)].map(x => [x[1], x[2]]));
      return `<div style="padding:8px 0;border-bottom:1px solid var(--line)">
        <div class="row"><span class="pill ${m[2] === 'OK' ? 'good' : 'bad'}">${m[2] === 'OK' ? 'OK' : 'Failed'}</span><b class="small">${esc(dateTime(m[1]))}</b>
          ${line.includes('(not activated)') ? '<span class="pill">saved only</span>' : ''}</div>
        <div class="small muted" style="margin-top:3px">${m[2] === 'OK'
          ? `${plural(Number(info.documents), 'document')} · ${plural(Number(info.containers), 'container')} · ${num(parseFloat(info.requestCharge))} RU · ${num(info.readRequests)} reads, ${esc(info.blockedWrites)} writes blocked`
          : esc(m[3])}</div></div>`;
    }).join('') : '<div class="empty">No fetches yet.</div>';
  },
  update() {
    if (S.lastFetch?.name !== this.lastHistoryKey) { this.lastHistoryKey = S.lastFetch?.name; this.loadHistory(); }
  },
};

/* ======================================================================
   Settings
   ====================================================================== */

const settings = {
  knownDatabases: null,
  mount() {
    const o = S.settings;
    const src = S.source;
    const dbs = new Set([...(this.knownDatabases || []), ...S.databases.map(d => d.id), ...o.databases]);
    const all = !o.databases.length;
    $('#page').innerHTML = `
      <div class="page-head"><div><h1>Settings</h1><p>Saved in <code>${esc(S.dataDirectory)}\\settings.json</code>.</p></div></div>
      <section class="settings-section">
        <div><h2>Source account</h2><p>The live Azure Cosmos DB account DbEmulator copies from. It is only ever read.</p></div>
        <div class="stack">
          ${src.configured
            ? `<div class="row"><span class="pill good">${icon('shield').replace('<svg', '<svg style="width:13px;height:13px"')} Connected</span><b>${esc(src.host)}</b>
                <span class="muted small">${src.authentication === 'Key' ? 'read-only key' : 'Microsoft Entra ID'}, ${esc(src.origin)}</span></div>
               ${src.editable ? `<div class="row"><button class="btn" data-act="wizard-connect">${icon('key')} Change…</button>
                 <button class="btn danger" data-act="forget-source">${icon('trash')} Remove</button></div>` : `<p class="small muted">Set in ${esc(src.origin)}; change it there.</p>`}`
            : `<p class="muted">No account connected yet.</p><button class="btn primary" data-act="wizard-connect">${icon('plug')} Connect an account</button>`}
        </div>
      </section>
      <section class="settings-section">
        <div><h2>What to copy</h2><p>Copy every database, or only the ones your app needs (faster, fewer RUs).</p></div>
        <div class="stack">
          <label class="check"><input type="radio" name="dbmode" value="all" ${all ? 'checked' : ''}> All databases</label>
          <label class="check"><input type="radio" name="dbmode" value="some" ${all ? '' : 'checked'}> Only these databases:</label>
          <div class="dblist" id="db-choices">${[...dbs].map(id => `<label class="check"><input type="checkbox" value="${esc(id)}" ${o.databases.includes(id) ? 'checked' : ''}> ${esc(id)}</label>`).join('') || '<span class="small muted">Load the list from your account first.</span>'}</div>
          ${src.configured ? `<button class="btn small" data-act="load-databases">${icon('restore')} Load the list from your account</button>` : ''}
        </div>
      </section>
      <section class="settings-section">
        <div><h2>Local server</h2><p>Where your app connects (<code>https://localhost:port/</code>).</p></div>
        <div class="stack">
          <div class="field"><label for="s-port">Port</label><input type="number" id="s-port" min="1024" max="65535" value="${o.port}">
            <span class="hint">8081 is the standard emulator port. Pick another if the official Cosmos DB Emulator is running.</span></div>
          ${toggle('s-writes', o.allowLocalWrites, 'Let your app write to the copy', 'Writes stay on this computer. Turn off to serve the copy read-only.')}
          ${toggle('s-log', o.logRequests, 'Log every request', 'Shows each request from your app on the Activity page.')}
          ${toggle('s-ttl', o.enforceTtl, 'Hide expired documents (TTL)', 'Like Azure: documents whose time-to-live has passed disappear. Off keeps the fetched data as it was.')}
        </div>
      </section>
      <section class="settings-section">
        <div><h2>Backups</h2><p>Each fetch is stored as JSON files in <code>${esc(S.backupsDirectory)}</code>.</p></div>
        <div class="stack">
          <div class="field"><label for="s-keep">Backups to keep</label><input type="number" id="s-keep" min="0" value="${o.keepSnapshots}"><span class="hint">0 keeps every backup. The one being served is never removed.</span></div>
          ${toggle('s-compress', o.compressSnapshots, 'Compress backups (gzip)', 'Much smaller, but you need to unzip the files to read them outside DbEmulator.')}
          <div class="row"><button class="btn small" data-act="open-folder" data-what="data">${icon('folder')} Open data folder</button></div>
        </div>
      </section>
      <section class="settings-section">
        <div><h2>Automatic fetching</h2><p>Keep the copy fresh without clicking. 0 turns an option off.</p></div>
        <div class="stack">
          <div class="field"><label for="s-startup">Fetch at startup when the copy is older than (hours)</label><input type="number" id="s-startup" min="0" step="1" value="${o.onStartupIfOlderThanHours}"></div>
          <div class="field"><label for="s-every">Fetch every (hours) while running</label><input type="number" id="s-every" min="0" step="1" value="${o.autoFetchEveryHours}">
            <span class="hint">An automatic fetch replaces local changes. After a failure it waits before trying again.</span></div>
        </div>
      </section>
      ${S.desktop ? `<section class="settings-section">
        <div><h2>Window</h2><p>Your app needs DbEmulator running while you develop.</p></div>
        <div class="stack">${toggle('s-tray', o.closeToTray, 'Keep running in the system tray when the window is closed', 'Quit from the tray icon or with the button below.')}
          <div class="row"><button class="btn danger" data-act="quit">${icon('x')} Quit DbEmulator</button></div></div>
      </section>` : ''}
      <section class="settings-section">
        <div><h2>About</h2><p>DbEmulator ${esc(S.version)}</p></div>
        <div class="stack small muted">
          <span>Every request to Azure goes through a read-only guard: writes are blocked before they leave this computer.</span>
          <span><a data-act="open-github">Source code and documentation on GitHub</a></span>
        </div>
      </section>
      <div class="save-bar"><button class="btn primary" data-act="save-settings">${icon('check')} Save changes</button><span class="small muted" id="save-note"></span></div>`;
    const syncDbList = () => {
      const some = document.querySelector('input[name=dbmode][value=some]').checked;
      document.querySelectorAll('#db-choices input').forEach(i => { i.disabled = !some; });
    };
    document.querySelectorAll('input[name=dbmode]').forEach(r => r.onchange = syncDbList);
    syncDbList();
  },
  values() {
    const n = (id, d) => { const v = Number($(id)?.value); return Number.isFinite(v) ? v : d; };
    const some = document.querySelector('input[name=dbmode][value=some]').checked;
    return {
      port: n('#s-port', S.settings.port),
      allowLocalWrites: $('#s-writes').checked,
      logRequests: $('#s-log').checked,
      enforceTtl: $('#s-ttl').checked,
      keepSnapshots: n('#s-keep', S.settings.keepSnapshots),
      compressSnapshots: $('#s-compress').checked,
      onStartupIfOlderThanHours: n('#s-startup', 0),
      autoFetchEveryHours: n('#s-every', 0),
      closeToTray: $('#s-tray') ? $('#s-tray').checked : S.settings.closeToTray,
      databases: some ? [...document.querySelectorAll('#db-choices input:checked')].map(i => i.value) : [],
    };
  },
};

function toggle(id, on, title, text) {
  return `<label class="switch"><input type="checkbox" id="${id}" ${on ? 'checked' : ''}><span class="track"></span><span class="label"><b>${esc(title)}</b><span>${esc(text)}</span></span></label>`;
}

const pages = { overview, data, backups, activity, settings };

/* ======================================================================
   Setup wizard
   ====================================================================== */

let wizard = null;
const STEPS = ['welcome', 'connect', 'choose', 'fetch', 'done'];

function maybeStartWizard() {
  let skipped = false;
  try { skipped = localStorage.getItem('wizardSkipped') === '1'; } catch { /* ignore */ }
  if (!S.source.configured && !S.lastFetch && !skipped) openWizard('welcome');
}

function openWizard(step) {
  wizard = { step, mode: 'key', cs: '', endpoint: '', tenant: '', test: null, testing: false, all: true, selected: new Set(), fetchStarted: false };
  renderWizard();
}

function closeWizard() {
  wizard = null;
  $('#modal').innerHTML = '';
  lastRendered = '';
  refresh();
}

function wizardTick() {
  if (wizard.step !== 'fetch') return;
  if (wizard.fetchStarted && !S.fetch.running) {
    const a = S.lastAttempt;
    if (a && a.succeeded) { wizard.step = 'done'; }
    else { wizard.fetchStarted = false; wizard.fetchError = a ? a.error : 'The fetch did not complete.'; }
  }
  renderWizard();
}

function renderWizard() {
  const w = wizard;
  const i = STEPS.indexOf(w.step);
  let body = '';
  let foot = '';
  if (w.step === 'welcome') {
    body = `<h1>Welcome to DbEmulator</h1>
      <p>Work with a copy of your Azure Cosmos DB data on your own computer.</p>
      <div class="feature"><div class="ic">${icon('fetch')}</div><div><b>Copies your database — read-only</b><span class="muted">DbEmulator only reads from your account. It never writes to it.</span></div></div>
      <div class="feature"><div class="ic">${icon('backups')}</div><div><b>Keeps every copy as a backup</b><span class="muted">Plain JSON files on this computer; go back to any copy at any time.</span></div></div>
      <div class="feature"><div class="ic">${icon('plug')}</div><div><b>Your app connects as if it were Cosmos DB</b><span class="muted">Point your connection string at localhost. Your app's writes stay local.</span></div></div>`;
    foot = `<button class="btn ghost" data-w="skip">Skip for now</button><span class="spacer"></span><button class="btn primary" data-w="next">Get started</button>`;
  } else if (w.step === 'connect') {
    body = `<h1>Connect to your Cosmos DB account</h1>
      <div class="tabs"><button class="${w.mode === 'key' ? 'active' : ''}" data-w="mode-key">Read-only key (recommended)</button><button class="${w.mode === 'entra' ? 'active' : ''}" data-w="mode-entra">Microsoft Entra ID</button></div>
      ${w.mode === 'key' ? `
        <ol class="steps-list"><li>Open your Cosmos DB account in the <a data-w="portal">Azure Portal</a></li>
          <li>Go to <b>Settings › Keys</b> and choose the <b>Read-only Keys</b> tab</li>
          <li>Copy the <b>PRIMARY READ-ONLY CONNECTION STRING</b> and paste it below</li></ol>
        <div class="field"><label for="w-cs">Read-only connection string</label>
          <input type="password" id="w-cs" placeholder="AccountEndpoint=https://…documents.azure.com:443/;AccountKey=…;" value="${esc(w.cs)}" autocomplete="off" spellcheck="false">
          <span class="hint">Saved encrypted for your Windows user. A read-only key can't change your data — and DbEmulator blocks writes regardless.</span></div>`
      : `
        <p>Uses your Azure sign-in (Azure CLI <code>az login</code>, Visual Studio, or a browser prompt). Your account needs the <b>Cosmos DB Built-in Data Reader</b> role.</p>
        <div class="field"><label for="w-endpoint">Account endpoint</label><input type="text" id="w-endpoint" placeholder="https://myaccount.documents.azure.com:443/" value="${esc(w.endpoint)}" spellcheck="false"></div>
        <div class="field" style="margin-top:12px"><label for="w-tenant">Tenant ID <span class="faint">(optional)</span></label><input type="text" id="w-tenant" value="${esc(w.tenant)}" spellcheck="false"></div>`}
      ${w.testing ? `<div class="result-box" style="background:var(--panel-2)"><span class="spinner"></span> Connecting (read-only)…</div>` : ''}
      ${w.test ? (w.test.ok
        ? `<div class="result-box good">${icon('check').replace('<svg', '<svg style="width:16px;height:16px;vertical-align:-3px"')} Connected to <b>${esc(w.test.host)}</b> — ${plural(w.test.databases.length, 'database')} found.</div>`
        : `<div class="result-box bad">${esc(w.test.error)}</div>`) : ''}`;
    foot = `<button class="btn ghost" data-w="back">Back</button><span class="spacer"></span>
      <button class="btn" data-w="test" ${w.testing ? 'disabled' : ''}>Test connection</button>
      <button class="btn primary" data-w="next" ${w.test && w.test.ok ? '' : 'disabled'}>Next</button>`;
  } else if (w.step === 'choose') {
    const dbs = w.test.databases;
    body = `<h1>Choose what to copy</h1><p>Copy everything, or only the databases your app uses.</p>
      <label class="check"><input type="checkbox" data-w="all" ${w.all ? 'checked' : ''}> <b>All databases</b></label>
      <div class="dblist" style="margin-top:8px">${dbs.length ? dbs.map(d => `<label class="check"><input type="checkbox" data-w="db" value="${esc(d.id)}" ${w.all || w.selected.has(d.id) ? 'checked' : ''} ${w.all ? 'disabled' : ''}> ${esc(d.id)}</label>
        <div class="sub">${d.containers.length ? esc(d.containers.join(', ')) : 'no containers'}</div>`).join('') : '<span class="muted">This account has no databases.</span>'}</div>`;
    foot = `<button class="btn ghost" data-w="back">Back</button><span class="spacer"></span>
      <button class="btn primary" data-w="save" ${w.all || w.selected.size ? '' : 'disabled'}>Save and continue</button>`;
  } else if (w.step === 'fetch') {
    const p = S.fetch.progress;
    const running = S.fetch.running;
    const pct = p && p.containerExpected ? Math.min(100, 100 * p.containerDocuments / p.containerExpected) : null;
    body = `<h1>${running ? 'Copying your data…' : 'Make your first copy'}</h1>
      ${running ? `<p>${p ? `Copying <b>${esc(p.database)} / ${esc(p.container)}</b> — ${num(p.totalDocuments)} documents so far` : 'Connecting…'}</p>
        <div class="progress ${pct === null ? 'indeterminate' : ''}"><div style="width:${pct ?? 0}%"></div></div>
        <p class="small">You can close this window — the copy keeps going.</p>`
      : `<p>DbEmulator reads every document once and saves it as a backup on this computer. On a large account this uses Request Units, like any full read.</p>
         ${w.fetchError ? `<div class="result-box bad">${esc(w.fetchError)}</div>` : ''}`}`;
    foot = running ? `<span class="spacer"></span><button class="btn" data-w="close">Continue in the background</button>`
      : `<button class="btn ghost" data-w="close">Later</button><span class="spacer"></span><button class="btn primary big" data-w="fetch">${icon('fetch')} Fetch now</button>`;
  } else if (w.step === 'done') {
    body = `<h1>You're all set</h1>
      <p>Your copy is ready${S.lastFetch ? ` — ${plural(S.lastFetch.documents, 'document')} in ${plural(S.lastFetch.containers, 'container')}` : ''}. Point your app at it:</p>
      <div class="copybox"><code>${esc(S.server.connectionString)}</code><button class="btn small" data-w="copy-cs">${icon('copy')}</button></div>
      <pre class="snippet">${esc(csharpSnippet(S.server.connectionString))}</pre>
      <p class="small">Keep DbEmulator running while you develop. It lives in the system tray when you close the window.</p>`;
    foot = `<button class="btn" data-w="copy-snippet">${icon('copy')} Copy C# code</button><span class="spacer"></span><button class="btn primary" data-w="close">Open DbEmulator</button>`;
  }

  $('#modal').innerHTML = `<div class="overlay"><div class="wizard">
    <div class="wizard-head"><div class="steps">${STEPS.map((_, n) => `<i class="${n <= i ? 'done' : ''}"></i>`).join('')}</div></div>
    <div class="wizard-body stack">${body}</div>
    <div class="wizard-foot">${foot}</div></div></div>`;

  const bind = (id, key) => { const el = $(id); if (el) el.oninput = () => { w[key] = el.value; w.test = null; renderFootOnly(); }; };
  bind('#w-cs', 'cs'); bind('#w-endpoint', 'endpoint'); bind('#w-tenant', 'tenant');
}

function renderFootOnly() {
  const next = document.querySelector('[data-w="next"]');
  if (next && wizard.step === 'connect') next.disabled = true;
  document.querySelectorAll('.result-box').forEach(b => b.remove());
}

function sourceArgs(w) {
  return w.mode === 'key' ? { connectionString: w.cs.trim() } : { endpoint: w.endpoint.trim(), tenantId: w.tenant.trim() };
}

document.addEventListener('click', async e => {
  const el = e.target.closest('[data-w]');
  if (!el || !wizard) return;
  const w = wizard;
  switch (el.dataset.w) {
    case 'skip':
      try { localStorage.setItem('wizardSkipped', '1'); } catch { /* ignore */ }
      closeWizard();
      return;
    case 'close': closeWizard(); return;
    case 'next': w.step = STEPS[STEPS.indexOf(w.step) + 1]; break;
    case 'back': w.step = STEPS[Math.max(0, STEPS.indexOf(w.step) - 1)]; break;
    case 'mode-key': w.mode = 'key'; w.test = null; break;
    case 'mode-entra': w.mode = 'entra'; w.test = null; break;
    case 'portal': call('openExternal', { url: 'https://portal.azure.com/#browse/Microsoft.DocumentDb%2FdatabaseAccounts' }).catch(() => {}); return;
    case 'test':
      w.testing = true; w.test = null; renderWizard();
      try { w.test = await call('testConnection', sourceArgs(w)); } catch (err) { w.test = { ok: false, error: err.message }; }
      w.testing = false;
      if (w.test.ok) { w.selected = new Set(w.test.databases.map(d => d.id)); settings.knownDatabases = w.test.databases.map(d => d.id); }
      break;
    case 'all': w.all = el.checked; break;
    case 'db': el.checked ? w.selected.add(el.value) : w.selected.delete(el.value); break;
    case 'save':
      try {
        await call('saveSource', { ...sourceArgs(w), databases: w.all ? [] : [...w.selected] });
        await refresh();
        w.step = 'fetch';
      } catch (err) { toast(err.message, 'bad'); return; }
      break;
    case 'fetch':
      try { await call('fetch'); w.fetchStarted = true; w.fetchError = null; await refresh(); } catch (err) { toast(err.message, 'bad'); }
      break;
    case 'copy-cs': copy(S.server.connectionString, 'Connection string copied'); return;
    case 'copy-snippet': copy(csharpSnippet(S.server.connectionString), 'C# code copied'); return;
    default: return;
  }
  renderWizard();
});

/* ======================================================================
   Actions
   ====================================================================== */

document.addEventListener('click', async e => {
  const el = e.target.closest('[data-act]');
  if (!el) return;
  const a = el.dataset.act;
  switch (a) {
    case 'fetch':
      if (S.localChanges > 0 && !await confirmDialog({ title: 'Fetch a fresh copy?', body: `The new copy replaces your ${plural(S.localChanges, 'local change')}. The current copy stays available under Backups.`, ok: 'Fetch' })) return;
      await act(() => call('fetch'));
      break;
    case 'cancel-fetch': await act(() => call('cancelFetch')); break;
    case 'reset':
      if (await confirmDialog({ title: 'Discard local changes?', body: `This removes the ${plural(S.localChanges, 'change')} your app made and goes back to the fetched copy.`, ok: 'Discard', danger: true })) {
        await act(() => call('resetLocalChanges'), 'Local changes discarded');
      }
      break;
    case 'serve': {
      const extra = S.localChanges > 0 ? ` Your ${plural(S.localChanges, 'local change')} will be discarded.` : '';
      if (await confirmDialog({ title: 'Serve this copy?', body: `Your app will see the data as it was when this backup was made.${extra}`, ok: 'Serve this copy' })) {
        await act(() => call('serveSnapshot', { name: el.dataset.name }), 'Now serving that copy');
        backups.key = '';
      }
      break;
    }
    case 'delete-backup':
      if (await confirmDialog({ title: 'Delete this backup?', body: 'The files are removed from this computer. This cannot be undone.', ok: 'Delete', danger: true })) {
        await act(() => call('deleteSnapshot', { name: el.dataset.name }), 'Backup deleted');
        backups.key = '';
        backups.update();
      }
      break;
    case 'open-folder': act(() => call('openFolder', { what: el.dataset.what, name: el.dataset.name })); break;
    case 'copy-cs': copy(S.server.connectionString, 'Connection string copied'); break;
    case 'copy-snippet': copy(csharpSnippet(S.server.connectionString), 'C# code copied'); break;
    case 'copy-doc': e.preventDefault(); copy(JSON.stringify(data.results.items[Number(el.dataset.i)], null, 2), 'Document copied'); break;
    case 'goto-data': showPage('data'); break;
    case 'goto-backups': showPage('backups'); break;
    case 'goto-settings': showPage('settings'); break;
    case 'restart-server': await act(() => call('restartServer')); break;
    case 'wizard': openWizard('welcome'); break;
    case 'wizard-connect': openWizard('connect'); break;
    case 'forget-source':
      if (await confirmDialog({ title: 'Remove the source account?', body: 'DbEmulator forgets the saved connection. Your backups and the current copy stay.', ok: 'Remove', danger: true })) {
        await act(() => call('forgetSource'), 'Source account removed');
        settings.mount();
      }
      break;
    case 'load-databases': {
      el.disabled = true;
      try {
        const r = await call('testConnection', {});
        if (!r.ok) throw new Error(r.error);
        settings.knownDatabases = r.databases.map(d => d.id);
        settings.mount();
        toast(`${plural(r.databases.length, 'database')} found`);
      } catch (err) { toast(err.message, 'bad'); el.disabled = false; }
      break;
    }
    case 'save-settings': {
      const v = settings.values();
      const portChanged = v.port !== S.settings.port;
      const r = await act(() => call('saveSettings', v), 'Settings saved');
      if (r) {
        $('#save-note').textContent = portChanged ? `Your app now connects to https://localhost:${v.port}/ — update its connection string.` : '';
      }
      break;
    }
    case 'select-container': data.select(el.dataset.db, el.dataset.coll); break;
    case 'run-query': data.run(); break;
    case 'expand-all': document.querySelectorAll('details.doc').forEach(d => { d.open = true; }); break;
    case 'collapse-all': document.querySelectorAll('details.doc').forEach(d => { d.open = false; }); break;
    case 'open-github': call('openExternal', { url: 'https://github.com/hwoudenberg99/custom-db-emulator' }).catch(() => {}); break;
    case 'quit':
      if (await confirmDialog({ title: 'Quit DbEmulator?', body: 'Your app will no longer be able to connect until you start it again.', ok: 'Quit', danger: true })) {
        call('quit');
      }
      break;
    default:
      break;
  }
});

poll();
