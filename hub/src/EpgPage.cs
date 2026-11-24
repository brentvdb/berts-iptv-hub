namespace BertsIptvHub;

/// <summary>/epg — see where each channel's guide comes from and pick a public guide channel by hand.</summary>
public static class EpgPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Guide matching · Bert's IPTV Hub</title>
<link rel="icon" href="/favicon.ico">
<style>
  :root { color-scheme: dark; --bg:#060a16; --panel:#0c1627; --line:#1f3550; --cyan:#22e6ff; --magenta:#ff2bd6; --violet:#9b7bff;
          --ok:#7dffb0; --warn:#ffbd6b; --bad:#ff8a9a; --text:#eaf6ff; --muted:#94a9c3; }
  * { box-sizing: border-box; }
  body { margin:0; color:var(--text); font:15px/1.5 "Segoe UI", system-ui, sans-serif;
         background: radial-gradient(900px 420px at 0% -10%, rgba(34,230,255,.14), transparent 60%),
                     radial-gradient(900px 420px at 100% -10%, rgba(255,43,214,.13), transparent 60%), var(--bg); min-height:100vh; }
  main { max-width:1200px; margin:0 auto; padding:28px 22px 60px; }
  header { display:flex; align-items:center; gap:16px; flex-wrap:wrap; margin-bottom:18px; }
  header img { width:56px; height:56px; filter:drop-shadow(0 0 12px rgba(34,230,255,.45)); }
  h1 { margin:0; font-size:2rem; font-weight:800; background:linear-gradient(90deg,var(--cyan),var(--violet) 55%,var(--magenta));
       -webkit-background-clip:text; background-clip:text; color:transparent; }
  a { color:var(--cyan); }
  .card { background:linear-gradient(180deg,var(--panel),#0a1322); border:1px solid var(--line); border-radius:14px; padding:16px 18px; margin-top:14px; }
  .row { display:flex; gap:12px; flex-wrap:wrap; align-items:center; }
  input, select, button { font:inherit; color:var(--text); background:#08111f; border:1px solid var(--line); border-radius:9px; padding:7px 10px; }
  input:focus, select:focus { outline:none; border-color:var(--cyan); box-shadow:0 0 0 2px rgba(34,230,255,.25); }
  button { cursor:pointer; border-color:rgba(34,230,255,.55); }
  button:hover { box-shadow:0 0 12px rgba(34,230,255,.35); }
  .stat b { color:var(--cyan); }
  table { width:100%; border-collapse:collapse; margin-top:6px; }
  th { text-align:left; color:var(--muted); font-size:.75rem; letter-spacing:.12em; text-transform:uppercase; padding:8px 6px; border-bottom:1px solid var(--line); }
  td { padding:7px 6px; border-bottom:1px solid rgba(31,53,80,.6); vertical-align:middle; }
  .pill { display:inline-block; border-radius:999px; padding:1px 9px; font-size:.75rem; font-weight:700; border:1px solid var(--line); }
  .provider { color:var(--ok); border-color:rgba(125,255,176,.45); }
  .auto { color:var(--cyan); border-color:rgba(34,230,255,.5); }
  .manual { color:var(--magenta); border-color:rgba(255,43,214,.5); }
  .none, .manual-none { color:var(--muted); }
  .muted { color:var(--muted); }
  .fav { color:var(--magenta); }
  td input.pick { width:100%; min-width:260px; }
  .msg { color:var(--ok); min-height:1.4em; }
</style>
</head>
<body>
<main>
  <header>
    <img src="/logo.png" alt="">
    <div><h1>Guide matching</h1><div class="muted">Where each channel's TV guide comes from · <a href="/">← hub status</a></div></div>
  </header>

  <section class="card" id="login" hidden>
    <div class="row">
      <span>TV password:</span><input id="pw" type="password" autocomplete="current-password">
      <button id="go">Open</button><span class="muted">(the same password your TVs use)</span>
    </div>
  </section>

  <section class="card" id="top" hidden>
    <div class="row stat" id="summary"></div>
    <div class="muted" id="sources" style="margin-top:6px"></div>
  </section>

  <section class="card" id="list" hidden>
    <div class="row">
      <input id="q" placeholder="Search channels…" style="flex:1;min-width:220px">
      <select id="filter">
        <option value="missing">Without a guide</option>
        <option value="fav">Favourites</option>
        <option value="public">From public guides</option>
        <option value="all">All channels</option>
      </select>
      <span class="msg" id="msg"></span>
    </div>
    <p class="muted" style="margin:10px 0 0">To fix a channel, type in the box to search the public guide channels, pick one, then press <b>Save</b>.
       <b>Auto</b> puts it back to automatic matching, and <b>None</b> means the channel never shows a guide.
       FloRugby and other event-only channels have no public guide.</p>
    <table>
      <thead><tr><th></th><th>Channel</th><th>Guide</th><th>On now</th><th style="width:38%">Use this public guide channel</th><th></th></tr></thead>
      <tbody id="rows"></tbody>
    </table>
    <datalist id="sourceList"></datalist>
  </section>
  <p class="muted" style="text-align:center;margin-top:24px">Bert's IPTV Hub {{VERSION}}</p>
</main>
<script>
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let pw = '';
try { pw = localStorage.getItem('hubPw') || ''; } catch (e) {}
let data = null, byLabel = new Map();
const $ = id => document.getElementById(id);
const auth = () => `username=home&password=${encodeURIComponent(pw)}`;

async function load() {
  const r = await fetch('/api/epg/report?' + auth(), { cache: 'no-store' });
  if (r.status === 401) { $('login').hidden = false; $('top').hidden = $('list').hidden = true; return; }
  data = await r.json();
  $('login').hidden = true; $('top').hidden = $('list').hidden = false;
  const s = data.summary;
  $('summary').innerHTML = `<span>Channels: <b>${s.total}</b></span><span>From your provider: <b>${s.provider}</b></span>` +
    `<span>From public guides: <b>${s.publicGuides}</b></span><span>Chosen by hand: <b>${s.manual}</b></span>` +
    `<span>No guide: <b>${s.total - s.provider - s.publicGuides - s.manual}</b></span>`;
  $('sources').innerHTML = 'Public guides: ' + (data.sources.map(x => `${esc(x.label)} <span class="muted">(${x.updated ? 'updated ' + new Date(x.updated).toLocaleString() : 'not downloaded yet'})</span>`).join(' · ') || 'none');
  byLabel = new Map();
  $('sourceList').innerHTML = data.sourceChannels.map(c => {
    const label = `${c.name} — ${c.source} [${c.id}]`;
    byLabel.set(label, c.id);
    return `<option value="${esc(label)}">`;
  }).join('');
  render();
}

function render() {
  const q = $('q').value.trim().toLowerCase(), f = $('filter').value;
  const rows = data.channels.filter(c =>
      (f === 'all' || (f === 'fav' && c.favorite) || (f === 'missing' && (c.origin === 'none' || c.origin === 'manual-none')) ||
       (f === 'public' && (c.origin === 'auto' || c.origin === 'manual')))
      && (!q || c.name.toLowerCase().includes(q))).slice(0, 400);
  const names = { provider: 'Provider', auto: 'Public · auto', manual: 'Public · chosen', none: 'None', 'manual-none': 'None (chosen)' };
  $('rows').innerHTML = rows.map(c => `<tr data-id="${c.id}">
      <td class="fav">${c.favorite ? '★' : ''}</td>
      <td>${esc(c.name)}</td>
      <td><span class="pill ${c.origin}">${names[c.origin] || c.origin}</span>${c.sourceChannelName ? `<div class="muted" style="font-size:.8rem">${esc(c.sourceChannelName)} · ${esc(c.source)}</div>` : ''}</td>
      <td class="muted">${esc(c.now || '')}</td>
      <td><input class="pick" list="sourceList" placeholder="Search e.g. Sky Sports Premier League"></td>
      <td style="white-space:nowrap"><button data-act="save">Save</button> <button data-act="auto">Auto</button> <button data-act="none">None</button></td>
    </tr>`).join('') || '<tr><td></td><td class="muted" colspan="5">Nothing to show.</td></tr>';
}

$('rows').addEventListener('click', async e => {
  const act = e.target.dataset.act; if (!act) return;
  const tr = e.target.closest('tr'); const id = +tr.dataset.id;
  let sourceChannelId;
  if (act === 'auto') sourceChannelId = null;
  else if (act === 'none') sourceChannelId = '';
  else {
    const v = tr.querySelector('.pick').value;
    sourceChannelId = byLabel.get(v);
    if (!sourceChannelId) { $('msg').style.color = 'var(--bad)'; $('msg').textContent = 'Pick a channel from the list first'; return; }
  }
  $('msg').style.color = 'var(--muted)'; $('msg').textContent = 'Saving…';
  const r = await fetch('/api/epg/map?' + auth(), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ streamId: id, sourceChannelId }) });
  const res = await r.json();
  $('msg').style.color = 'var(--ok)';
  $('msg').textContent = res.origin === 'manual' || res.origin === 'auto' ? `Saved - guide now from ${res.sourceChannelName}` : res.origin === 'provider' ? 'Saved - using the provider guide' : 'Saved - no guide';
  await load();
});
$('q').addEventListener('input', () => data && render());
$('filter').addEventListener('change', () => data && render());
$('go').addEventListener('click', () => { pw = $('pw').value; try { localStorage.setItem('hubPw', pw); } catch (e) {} load(); });
$('pw').addEventListener('keydown', e => { if (e.key === 'Enter') $('go').click(); });
if (pw) load(); else $('login').hidden = false;
</script>
</body>
</html>
""";
}
