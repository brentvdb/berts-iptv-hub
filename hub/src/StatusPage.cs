namespace BertsIptvHub;

/// <summary>Neon status dashboard (same family as Bert's Capture Cast Server). {{VERSION}} is filled in at startup.</summary>
public static class StatusPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Bert's IPTV Hub</title>
<link rel="icon" href="/favicon.ico">
<style>
  :root {
    color-scheme: dark;
    --bg: #060a16; --panel: #0c1627; --panel2: #0a1322; --line: #1f3550;
    --cyan: #22e6ff; --magenta: #ff2bd6; --violet: #9b7bff;
    --ok: #7dffb0; --warn: #ffbd6b; --bad: #ff8a9a; --text: #eaf6ff; --muted: #94a9c3;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; color: var(--text); font: 15px/1.5 "Segoe UI", system-ui, sans-serif;
    background:
      radial-gradient(900px 420px at 0% -10%, rgba(34,230,255,.14), transparent 60%),
      radial-gradient(900px 420px at 100% -10%, rgba(255,43,214,.13), transparent 60%),
      var(--bg);
    min-height: 100vh;
  }
  main { max-width: 1100px; margin: 0 auto; padding: 32px 22px 60px; }
  header { display: flex; align-items: center; gap: 18px; flex-wrap: wrap; margin-bottom: 26px; }
  header img { width: 76px; height: 76px; filter: drop-shadow(0 0 14px rgba(34,230,255,.45)); }
  h1 {
    margin: 0; font-size: clamp(1.8rem, 4.5vw, 2.8rem); font-weight: 800; letter-spacing: .5px;
    background: linear-gradient(90deg, var(--cyan), var(--violet) 55%, var(--magenta));
    -webkit-background-clip: text; background-clip: text; color: transparent;
    filter: drop-shadow(0 0 10px rgba(34,230,255,.35)) drop-shadow(0 0 18px rgba(255,43,214,.25));
  }
  .sub { color: var(--muted); margin: 2px 0 0; }
  .spacer { flex: 1; }
  .pill {
    display: inline-flex; align-items: center; gap: 8px; border-radius: 999px; padding: 5px 13px;
    font-size: .8rem; font-weight: 700; letter-spacing: .08em; text-transform: uppercase;
    border: 1px solid var(--line); background: rgba(12,22,39,.8);
  }
  .version { color: var(--cyan); border-color: rgba(34,230,255,.5); box-shadow: 0 0 12px rgba(34,230,255,.25), inset 0 0 8px rgba(34,230,255,.12); text-transform: none; letter-spacing: .02em; }
  .dot { width: 9px; height: 9px; border-radius: 50%; background: currentColor; box-shadow: 0 0 10px currentColor; }
  .live { color: var(--ok); border-color: rgba(125,255,176,.45); }
  .idle { color: var(--muted); }
  .vod { color: var(--magenta); border-color: rgba(255,43,214,.5); }
  .card {
    position: relative; background: linear-gradient(180deg, var(--panel), var(--panel2));
    border: 1px solid var(--line); border-radius: 14px; padding: 20px 22px; margin-top: 16px;
  }
  .card::before {
    content: ""; position: absolute; inset: -1px; border-radius: 14px; padding: 1px; pointer-events: none;
    background: linear-gradient(120deg, rgba(34,230,255,.55), transparent 35%, transparent 65%, rgba(255,43,214,.55));
    -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
    -webkit-mask-composite: xor; mask-composite: exclude;
  }
  .label { color: var(--muted); font-size: .75rem; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; margin-bottom: 10px; }
  .now { display: flex; align-items: center; gap: 18px; }
  .now img { width: 84px; height: 84px; object-fit: contain; border-radius: 12px; background: #050912; border: 1px solid var(--line); padding: 6px; }
  .now img[hidden] { display: none; }
  .channel { font-size: clamp(1.4rem, 3.5vw, 2rem); font-weight: 700; text-shadow: 0 0 16px rgba(34,230,255,.35); }
  .meta { color: var(--muted); margin-top: 4px; }
  .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 16px; }
  .grid .card { margin-top: 0; }
  table { width: 100%; border-collapse: collapse; }
  td { padding: 7px 0; border-bottom: 1px solid rgba(31,53,80,.7); vertical-align: top; }
  tr:last-child td { border-bottom: 0; }
  td:last-child { text-align: right; color: var(--muted); }
  .empty { color: var(--muted); }
  .addr { font-family: ui-monospace, Consolas, monospace; color: var(--cyan); text-shadow: 0 0 10px rgba(34,230,255,.5); }
  .bad { color: var(--bad); }
  footer { margin-top: 28px; color: var(--muted); font-size: .85rem; text-align: center; }
  footer span { color: var(--cyan); }
</style>
</head>
<body>
<main>
  <header>
    <img src="/logo.png" alt="">
    <div>
      <h1>Bert's IPTV Hub</h1>
      <p class="sub">One IPTV account, shared with every TV in the house</p>
    </div>
    <div class="spacer"></div>
    <span class="pill version" title="Hub version">{{VERSION}}</span>
  </header>

  <section class="card">
    <div class="label">Now playing</div>
    <div class="now">
      <img id="icon" hidden alt="" onerror="this.hidden=true">
      <div>
        <div id="mode"></div>
        <div class="channel" id="channel">Connecting…</div>
        <div class="meta" id="meta"></div>
      </div>
    </div>
  </section>

  <div class="grid" style="margin-top:16px">
    <section class="card"><div class="label">Watching now</div><table id="viewers"></table></section>
    <section class="card"><div class="label">TVs connected</div><table id="tvs"></table></section>
    <section class="card"><div class="label">Hub</div><table id="server"></table></section>
  </div>

  <footer>Bert's IPTV Hub <span>{{VERSION}}</span> · TVs connect to <span class="addr" id="addr2"></span></footer>
</main>
<script>
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const rows = (el, items, empty) => {
  el.innerHTML = items.length ? items.map(([a, b]) => `<tr><td>${a}</td><td>${b}</td></tr>`).join('')
                              : `<tr><td class="empty">${empty}</td><td></td></tr>`;
};
const ago = t => {
  if (!t) return '';
  if (typeof t === 'string' && Date.now() - new Date(t) < 60000) return 'just now';
  const m = Math.round((Date.now() - new Date(t)) / 60000);
  return m < 1 ? 'just now' : m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${m % 60} min`;
};
const since = t => { const a = ago(t); return a === 'just now' ? a : a + ' ago'; };
const host = location.host;
document.getElementById('addr2').textContent = 'http://' + host;

async function refresh() {
  try {
    const s = await (await fetch('/api/status', { cache: 'no-store' })).json();
    const st = s.state;
    const pill = { live: ['live', 'Live'], vod: ['vod', st.vodKind || 'VOD'], idle: ['idle', 'Idle'] }[st.mode];
    document.getElementById('mode').innerHTML = `<span class="pill ${pill[0]}"><span class="dot"></span>${esc(pill[1])}</span>`;
    document.getElementById('channel').textContent = st.mode === 'idle' ? 'Nothing playing' : (st.name || '');
    const icon = document.getElementById('icon');
    if (st.mode === 'live' && st.icon) { if (icon.dataset.src !== st.icon) { icon.dataset.src = st.icon; icon.hidden = false; icon.src = st.icon; } } else { icon.hidden = true; icon.dataset.src = ''; }
    document.getElementById('meta').textContent = st.mode === 'idle'
      ? (st.reason ? 'Last stop: ' + st.reason.replaceAll('_', ' ') : 'Pick a channel on any TV')
      : `${st.viewers} watching · started ${since(st.startedAt)}` +
        (s.upstream ? ` · ${s.upstream.megabytesIn} MB received · ${s.upstream.reconnects} reconnects` : '');

    rows(document.getElementById('viewers'), (s.upstream?.viewers ?? []).map(v => [esc(v.who), ago(v.since)]), 'No TVs watching');
    rows(document.getElementById('tvs'), s.syncClients.map(c => [esc(c.device) + ` <span class="empty">· ${esc(c.ip)}</span>`, ago(c.since)]), 'No TVs connected');
    rows(document.getElementById('server'), [
      ['Version', `<span class="addr">${esc(s.versionDisplay)}</span>`],
      ['Provider', s.providerConfigured ? esc(s.providerHost) : '<span class="bad">Not configured</span>'],
      ['TV address', `<span class="addr">http://${esc(host)}</span>`],
      ['Uptime', ago(Date.now() - s.uptimeMinutes * 60000)],
      ['Memory', s.memoryMB + ' MB'],
      ['Channel list updated', s.updates.channels ? since(s.updates.channels) : 'not yet'],
      ['EPG updated', s.updates.epg ? since(s.updates.epg) : 'not yet'],
      ['TV guide', s.guide && s.guide.liveChannels ? `${s.guide.withGuide} of ${s.guide.liveChannels} channels · ${s.guide.fromPublic + s.guide.manual} from public guides · <a href="/epg" style="color:var(--cyan)">matching →</a>` : '<a href="/epg" style="color:var(--cyan)">matching →</a>'],
    ], '');
  } catch (e) {
    document.getElementById('channel').innerHTML = '<span class="bad">Hub not reachable</span>';
  }
}
refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
