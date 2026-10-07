namespace DbEmulator.Hosting;

/// <summary>The /_emulator page: last fetch, backups, local changes and a small data browser.</summary>
internal static class StatusPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DbEmulator</title>
<style>
:root {
  --bg: #f6f7f9; --card: #ffffff; --text: #1d2330; --muted: #5f6b7a; --line: #e3e6eb;
  --accent: #2f6fde; --accent-text: #ffffff; --good: #1f9254; --warn: #b7791f; --bad: #c53030; --code: #f1f3f6;
}
@media (prefers-color-scheme: dark) {
  :root { --bg: #14171c; --card: #1c2027; --text: #e6e9ee; --muted: #9aa4b1; --line: #2c323b;
    --accent: #5b8ff0; --accent-text: #0b1220; --good: #48bb78; --warn: #ecc94b; --bad: #fc8181; --code: #232831; }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
main { max-width: 1080px; margin: 0 auto; padding: 24px 16px 48px; }
header { display: flex; align-items: baseline; gap: 12px; flex-wrap: wrap; margin-bottom: 20px; }
h1 { font-size: 22px; margin: 0; }
h2 { font-size: 16px; margin: 0 0 12px; }
.badge { font-size: 12px; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--line); color: var(--muted); }
.card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 18px; margin-bottom: 16px; }
.hero { display: flex; justify-content: space-between; align-items: center; gap: 16px; flex-wrap: wrap; }
.when { font-size: 28px; font-weight: 650; }
.dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 8px; vertical-align: middle; }
.muted { color: var(--muted); }
.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(230px, 1fr)); gap: 12px; }
.fact .label { font-size: 12px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); }
.fact .value { overflow-wrap: anywhere; }
button { font: inherit; border: 1px solid var(--line); background: var(--card); color: var(--text); padding: 7px 14px; border-radius: 8px; cursor: pointer; }
button.primary { background: var(--accent); border-color: var(--accent); color: var(--accent-text); }
button:disabled { opacity: .55; cursor: default; }
.actions { display: flex; gap: 8px; flex-wrap: wrap; }
table { width: 100%; border-collapse: collapse; }
th, td { text-align: left; padding: 7px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
th { font-size: 12px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); font-weight: 600; }
td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
.scroll { overflow-x: auto; }
code, pre, textarea, select { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; font-size: 13px; }
pre { background: var(--code); border-radius: 8px; padding: 12px; overflow: auto; max-height: 480px; margin: 0; }
textarea { width: 100%; min-height: 70px; padding: 10px; border-radius: 8px; border: 1px solid var(--line); background: var(--code); color: var(--text); }
select { padding: 6px 8px; border-radius: 8px; border: 1px solid var(--line); background: var(--card); color: var(--text); max-width: 100%; }
.progress { height: 6px; background: var(--line); border-radius: 999px; overflow: hidden; margin-top: 10px; }
.progress > div { height: 100%; width: 0; background: var(--accent); transition: width .4s; }
.error { color: var(--bad); }
.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
.pill { font-size: 12px; padding: 1px 7px; border-radius: 999px; background: var(--code); }
</style>
</head>
<body>
<main>
  <header>
    <h1>DbEmulator</h1>
    <span class="badge" id="source">read-only mirror of Azure Cosmos DB</span>
  </header>

  <section class="card">
    <div class="hero">
      <div>
        <div class="muted">Last fetched from Cosmos DB</div>
        <div class="when"><span class="dot" id="dot"></span><span id="when">…</span></div>
        <div class="muted" id="whenDetail"></div>
      </div>
      <div class="actions">
        <button class="primary" id="fetchBtn">Fetch fresh copy</button>
        <button id="resetBtn">Reset local changes</button>
      </div>
    </div>
    <div id="progressBox" hidden>
      <div class="progress"><div id="progressBar"></div></div>
      <div class="muted" id="progressText"></div>
    </div>
    <div class="error" id="attemptError"></div>
  </section>

  <section class="card grid">
    <div class="fact"><div class="label">Endpoint for your app</div><div class="value"><code id="endpoint"></code></div></div>
    <div class="fact"><div class="label">Serving snapshot</div><div class="value" id="active"></div></div>
    <div class="fact"><div class="label">Local changes</div><div class="value" id="changes"></div></div>
    <div class="fact"><div class="label">Data folder</div><div class="value"><code id="dataDir"></code></div></div>
  </section>

  <section class="card">
    <h2>Data</h2>
    <div class="scroll"><table>
      <thead><tr><th>Database</th><th>Container</th><th>Partition key</th><th class="num">Documents</th><th></th></tr></thead>
      <tbody id="containers"></tbody>
    </table></div>
  </section>

  <section class="card">
    <h2>Query</h2>
    <div class="row"><select id="target"></select></div>
    <textarea id="query">SELECT TOP 20 * FROM c</textarea>
    <div class="row" style="margin-top:8px"><button class="primary" id="runBtn">Run</button><span class="muted" id="queryInfo"></span></div>
    <pre id="results">Results appear here.</pre>
  </section>

  <section class="card">
    <h2>Backups (snapshots)</h2>
    <div class="scroll"><table>
      <thead><tr><th>Fetched</th><th class="num">Documents</th><th class="num">Size</th><th></th></tr></thead>
      <tbody id="snapshots"></tbody>
    </table></div>
  </section>
</main>
<script>
const $ = id => document.getElementById(id);
const fmt = n => Number(n).toLocaleString();
const size = b => b > 1e9 ? (b / 1e9).toFixed(2) + ' GB' : b > 1e6 ? (b / 1e6).toFixed(1) + ' MB' : (b / 1e3).toFixed(0) + ' KB';
function ago(iso) {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return 'just now';
  if (s < 5400) return Math.round(s / 60) + ' minutes ago';
  if (s < 129600) return Math.round(s / 3600) + ' hours ago';
  return Math.round(s / 86400) + ' days ago';
}
async function post(url, body) {
  const r = await fetch(url, { method: 'POST', headers: { 'X-DbEmulator-Action': '1', 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) });
  if (!r.ok) { let m = r.statusText; try { m = (await r.json()).message || m; } catch {} throw new Error(m); }
  return r.status === 202 || r.status === 200 ? r.json().catch(() => ({})) : {};
}
let lastTargets = '';
async function refresh() {
  let s;
  try { s = await (await fetch('/_emulator/status')).json(); } catch { return; }
  $('source').textContent = s.source.configured ? 'read-only mirror of ' + s.source.host : 'source not configured';
  if (s.lastFetch) {
    const age = s.lastFetch.ageHours;
    $('when').textContent = ago(s.lastFetch.completedUtc);
    $('whenDetail').textContent = new Date(s.lastFetch.completedUtc).toLocaleString() + ' · ' + fmt(s.lastFetch.documents) + ' documents in ' + s.lastFetch.containers + ' containers';
    $('dot').style.background = age < 24 ? 'var(--good)' : age < 168 ? 'var(--warn)' : 'var(--bad)';
  } else {
    $('when').textContent = 'never';
    $('whenDetail').textContent = 'Click "Fetch fresh copy" (or press F in the console) to copy your Cosmos DB data.';
    $('dot').style.background = 'var(--bad)';
  }
  const a = s.lastAttempt;
  $('attemptError').textContent = a && !a.succeeded && a.completedUtc ? 'Last fetch attempt failed (' + new Date(a.completedUtc).toLocaleString() + '): ' + a.error : '';
  $('endpoint').textContent = s.endpoint;
  $('active').textContent = s.activeSnapshot || '(empty)';
  $('changes').textContent = fmt(s.localChanges) + (s.allowLocalWrites ? ' (kept locally, never sent to Azure)' : ' (local writes disabled)');
  $('dataDir').textContent = s.dataDirectory;
  $('fetchBtn').disabled = s.fetchInProgress || !s.source.configured;
  $('progressBox').hidden = !s.fetchInProgress;
  if (s.fetchInProgress && s.fetchProgress) {
    const p = s.fetchProgress;
    const pct = p.containerExpected ? Math.min(100, 100 * p.containerDocuments / p.containerExpected) : 50;
    $('progressBar').style.width = pct + '%';
    $('progressText').textContent = 'Fetching ' + p.database + '/' + p.container + ': ' + fmt(p.containerDocuments) + (p.containerExpected ? ' of ' + fmt(p.containerExpected) : '') + ' documents · ' + fmt(p.totalDocuments) + ' total · ' + fmt(p.requestCharge) + ' RU';
  }
  const rows = [], targets = [];
  for (const d of s.databases) for (const c of d.containers) {
    rows.push('<tr><td>' + esc(d.id) + '</td><td>' + esc(c.id) + '</td><td><code>' + esc(c.partitionKey) + '</code></td><td class="num">' + fmt(c.documents) + '</td><td><button data-db="' + esc(d.id) + '" data-c="' + esc(c.id) + '" class="browse">Browse</button></td></tr>');
    targets.push(d.id + '/' + c.id);
  }
  $('containers').innerHTML = rows.join('') || '<tr><td colspan="5" class="muted">No data yet.</td></tr>';
  document.querySelectorAll('.browse').forEach(b => b.onclick = () => { $('target').value = b.dataset.db + '/' + b.dataset.c; $('query').value = 'SELECT TOP 20 * FROM c'; run(); });
  if (targets.join('|') !== lastTargets) {
    lastTargets = targets.join('|');
    $('target').innerHTML = targets.map(t => '<option>' + esc(t) + '</option>').join('');
  }
  $('snapshots').innerHTML = s.snapshots.map(x => '<tr><td>' + new Date(x.completedUtc).toLocaleString() + ' <span class="muted">(' + ago(x.completedUtc) + ')</span>' + (x.active ? ' <span class="pill">serving</span>' : '') + '</td><td class="num">' + fmt(x.documents) + '</td><td class="num">' + size(x.sizeBytes) + '</td><td>' + (x.active ? '' : '<button class="restore" data-name="' + esc(x.name) + '">Serve this copy</button>') + '</td></tr>').join('') || '<tr><td colspan="4" class="muted">No backups yet.</td></tr>';
  document.querySelectorAll('.restore').forEach(b => b.onclick = async () => {
    if (!confirm('Serve the copy from ' + b.dataset.name + '? Local changes will be discarded.')) return;
    try { await post('/_emulator/api/restore', { snapshot: b.dataset.name }); } catch (e) { alert(e.message); }
    refresh();
  });
  clearTimeout(window.t); window.t = setTimeout(refresh, s.fetchInProgress ? 1000 : 4000);
}
function esc(v) { return String(v).replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch])); }
async function run() {
  const [database, ...rest] = $('target').value.split('/');
  $('queryInfo').textContent = 'Running…';
  try {
    const r = await post('/_emulator/api/query', { database, container: rest.join('/'), query: $('query').value });
    $('results').textContent = JSON.stringify(r.items, null, 2);
    $('queryInfo').textContent = r.items.length + ' result(s)' + (r.truncated ? ' (first 100 shown)' : '');
  } catch (e) { $('results').textContent = e.message; $('queryInfo').textContent = 'Error'; }
}
$('runBtn').onclick = run;
$('fetchBtn').onclick = async () => {
  if (!confirm('Fetch a fresh copy from Azure Cosmos DB (read-only)? Your local changes will be replaced.')) return;
  try { await post('/_emulator/api/fetch'); } catch (e) { alert(e.message); }
  setTimeout(refresh, 500);
};
$('resetBtn').onclick = async () => {
  if (!confirm('Discard all local changes and go back to the fetched copy?')) return;
  try { await post('/_emulator/api/reset'); } catch (e) { alert(e.message); }
  refresh();
};
refresh();
</script>
</body>
</html>
""";
}
