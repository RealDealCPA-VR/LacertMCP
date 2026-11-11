namespace LacertMCP.Server.Web
{
    /// <summary>
    /// The Lacerte MCP control page served at GET /. Self-contained (no external
    /// requests). Same look and conventions as the sibling QuickBooks MCP page.
    /// Tabs: Overview (health + what to do next), Access (tailnet devices ×
    /// None / Read only / Read &amp; write), Agents (live connections + copy-paste
    /// setup), Data folders (discovered folders + a live read test), Activity
    /// (filterable timeline), Storage (where everything lives).
    /// All data comes from /api/state and is rendered with textContent, never
    /// innerHTML, because paths, device names and messages are untrusted text.
    /// </summary>
    internal static class ControlPage
    {
        public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Lacerte MCP Control</title>
<style>
  :root {
    --bg: #f4f6f9; --panel: #ffffff; --panel2: #f8fafc; --text: #1b2230; --muted: #5d6b7c; --border: #dfe4ea;
    --accent: #1f6feb; --accent-text: #ffffff; --chip: #eef2f7;
    --ok: #1a7f37; --ok-bg: #e6f4ea; --warn: #9a6700; --warn-bg: #fff4d6; --bad: #b42318; --bad-bg: #fdecea; --info: #0b5cad; --info-bg: #e8f1fd; --idle: #6b7280; --idle-bg: #eef0f3;
    --shadow: 0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.08);
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #0f1318; --panel: #171d25; --panel2: #1c232d; --text: #e6e9ee; --muted: #9aa6b5; --border: #2a3441;
      --accent: #4c8dff; --accent-text: #0b1220; --chip: #233041;
      --ok: #6fd391; --ok-bg: #12301f; --warn: #f2c46d; --warn-bg: #33270f; --bad: #f4a3a3; --bad-bg: #3a1717; --info: #8ab8ff; --info-bg: #13243d; --idle: #9aa6b5; --idle-bg: #222a35;
      --shadow: none;
    }
  }
  * { box-sizing: border-box; }
  html, body { margin: 0; }
  body { background: var(--bg); color: var(--text); font: 14.5px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
  header.top { background: var(--panel); border-bottom: 1px solid var(--border); position: sticky; top: 0; z-index: 5; }
  .wrap { max-width: 1180px; margin: 0 auto; padding: 0 16px; }
  .topline { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 0 8px; flex-wrap: wrap; }
  .brand { display: flex; align-items: center; gap: 10px; }
  .logo { width: 30px; height: 30px; border-radius: 8px; background: var(--accent); color: var(--accent-text); display: grid; place-items: center; font-weight: 700; font-size: 13px; }
  .brand h1 { font-size: 17px; margin: 0; }
  .brand p { margin: 0; color: var(--muted); font-size: 12.5px; }
  .chips { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; }
  .chip { display: inline-flex; align-items: center; gap: 6px; background: var(--chip); border-radius: 999px; padding: 2px 10px; font-size: 12.5px; max-width: 100%; overflow-wrap: anywhere; }
  .chip.ok { background: var(--ok-bg); color: var(--ok); } .chip.warn { background: var(--warn-bg); color: var(--warn); }
  .chip.bad { background: var(--bad-bg); color: var(--bad); } .chip.info { background: var(--info-bg); color: var(--info); }
  nav.tabs { display: flex; gap: 2px; overflow-x: auto; }
  nav.tabs button { border: none; background: none; color: var(--muted); padding: 10px 14px; font: inherit; font-weight: 600; cursor: pointer; border-bottom: 2px solid transparent; white-space: nowrap; border-radius: 0; }
  nav.tabs button[aria-selected=true] { color: var(--text); border-bottom-color: var(--accent); }
  nav.tabs .count { background: var(--chip); border-radius: 999px; padding: 0 7px; font-size: 11.5px; margin-left: 4px; }
  main { padding: 18px 0 64px; }
  section.tab { display: none; } section.tab.active { display: block; }
  .panel { background: var(--panel); border: 1px solid var(--border); border-radius: 12px; padding: 18px; margin-bottom: 16px; box-shadow: var(--shadow); min-width: 0; }
  .panel h2 { font-size: 15.5px; margin: 0 0 4px; }
  .panel .lead { color: var(--muted); margin: 0 0 14px; font-size: 13.5px; }
  .grid2 { display: grid; grid-template-columns: 1.4fr 1fr; gap: 16px; }
  .grid2 > * { min-width: 0; }
  .tiles { display: grid; grid-template-columns: repeat(4, 1fr); gap: 12px; margin-bottom: 16px; }
  .tile { background: var(--panel); border: 1px solid var(--border); border-radius: 12px; padding: 14px; box-shadow: var(--shadow); }
  .tile .k { color: var(--muted); font-size: 12.5px; }
  .tile .v { font-size: 24px; font-weight: 700; line-height: 1.2; margin-top: 2px; }
  .tile .s { color: var(--muted); font-size: 12.5px; }
  .hero { display: flex; gap: 14px; align-items: flex-start; border-radius: 12px; padding: 16px; margin-bottom: 14px; }
  .hero .icon { width: 40px; height: 40px; border-radius: 50%; display: grid; place-items: center; font-weight: 800; font-size: 15px; flex: none; }
  .hero h3 { margin: 0; font-size: 17px; }
  .hero p { margin: 4px 0 0; }
  .tone-ok { background: var(--ok-bg); } .tone-ok .icon { background: var(--ok); color: var(--panel); }
  .tone-warn { background: var(--warn-bg); } .tone-warn .icon { background: var(--warn); color: var(--panel); }
  .tone-bad { background: var(--bad-bg); } .tone-bad .icon { background: var(--bad); color: var(--panel); }
  .check { display: grid; grid-template-columns: 14px 1fr; gap: 10px; padding: 9px 0; border-bottom: 1px solid var(--border); }
  .check:last-child { border-bottom: none; }
  .check .n { font-weight: 600; }
  .check .d { color: var(--muted); font-size: 13px; overflow-wrap: anywhere; }
  .check .fix { font-size: 13px; margin-top: 2px; overflow-wrap: anywhere; }
  .dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; margin-top: 6px; background: var(--idle); }
  .dot.ok { background: var(--ok); } .dot.warn { background: var(--warn); } .dot.bad { background: var(--bad); } .dot.info { background: var(--info); }
  button { font: inherit; border-radius: 8px; border: 1px solid var(--border); background: var(--panel); color: var(--text); padding: 7px 14px; cursor: pointer; }
  button.primary { background: var(--accent); color: var(--accent-text); border-color: var(--accent); font-weight: 600; }
  button.danger { color: var(--bad); border-color: var(--bad); background: var(--panel); }
  button:disabled { opacity: .5; cursor: default; }
  button:focus-visible, select:focus, input:focus { outline: 2px solid var(--accent); outline-offset: 1px; }
  select, input[type=text] { padding: 7px 10px; border: 1px solid var(--border); border-radius: 8px; background: var(--bg); color: var(--text); font: inherit; }
  .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .banner { border-radius: 9px; padding: 10px 12px; margin: 0 0 12px; font-size: 13.5px; overflow-wrap: anywhere; }
  .banner.ok { background: var(--ok-bg); color: var(--ok); } .banner.warn { background: var(--warn-bg); color: var(--warn); }
  .banner.err { background: var(--bad-bg); color: var(--bad); } .banner.info { background: var(--info-bg); color: var(--info); }
  .hidden { display: none !important; }
  code, pre { font: 12.5px ui-monospace, Consolas, monospace; background: var(--chip); border-radius: 6px; }
  code { padding: 1px 6px; overflow-wrap: anywhere; }
  pre { padding: 10px 12px; overflow-x: auto; margin: 6px 0 0; white-space: pre; }
  .muted { color: var(--muted); } .small { font-size: 12.5px; }
  .empty { color: var(--muted); font-style: italic; }
  table { width: 100%; border-collapse: collapse; }
  th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); vertical-align: middle; overflow-wrap: anywhere; }
  th { font-size: 12px; color: var(--muted); font-weight: 700; text-transform: uppercase; letter-spacing: .03em; }
  .scroll { overflow-x: auto; }
  .seg { display: inline-flex; border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
  .seg button { border: none; border-radius: 0; padding: 6px 12px; background: var(--panel2); }
  .seg button + button { border-left: 1px solid var(--border); }
  .seg button[aria-pressed=true].none { background: var(--idle-bg); font-weight: 700; }
  .seg button[aria-pressed=true].read { background: var(--info-bg); color: var(--info); font-weight: 700; }
  .seg button[aria-pressed=true].write { background: var(--ok-bg); color: var(--ok); font-weight: 700; }
  .ev { display: grid; grid-template-columns: 120px 14px 1fr; gap: 10px; padding: 8px 0; border-bottom: 1px solid var(--border); }
  .ev .when { color: var(--muted); font-size: 12px; padding-top: 2px; }
  .ev .msg { overflow-wrap: anywhere; }
  .ev .det { color: var(--muted); font-size: 12.5px; overflow-wrap: anywhere; }
  .lvl { width: 12px; height: 12px; border-radius: 50%; margin-top: 5px; background: var(--idle); }
  .lvl.success { background: var(--ok); } .lvl.warn { background: var(--warn); } .lvl.error { background: var(--bad); } .lvl.info { background: var(--info); }
  .filters { display: flex; gap: 6px; flex-wrap: wrap; margin-bottom: 10px; }
  .filters button[aria-pressed=true] { background: var(--accent); color: var(--accent-text); border-color: var(--accent); }
  .kv { display: grid; grid-template-columns: 200px 1fr; gap: 6px 14px; }
  .kv > div:nth-child(odd) { color: var(--muted); }
  .kv > div { overflow-wrap: anywhere; }
  @media (max-width: 860px) {
    .grid2 { grid-template-columns: 1fr; }
    .tiles { grid-template-columns: repeat(2, 1fr); }
    .kv { grid-template-columns: 1fr; }
    .ev { grid-template-columns: 1fr; gap: 2px; } .ev .lvl { display: none; }
  }
</style>
</head>
<body>
<header class="top">
  <div class="wrap">
    <div class="topline">
      <div class="brand"><div class="logo">L</div><div><h1>Lacerte MCP</h1><p id="sub">Loading…</p></div></div>
      <div class="chips" id="chips"></div>
    </div>
    <nav class="tabs" role="tablist" id="tabs">
      <button role="tab" data-tab="overview" aria-selected="true">Overview</button>
      <button role="tab" data-tab="access" aria-selected="false">Access <span class="count" id="cAccess">0</span></button>
      <button role="tab" data-tab="agents" aria-selected="false">Agents <span class="count" id="cAgents">0</span></button>
      <button role="tab" data-tab="folders" aria-selected="false">Data folders</button>
      <button role="tab" data-tab="activity" aria-selected="false">Activity</button>
      <button role="tab" data-tab="storage" aria-selected="false">Storage</button>
    </nav>
  </div>
</header>
<main class="wrap">
  <div id="flash" class="banner hidden" role="status"></div>

  <section class="tab active" id="tab-overview">
    <div id="hero" class="hero tone-ok"><div class="icon" id="heroIcon">✓</div><div><h3 id="heroTitle">Checking…</h3><p id="heroText"></p></div></div>
    <div class="tiles">
      <div class="tile"><div class="k">Agents connected</div><div class="v" id="tConn">0</div><div class="s">right now</div></div>
      <div class="tile"><div class="k">Devices with access</div><div class="v" id="tDev">0</div><div class="s">besides this PC</div></div>
      <div class="tile"><div class="k">Verified writes today</div><div class="v" id="tWrites">0</div><div class="s" id="tCalls">0 tool calls</div></div>
      <div class="tile"><div class="k">Problems today</div><div class="v" id="tProb">0</div><div class="s">errors in Activity</div></div>
    </div>
    <div class="grid2">
      <div class="panel">
        <h2>Health</h2>
        <p class="lead">Everything the Lacerte MCP needs to read and write your returns accurately.</p>
        <div id="checks"></div>
        <div class="row" style="margin-top:12px">
          <button class="primary" id="btnSignin">Sign in to Lacerte</button>
          <button id="btnReconnect">Reconnect</button>
        </div>
        <p class="hint muted small">Sign in opens Intuit's sign-in window on the Lacerte PC (<span id="pcName"></span>). The login lasts 24 hours.</p>
      </div>
      <div class="panel">
        <h2>Recent activity</h2>
        <p class="lead">The latest tool calls, connections and changes.</p>
        <div id="recent"></div>
      </div>
    </div>
  </section>

  <section class="tab" id="tab-access">
    <div class="panel">
      <h2>Who can use Lacerte</h2>
      <p class="lead">This PC always has full access. Other tailnet devices can't use the Lacerte MCP until you give them access here. <b>Read only</b> devices can look at returns but every write is refused. Changes apply on the next tool call; removing access also disconnects that device's agents.</p>
      <div class="scroll"><table><thead><tr><th>Device</th><th>Status</th><th>Access</th></tr></thead><tbody id="devices"></tbody></table></div>
      <div id="orphans"></div>
    </div>
  </section>

  <section class="tab" id="tab-agents">
    <div class="panel">
      <h2>Connected agents</h2>
      <p class="lead">Each agent connection has its own client file (the data folder, year and module it picked with lacerte_set_context), so two agents can't redirect each other's writes. Idle connections close after 60 minutes.</p>
      <div class="scroll"><table><thead><tr><th>Device</th><th>Agent</th><th>Working in</th><th>Last active</th><th></th></tr></thead><tbody id="conns"></tbody></table></div>
    </div>
    <div class="panel">
      <h2>Connect an agent</h2>
      <p class="lead">Give the device access on the Access tab first. Then use one of these.</p>
      <div class="kv">
        <div>Server address</div><div id="urls"></div>
        <div>Claude Code</div><div><pre id="cfgCode"></pre></div>
        <div>Claude Desktop / Cursor / any MCP client (JSON)</div><div><pre id="cfgJson"></pre></div>
      </div>
    </div>
  </section>

  <section class="tab" id="tab-folders">
    <div class="panel">
      <h2>Lacerte data folders</h2>
      <p class="lead">Folders found under <code id="root"></code>. <b>Test read</b> reads the client list from that folder through the same path agents use, so you can confirm the data is reachable before relying on it.</p>
      <div class="scroll"><table><thead><tr><th>Folder</th><th>Tax year</th><th>Return type</th><th></th></tr></thead><tbody id="folders"></tbody></table></div>
    </div>
  </section>

  <section class="tab" id="tab-activity">
    <div class="panel">
      <h2>Activity</h2>
      <p class="lead">Tool calls (tool, device, outcome, and for writes the client and field), connections, access changes, and problems. Kept on this PC.</p>
      <div class="filters" id="filters"></div>
      <div id="timeline"></div>
    </div>
  </section>

  <section class="tab" id="tab-storage">
    <div class="panel">
      <h2>Where everything is kept</h2>
      <p class="lead">All on the Lacerte PC. Nothing is sent anywhere else.</p>
      <div class="kv" id="storage"></div>
    </div>
    <div class="panel">
      <h2>What this server can and can't do</h2>
      <p class="lead">Reads come straight from Lacerte's ODBC driver. Every field write is read back from Lacerte and reported as verified only when the stored value matches. A mismatch is reported as an error (WriteNotVerified) with the value that is actually on the return. Read-only devices can never write. E-filing, printing and the Lacerte UI itself stay in Lacerte.</p>
    </div>
  </section>
</main>
<script>
(function () {
  "use strict";
  var state = null;
  var filter = "all";
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = String(text); return e; }
  function clear(n) { while (n.firstChild) n.removeChild(n.firstChild); }
  function ago(iso) {
    if (!iso) return "";
    var s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
    if (s < 60) return "just now";
    if (s < 3600) return Math.floor(s / 60) + " min ago";
    if (s < 86400) return Math.floor(s / 3600) + " h ago";
    return new Date(iso).toLocaleString();
  }
  function when(iso) { var d = new Date(iso); return d.toLocaleDateString() === new Date().toLocaleDateString() ? d.toLocaleTimeString() : d.toLocaleString(); }
  function flash(kind, text) { var f = $("flash"); f.className = "banner " + kind; f.textContent = text; setTimeout(function () { f.className = "banner hidden"; }, 9000); }

  function post(path, body) {
    return fetch(path, { method: "POST", headers: { "Content-Type": "application/json", "X-Lacerte-Admin": "1" }, body: JSON.stringify(body || {}) })
      .then(function (r) { return r.json().then(function (j) { if (!r.ok) throw new Error(j.error || j.message || ("HTTP " + r.status)); return j; }); });
  }

  function load() {
    return fetch("/api/state", { cache: "no-store" })
      .then(function (r) { if (!r.ok) return r.json().then(function (j) { throw new Error(j.error || ("HTTP " + r.status)); }); return r.json(); })
      .then(function (s) { state = s; render(); })
      .catch(function (e) { flash("err", "Couldn't load status: " + e.message); });
  }

  function render() {
    var s = state;
    $("sub").textContent = "on " + s.pc + " · build " + s.version;
    $("pcName").textContent = s.pc;
    var chips = $("chips"); clear(chips);
    chips.appendChild(el("span", "chip " + (s.mode === "live" ? "ok" : "warn"), s.mode === "live" ? "Live data" : "Simulation"));
    chips.appendChild(el("span", "chip " + (s.signedIn ? "ok" : "bad"), s.signedIn ? "Signed in to Lacerte" : "Not signed in"));
    $("cAccess").textContent = s.counts.devicesWithAccess;
    $("cAgents").textContent = s.counts.connections;
    renderOverview(s); renderAccess(s); renderAgents(s); renderFolders(s); renderActivity(s); renderStorage(s);
  }

  function renderOverview(s) {
    var bad = s.health.filter(function (c) { return c.status === "bad"; });
    var warn = s.health.filter(function (c) { return c.status === "warn"; });
    var hero = $("hero");
    if (bad.length) {
      hero.className = "hero tone-bad"; $("heroIcon").textContent = "!";
      $("heroTitle").textContent = bad.length === 1 ? bad[0].name + " needs attention" : bad.length + " problems need attention";
      $("heroText").textContent = bad[0].fix || bad[0].summary;
    } else if (warn.length) {
      hero.className = "hero tone-warn"; $("heroIcon").textContent = "!";
      $("heroTitle").textContent = "Working, with " + warn.length + " warning" + (warn.length > 1 ? "s" : "");
      $("heroText").textContent = warn[0].summary;
    } else {
      hero.className = "hero tone-ok"; $("heroIcon").textContent = "✓";
      $("heroTitle").textContent = "Ready";
      $("heroText").textContent = "Agents on authorized devices can read and write your Lacerte returns.";
    }
    $("tConn").textContent = s.counts.connections;
    $("tDev").textContent = s.counts.devicesWithAccess;
    $("tWrites").textContent = s.counts.writesToday;
    $("tCalls").textContent = s.counts.toolCallsToday + " tool calls";
    $("tProb").textContent = s.counts.problemsToday;
    var box = $("checks"); clear(box);
    s.health.forEach(function (c) {
      var row = el("div", "check");
      row.appendChild(el("span", "dot " + c.status));
      var body = el("div");
      body.appendChild(el("div", "n", c.name));
      body.appendChild(el("div", "d", c.summary));
      if (c.fix && c.status !== "ok") body.appendChild(el("div", "fix", c.fix));
      row.appendChild(body); box.appendChild(row);
    });
    var rec = $("recent"); clear(rec);
    var items = s.activity.slice(0, 8);
    if (!items.length) rec.appendChild(el("p", "empty", "Nothing yet."));
    items.forEach(function (e) { rec.appendChild(eventRow(e)); });
  }

  function eventRow(e) {
    var r = el("div", "ev");
    r.appendChild(el("div", "when", when(e.at)));
    r.appendChild(el("span", "lvl " + e.level));
    var b = el("div");
    b.appendChild(el("div", "msg", e.message + (e.device ? " · " + e.device : "")));
    if (e.detail) b.appendChild(el("div", "det", e.detail));
    r.appendChild(b);
    return r;
  }

  function levelControl(stableId, name, level) {
    var seg = el("div", "seg");
    [["none", "No access"], ["read", "Read only"], ["write", "Read & write"]].forEach(function (o) {
      var b = el("button", o[0], o[1]);
      b.setAttribute("aria-pressed", String(level === o[0]));
      b.addEventListener("click", function () {
        if (level === o[0]) return;
        if (o[0] === "write" && !confirm("Let agents on " + name + " change data on your Lacerte returns?")) return;
        post("/api/access", { stableId: stableId, name: name, level: o[0] })
          .then(function () { flash("ok", name + ": " + o[1].toLowerCase()); load(); })
          .catch(function (e) { flash("err", e.message); });
      });
      seg.appendChild(b);
    });
    return seg;
  }

  function renderAccess(s) {
    var tb = $("devices"); clear(tb);
    var me = el("tr");
    me.appendChild(el("td", null, s.pc + " (this PC)"));
    me.appendChild(el("td", null, "Online"));
    me.appendChild(el("td", null, "Full access, always"));
    tb.appendChild(me);
    if (!s.devices.length) {
      var tr = el("tr"); var td = el("td", "empty", s.tailscaleError ? "Tailscale problem: " + s.tailscaleError : "No other tailnet devices found."); td.colSpan = 3; tr.appendChild(td); tb.appendChild(tr);
    }
    s.devices.forEach(function (d) {
      var tr = el("tr");
      var n = el("td"); n.appendChild(el("div", null, d.name)); n.appendChild(el("div", "muted small", (d.os || "") + (d.address ? " · " + d.address : "") + (d.sameOwner ? "" : " · different Tailscale login")));
      tr.appendChild(n);
      tr.appendChild(el("td", null, d.online ? "Online" : "Offline"));
      var c = el("td"); c.appendChild(levelControl(d.stableId, d.name, d.level)); tr.appendChild(c);
      tb.appendChild(tr);
    });
    var o = $("orphans"); clear(o);
    if (s.orphanGrants.length) {
      o.appendChild(el("p", "muted small", "Grants for devices no longer on the tailnet:"));
      s.orphanGrants.forEach(function (g) {
        var r = el("div", "row"); r.appendChild(el("span", null, g.name + " (" + g.level + ")"));
        var b = el("button", "danger", "Remove");
        b.addEventListener("click", function () { post("/api/access", { stableId: g.stableId, name: g.name, level: "none" }).then(load).catch(function (e) { flash("err", e.message); }); });
        r.appendChild(b); o.appendChild(r);
      });
    }
  }

  function renderAgents(s) {
    var tb = $("conns"); clear(tb);
    if (!s.connections.length) { var tr = el("tr"); var td = el("td", "empty", "No agents connected."); td.colSpan = 5; tr.appendChild(td); tb.appendChild(tr); }
    s.connections.forEach(function (c) {
      var tr = el("tr");
      tr.appendChild(el("td", null, c.device + (c.readOnly ? " (read only)" : "")));
      tr.appendChild(el("td", null, c.client));
      tr.appendChild(el("td", null, c.context ? c.context.taxYear + " " + c.context.module + " · " + c.context.dataPath : "No client file chosen yet"));
      tr.appendChild(el("td", null, c.busy ? "Working…" : ago(c.lastSeen)));
      var td = el("td"); var b = el("button", "danger", "Disconnect");
      b.addEventListener("click", function () { post("/api/disconnect", { sessionId: c.id }).then(load).catch(function (e) { flash("err", e.message); }); });
      td.appendChild(b); tr.appendChild(td); tb.appendChild(tr);
    });
    var url = s.mcpUrls[0] || s.localUrl;
    var u = $("urls"); clear(u);
    (s.mcpUrls.length ? s.mcpUrls : [s.localUrl]).forEach(function (x) { u.appendChild(el("code", null, x)); u.appendChild(document.createTextNode(" ")); });
    $("cfgCode").textContent = "claude mcp add --transport http lacerte " + url;
    $("cfgJson").textContent = JSON.stringify({ mcpServers: { lacerte: { type: "http", url: url } } }, null, 2);
  }

  function renderFolders(s) {
    $("root").textContent = s.dataRoot;
    var tb = $("folders"); clear(tb);
    if (!s.dataFolders.length) { var tr = el("tr"); var td = el("td", "empty", "No Lacerte data folders found here. If your data is on a file server, set LACERTE_DATA_ROOT for the server and restart it."); td.colSpan = 4; tr.appendChild(td); tb.appendChild(tr); }
    s.dataFolders.forEach(function (f) {
      var tr = el("tr");
      tr.appendChild(el("td", null, f.path));
      tr.appendChild(el("td", null, f.taxYear));
      tr.appendChild(el("td", null, f.moduleName));
      var td = el("td"); var b = el("button", null, "Test read"); var out = el("div", "small muted");
      b.addEventListener("click", function () {
        b.disabled = true; out.textContent = "Reading…";
        post("/api/test-folder", { path: f.path, taxYear: f.taxYear, module: f.module })
          .then(function (r) { out.textContent = r.message + (r.remediation ? " " + r.remediation : ""); out.style.color = r.ok ? "var(--ok)" : "var(--bad)"; })
          .catch(function (e) { out.textContent = e.message; })
          .then(function () { b.disabled = false; });
      });
      td.appendChild(b); td.appendChild(out); tr.appendChild(td); tb.appendChild(tr);
    });
  }

  function renderActivity(s) {
    var fl = $("filters"); clear(fl);
    [["all", "All"], ["write", "Writes"], ["tool", "Reads"], ["error", "Problems"], ["access", "Access"], ["connection", "Connections"], ["system", "System"]].forEach(function (f) {
      var b = el("button", null, f[1]); b.setAttribute("aria-pressed", String(filter === f[0]));
      b.addEventListener("click", function () { filter = f[0]; renderActivity(state); });
      fl.appendChild(b);
    });
    var t = $("timeline"); clear(t);
    var items = s.activity.filter(function (e) {
      if (filter === "all") return true;
      if (filter === "error") return e.level === "error" || e.level === "warn";
      return e.category === filter || (filter === "system" && e.category === "security");
    });
    if (!items.length) t.appendChild(el("p", "empty", "Nothing here yet."));
    items.forEach(function (e) { t.appendChild(eventRow(e)); });
  }

  function renderStorage(s) {
    var k = $("storage"); clear(k);
    [["Device access list", s.storage.accessFile], ["Activity history", s.storage.activityFile], ["Lacerte app ID", s.storage.appIdStore], ["Write replay protection", s.storage.idempotencyCache], ["Debug wire log", s.storage.debugLog || "Off (set LACERTE_DEBUG_WIRE=1 to turn on)"], ["Network", "Only this computer and your tailnet" + (s.mcpUrls.length ? " (" + s.mcpUrls.join(", ") + ")" : "") + ". Not reachable from your local network or the internet."]]
      .forEach(function (p) { k.appendChild(el("div", null, p[0])); k.appendChild(el("div", null, p[1] || "")); });
  }

  document.querySelectorAll("nav.tabs button").forEach(function (b) {
    b.addEventListener("click", function () {
      document.querySelectorAll("nav.tabs button").forEach(function (x) { x.setAttribute("aria-selected", String(x === b)); });
      document.querySelectorAll("section.tab").forEach(function (x) { x.classList.toggle("active", x.id === "tab-" + b.dataset.tab); });
    });
  });
  $("btnSignin").addEventListener("click", function () {
    post("/api/signin").then(function (r) { flash("info", r.message); }).catch(function (e) { flash("err", e.message); });
  });
  $("btnReconnect").addEventListener("click", function () {
    post("/api/reconnect").then(function (r) { flash(r.ok ? "ok" : "err", r.message || (r.ok ? "Connected." : "Failed.")); load(); }).catch(function (e) { flash("err", e.message); });
  });
  load();
  setInterval(function () { if (!document.hidden) load(); }, 5000);
})();
</script>
</body>
</html>
""";
    }
}
