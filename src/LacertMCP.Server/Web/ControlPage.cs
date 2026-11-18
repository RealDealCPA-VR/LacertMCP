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
  .cmdrow { display: flex; gap: 8px; align-items: stretch; margin: 6px 0 10px; }
  .cmdrow .lbl { color: var(--muted); font-size: 12.5px; margin-top: 8px; }
  .cmdrow code.cmd { flex: 1; min-width: 0; display: block; background: var(--panel2); border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; font: 13px/1.45 ui-monospace, Consolas, monospace; overflow-wrap: anywhere; user-select: all; }
  ol.steps { margin: 4px 0 8px 18px; padding: 0; } ol.steps li { margin: 3px 0; }
  .badge { display: inline-block; border-radius: 999px; padding: 0 8px; font-size: 11.5px; font-weight: 600; background: var(--warn-bg); color: var(--warn); margin-left: 6px; }
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
  .panel h3 { font-size: 14.5px; margin: 20px 0 6px; }
  table.wide { min-width: 760px; }
  .pline { border: 1px solid var(--border); border-left: 4px solid var(--border); border-radius: 10px; padding: 12px 14px; margin: 10px 0; background: var(--panel2); }
  .pline.dec-Approved { border-left-color: var(--ok); }
  .pline.dec-Rejected { border-left-color: var(--bad); }
  .pline.invalid { border-color: var(--bad); }
  .pline .lh { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .pline .lid { font: 700 13px ui-monospace, Consolas, monospace; }
  .pline .tgt { font: 13px ui-monospace, Consolas, monospace; overflow-wrap: anywhere; }
  .pline .val { font-weight: 700; font-size: 15px; margin-top: 4px; overflow-wrap: anywhere; }
  .meta { display: grid; grid-template-columns: 120px 1fr; gap: 3px 12px; font-size: 13px; margin-top: 8px; }
  .meta > div { overflow-wrap: anywhere; }
  .meta > div:nth-child(odd) { color: var(--muted); }
  .ctrl { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; margin-top: 10px; }
  .ctrl input[type=text] { flex: 1; min-width: 200px; }
  .excerpt { font-style: italic; }
  .t-ok { color: var(--ok); } .t-warn { color: var(--warn); } .t-bad { color: var(--bad); font-weight: 600; }
  .confirm { border: 2px solid var(--bad); border-radius: 10px; padding: 12px 14px; background: var(--bad-bg); margin-top: 10px; }
  .confirm p { margin: 0 0 8px; }
  .pact { border-top: 1px solid var(--border); margin-top: 18px; padding-top: 6px; }
  ul.blockers { margin: 6px 0 0 18px; padding: 0; }
  details summary { cursor: pointer; color: var(--muted); margin-top: 16px; }
  @media (max-width: 860px) {
    .meta { grid-template-columns: 1fr; }
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
      <button role="tab" data-tab="proposals" aria-selected="false" title="Proposals waiting for your review">Proposals <span class="count" id="cProps">0</span></button>
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

  <section class="tab" id="tab-proposals">
    <div class="banner warn" id="propPrivacy">Proposals contain taxpayer data: amounts, descriptions and excerpts from the client's documents. Review them on a screen only you can see.</div>
    <div class="panel" id="propListPanel">
      <h2>Proposals</h2>
      <p class="lead">Entries an agent prepared for a client's return, with where each value came from. Nothing is written to Lacerte until you approve a proposal here and commit it. Decide every line, then approve the proposal; or send it back to the agent with a note.</p>
      <div class="row" style="margin-bottom:10px">
        <select id="propStatus" aria-label="Status">
          <option value="">All statuses</option>
          <option value="Submitted">Waiting for review</option>
          <option value="Approved">Approved, not committed</option>
          <option value="PartiallyCommitted">Partly committed</option>
          <option value="CommitFailed">Commit failed</option>
          <option value="Committed">Committed</option>
          <option value="ReturnedForChanges">Returned for changes</option>
          <option value="Draft">Draft</option>
          <option value="Rejected">Rejected</option>
        </select>
        <input type="text" id="propClient" placeholder="Client number" maxlength="64" aria-label="Client number">
        <button id="propRefresh">Refresh</button>
      </div>
      <div id="propListMsg"></div>
      <div class="scroll"><table class="wide"><thead><tr><th>Client</th><th>Title</th><th>Status</th><th>Version</th><th>Lines</th><th>Prepared by</th><th>Updated</th><th></th></tr></thead><tbody id="propRows"></tbody></table></div>
      <p class="muted small">Kept on this PC in <code id="propFolder"></code>. The command line can review them too: <code>lacerte-mcp proposal show &lt;id&gt;</code>.</p>
    </div>
    <div class="panel hidden" id="propDetail">
      <div class="row" style="justify-content:space-between"><h2 id="pdTitle">Proposal</h2><button id="pdClose">Back to the list</button></div>
      <div id="pdBody"></div>
    </div>
  </section>

  <section class="tab" id="tab-access">
    <div class="panel">
      <h2>Who can use Lacerte</h2>
      <p class="lead">This PC always has full access. Other tailnet devices can't use the Lacerte MCP until you give them access here. <b>Read only</b> devices can look at returns but every write is refused. Changes apply on the next tool call; removing access also disconnects that device's agents.</p>
      <p class="muted small">Adding a workstation? Run the one-line setup from the <b>Agents</b> tab on it first; it then shows here as <b>Ran setup</b>.</p>
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
    <div class="panel" id="easySetup">
      <h2>Set up a workstation (easy way)</h2>
      <p class="lead">On the workstation (Windows, with Tailscale connected), open <b>PowerShell</b> and paste this line. It adds the Lacerte MCP to Claude Desktop, Claude Code and Codex on that PC. It needs no admin rights and no extra software.</p>
      <div id="setupCmds"></div>
      <ol class="steps">
        <li>Run the line on the workstation.</li>
        <li>Come back here, open the <b>Access</b> tab, and choose <b>Read only</b> or <b>Read &amp; write</b> for that workstation. It's marked <b>Ran setup</b>.</li>
        <li>On the workstation, quit Claude Desktop from the tray icon and open it again.</li>
      </ol>
      <p class="muted small">Run the same line again to update. Agents on this PC need no setup: use the local address below.</p>
    </div>
    <div class="panel">
      <h2>Manual setup</h2>
      <p class="lead">For other MCP apps, or if you'd rather edit settings yourself. Give the device access on the Access tab first.</p>
      <div class="kv">
        <div>Server address</div><div id="urls"></div>
        <div>Claude Code</div><div><pre id="cfgCode"></pre></div>
        <div>Cursor, VS Code and other apps that connect over HTTP (JSON). Claude Desktop can't: use the easy setup above.</div><div><pre id="cfgJson"></pre></div>
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
  var testResults = {};
  var lastSetupKey = null;
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
    $("cProps").textContent = s.proposals ? s.proposals.awaitingReview : 0;
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
          .then(function (r) { flash(r.note ? "info" : "ok", name + ": " + o[1].toLowerCase() + (r.note ? ". " + r.note : "")); load(); })
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
      var n = el("td"); var nm = el("div", null, d.name);
      if (d.setupAt && d.level === "none") { var bd = el("span", "badge", "Ran setup " + ago(d.setupAt)); bd.title = "This workstation ran the setup line and is waiting for access."; nm.appendChild(bd); }
      n.appendChild(nm); n.appendChild(el("div", "muted small", (d.os || "") + (d.address ? " · " + d.address : "") + (d.tagged ? " · tagged device" : d.sameOwner ? "" : " · different Tailscale login")));
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

  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
    // Plain http over the tailnet isn't a "secure context", so the Clipboard API is missing there.
    var t = document.createElement("textarea"); t.value = text; t.setAttribute("readonly", ""); t.style.position = "fixed"; t.style.opacity = "0";
    document.body.appendChild(t); t.select();
    var ok = false; try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
    document.body.removeChild(t);
    return ok ? Promise.resolve() : Promise.reject(new Error("Copy blocked by the browser; select the line and press Ctrl+C."));
  }

  function cmdRow(label, text) {
    var wrap = el("div");
    wrap.appendChild(el("div", "lbl muted small", label));
    var r = el("div", "cmdrow");
    r.appendChild(el("code", "cmd", text));
    var b = el("button", null, "Copy");
    b.addEventListener("click", function () { copyText(text).then(function () { flash("ok", "Copied. Paste it into PowerShell on the workstation."); }).catch(function (e) { flash("err", e.message); }); });
    r.appendChild(b); wrap.appendChild(r);
    return wrap;
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
    // The addresses and setup lines rarely change: rebuild them only when they do,
    // so the 5 s refresh doesn't wipe out a text selection the operator is copying.
    var setupKey = JSON.stringify([s.mcpUrls, s.localUrl, s.setupUrls]);
    if (setupKey === lastSetupKey) return;
    lastSetupKey = setupKey;
    var u = $("urls"); clear(u);
    (s.mcpUrls.length ? s.mcpUrls : [s.localUrl]).forEach(function (x) { u.appendChild(el("code", null, x)); u.appendChild(document.createTextNode(" ")); });
    var sc = $("setupCmds"); clear(sc);
    if (!s.setupUrls || !s.setupUrls.length) {
      sc.appendChild(el("p", "fix", "Workstations can't reach this PC yet: Tailscale isn't connected here. Fix it (see Overview) and the setup line appears."));
    } else {
      var su = s.setupUrls[0];
      sc.appendChild(cmdRow("Set up / update", "irm " + su + " | iex"));
      sc.appendChild(cmdRow("Remove", "$env:LACERTE_MCP_UNINSTALL='1'; irm " + su + " | iex"));
    }
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
      var key = f.path + "|" + f.taxYear + "|" + f.module;
      showTest(b, out, testResults[key]);
      b.addEventListener("click", function () {
        // Results live in testResults, not in the row: the 5 s refresh rebuilds this table.
        testResults[key] = { busy: true, text: "Reading…" };
        showTest(b, out, testResults[key]);
        post("/api/test-folder", { path: f.path, taxYear: f.taxYear, module: f.module })
          .then(function (r) { testResults[key] = { text: r.message + (r.remediation ? " " + r.remediation : ""), color: r.ok ? "var(--ok)" : "var(--bad)" }; })
          .catch(function (e) { testResults[key] = { text: e.message, color: "var(--bad)" }; })
          .then(function () { if (state) renderFolders(state); });
      });
      td.appendChild(b); td.appendChild(out); tr.appendChild(td); tb.appendChild(tr);
    });
  }

  function showTest(b, out, r) {
    b.disabled = !!(r && r.busy);
    out.textContent = r ? r.text : "";
    out.style.color = r && r.color ? r.color : "";
  }

  function renderActivity(s) {
    var fl = $("filters"); clear(fl);
    [["all", "All"], ["write", "Writes"], ["tool", "Reads"], ["proposal", "Reviews"], ["error", "Problems"], ["access", "Access"], ["connection", "Connections"], ["system", "System"]].forEach(function (f) {
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

  // ---- Proposals ------------------------------------------------------------
  // The open proposal is re-rendered only when its id / version / status /
  // update time changes (or after the operator's own action), so the 5 s
  // refresh never wipes a note being typed. Typed notes live in prop.notes,
  // keyed by proposal + input, and are put back on every render.
  var activeTab = "overview";
  var prop = { list: null, listError: null, openId: null, detail: null, renderedKey: null, notes: {}, confirm: false, busy: null, report: null, err: null };
  var STATUS_LABEL = { Draft: "Draft (agent still working)", Submitted: "Waiting for review", Approved: "Approved, not committed", Rejected: "Rejected", ReturnedForChanges: "Returned for changes", Committing: "Writing to Lacerte", Committed: "Committed", PartiallyCommitted: "Partly committed", CommitFailed: "Commit failed" };
  var STATUS_TONE = { Submitted: "info", Approved: "ok", Committed: "ok", ReturnedForChanges: "warn", PartiallyCommitted: "bad", CommitFailed: "bad", Committing: "info" };
  var COMMIT_LABEL = { Running: "Was being written when the server stopped", Succeeded: "Written and verified", SucceededUnverified: "Written, not verified", Failed: "Not written", Uncertain: "May already be on the return", Skipped: "Skipped (the commit stopped earlier)" };
  var COMMIT_TONE = { Succeeded: "t-ok", SucceededUnverified: "t-warn", Failed: "t-bad", Uncertain: "t-bad", Skipped: "t-warn", Running: "t-warn" };

  function api(method, path, body) {
    var opts = { method: method, cache: "no-store", headers: { "X-Lacerte-Admin": "1" } };
    if (body !== undefined) { opts.headers["Content-Type"] = "application/json"; opts.body = JSON.stringify(body); }
    return fetch(path, opts).then(function (r) {
      return r.json().catch(function () { return {}; }).then(function (j) {
        if (!r.ok) { var e = new Error(j.error || ("HTTP " + r.status)); e.data = j; throw e; }
        return j;
      });
    });
  }
  function statusChip(st) { return el("span", "chip " + (STATUS_TONE[st] || ""), STATUS_LABEL[st] || st); }
  function plural(n, one, many) { return n + " " + (n === 1 ? one : (many || one + "s")); }
  function money(a) { return a == null ? "(no amount)" : Number(a).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 4 }); }
  function describeValue(v) {
    if (v === null || v === undefined) return "(blank)";
    if (typeof v === "string") return v === "" ? "(empty text)" : v;
    return JSON.stringify(v);
  }
  function targetText(l) {
    var t = l.target || {};
    if (l.kind === "Field") {
      return "Table " + t.table + " · field " + t.field + (t.subClientId != null ? " · sub-client " + t.subClientId : "") + (t.stateAbbrev ? " · state " + t.stateAbbrev : "");
    }
    var d = t.detail || {};
    var parts = ["Series " + d.series, "Code " + d.code];
    if (d.prefix) parts.push("Prefix " + d.prefix);
    if (d.suffix) parts.push("Suffix " + d.suffix);
    if (d.stateAbbrev) parts.push("State " + d.stateAbbrev);
    if (d.cityAbbr) parts.push("City " + d.cityAbbr);
    if (d.tsj) parts.push("TSJ " + d.tsj);
    if (d.source) parts.push("Source flag " + d.source);
    return "Detail row · " + parts.join(" · ");
  }
  function sourceText(s) {
    if (!s) return "(none)";
    var parts = [s.document || "(no document)"];
    if (s.page != null) parts.push("page " + s.page);
    if (s.location) parts.push(s.location);
    return parts.join(", ");
  }
  function nk(key) { return (prop.openId || "") + "|" + key; }
  function noteVal(key, fallback) { var v = prop.notes[nk(key)]; return v !== undefined ? v : (fallback || ""); }
  function noteInput(key, placeholder, fallback) {
    var k = nk(key);
    var i = el("input"); i.type = "text"; i.placeholder = placeholder; i.maxLength = 2000;
    i.value = noteVal(key, fallback); i.dataset.nk = k;
    i.addEventListener("input", function () { prop.notes[k] = i.value; });
    return i;
  }
  function metaRow(grid, label, value, cls) { grid.appendChild(el("div", null, label)); grid.appendChild(el("div", cls || null, value)); }

  function refreshProposals() {
    if (prop.openId) { if (!prop.busy) loadDetail(false); } else loadProposals();
  }

  function loadProposals() {
    var q = [], st = $("propStatus").value, cl = $("propClient").value.trim();
    if (st) q.push("status=" + encodeURIComponent(st));
    if (cl) q.push("client=" + encodeURIComponent(cl));
    return api("GET", "/api/proposals" + (q.length ? "?" + q.join("&") : ""))
      .then(function (j) { prop.list = j; prop.listError = null; renderPropList(); })
      .catch(function (e) { prop.listError = e.message; renderPropList(); });
  }

  function renderPropList() {
    var tb = $("propRows"); clear(tb);
    var msg = $("propListMsg"); clear(msg);
    if (prop.listError) { msg.appendChild(el("div", "banner err", "Couldn't load proposals: " + prop.listError)); return; }
    if (!prop.list) return;
    $("propFolder").textContent = prop.list.folder || "";
    var rows = prop.list.proposals || [];
    if (!rows.length) {
      var tr0 = el("tr"); var td0 = el("td", "empty", ($("propStatus").value || $("propClient").value.trim()) ? "No proposals match these filters." : "No proposals yet. When an agent submits one, it appears here.");
      td0.colSpan = 8; tr0.appendChild(td0); tb.appendChild(tr0);
    }
    rows.forEach(function (it) {
      var s = it.summary, st = it.state;
      var tr = el("tr");
      tr.appendChild(el("td", null, s.clientNumber));
      var tc = el("td"); tc.appendChild(el("div", null, s.title)); tc.appendChild(el("div", "muted small", s.context.taxYear + " · module " + s.context.module)); tr.appendChild(tc);
      var sc = el("td"); sc.appendChild(statusChip(s.status));
      if (st) {
        if (st.readyToApprove) sc.appendChild(el("div", "small t-ok", "Ready to approve"));
        else if (st.readyToCommit) sc.appendChild(el("div", "small t-ok", "Ready to commit"));
        if (st.uncertainLineIds && st.uncertainLineIds.length) sc.appendChild(el("div", "small t-bad", "Check " + plural(st.uncertainLineIds.length, "line") + " in Lacerte"));
      }
      tr.appendChild(sc);
      tr.appendChild(el("td", null, "v" + s.version));
      var lc = el("td");
      lc.appendChild(el("div", null, plural(s.lineCount, "line")));
      var bits = [s.approvedLineCount + " approved", s.rejectedLineCount + " rejected", s.pendingLineCount + " pending"];
      if (s.invalidLineCount) bits.push(s.invalidLineCount + " failed dry run");
      if (s.manualItemCount) bits.push(s.manualItemCount + " by hand");
      if (s.questionCount) bits.push(plural(s.questionCount, "question"));
      lc.appendChild(el("div", "muted small", bits.join(" · ")));
      tr.appendChild(lc);
      tr.appendChild(el("td", null, s.author));
      tr.appendChild(el("td", null, ago(s.updatedAt)));
      var td = el("td"); var b = el("button", s.status === "Submitted" ? "primary" : null, s.status === "Submitted" ? "Review" : "Open");
      b.addEventListener("click", function () { openProposal(s.id); });
      td.appendChild(b); tr.appendChild(td);
      tb.appendChild(tr);
    });
  }

  function openProposal(id) {
    prop.openId = id; prop.detail = null; prop.renderedKey = null; prop.confirm = false; prop.report = null; prop.err = null; prop.busy = null;
    $("propListPanel").classList.add("hidden");
    $("propDetail").classList.remove("hidden");
    $("pdTitle").textContent = "Proposal";
    var box = $("pdBody"); clear(box); box.appendChild(el("p", "muted", "Loading…"));
    window.scrollTo(0, 0);
    loadDetail(true);
  }

  function closeProposal() {
    if (prop.busy === "commit") { flash("warn", "A commit is running. Wait for its result before leaving this proposal."); return; }
    prop.openId = null; prop.detail = null; prop.renderedKey = null; prop.confirm = false; prop.report = null; prop.err = null;
    $("propDetail").classList.add("hidden");
    $("propListPanel").classList.remove("hidden");
    loadProposals();
  }

  function loadDetail(force) {
    var id = prop.openId;
    if (!id) return Promise.resolve();
    return api("GET", "/api/proposals/" + encodeURIComponent(id))
      .then(function (j) { if (prop.openId === id) setDetail(j, force); })
      .catch(function (e) {
        if (prop.openId !== id) return;
        prop.err = { text: "Couldn't load the proposal: " + e.message };
        if (prop.detail) renderDetail();
        else { var box = $("pdBody"); clear(box); box.appendChild(el("div", "banner err", prop.err.text)); }
      });
  }

  function setDetail(view, force) {
    var p = view.proposal;
    var key = [p.id, p.version, p.status, p.updatedAt].join("|");
    var changed = key !== prop.renderedKey;
    prop.detail = view;
    if (changed) prop.confirm = false;
    if (changed || force) { prop.renderedKey = key; renderDetail(); }
  }

  // Runs one reviewer action; the proposal is re-rendered from the server's answer.
  function act(action, body, okText, after) {
    var id = prop.openId;
    if (!id || prop.busy) return;
    prop.busy = action; prop.err = null;
    if (action === "commit") prop.confirm = false;
    renderDetail();
    api("POST", "/api/proposals/" + encodeURIComponent(id) + "/" + action, body)
      .then(function (j) {
        if (prop.openId !== id) return;
        prop.busy = null;
        if (j.report) prop.report = j.report;
        if (after) after();
        setDetail(j, true);
        if (okText) flash("ok", okText);
        load();
      })
      .catch(function (e) {
        if (prop.openId !== id) return;
        prop.busy = null;
        prop.err = { text: e.message, blockers: e.data && e.data.blockers };
        return loadDetail(true);
      });
  }

  function errBanner(box) {
    if (!prop.err) return;
    var b = el("div", "banner err", prop.err.text);
    if (prop.err.blockers && prop.err.blockers.length) {
      var ul = el("ul", "blockers");
      prop.err.blockers.forEach(function (x) { ul.appendChild(el("li", null, x)); });
      b.appendChild(ul);
    }
    box.appendChild(b);
  }

  function renderDetail() {
    var view = prop.detail;
    if (!view) return;
    // Keep the caret in a note box across the rebuild.
    var a = document.activeElement, fk = a && a.dataset ? a.dataset.nk : null;
    var ss = fk ? a.selectionStart : null, se = fk ? a.selectionEnd : null;
    var p = view.proposal, st = view.state, box = $("pdBody");
    clear(box);
    $("pdTitle").textContent = p.title;

    var hd = el("div", "kv");
    function kv(k, v) { hd.appendChild(el("div", null, k)); var d = el("div"); if (v instanceof Node) d.appendChild(v); else d.textContent = v == null ? "" : String(v); hd.appendChild(d); }
    kv("Client", p.clientNumber);
    kv("Status", statusChip(p.status));
    kv("Version", p.version + " (content " + String(st.contentHash || "").slice(0, 12) + ")");
    kv("Return", p.context.taxYear + " · module " + p.context.module);
    kv("Data folder", p.context.dataPath);
    kv("Prepared by", p.author + " · created " + when(p.createdAt) + " · updated " + when(p.updatedAt));
    if (p.notes) kv("Agent's notes", p.notes);
    box.appendChild(hd);

    if (p.approval) {
      var ap = p.approval;
      var t = "Approved by " + ap.by + " (" + ap.channel + ") on " + when(ap.at) + " for version " + ap.version + (ap.note ? ". Note: " + ap.note : ".");
      var ab = el("div", "banner " + (st.approvalCurrent ? "ok" : "err"), st.approvalCurrent ? t : t + " OUT OF DATE: the proposal changed after this approval, so it can't be committed until it is approved again.");
      ab.style.marginTop = "12px"; box.appendChild(ab);
    }
    if (p.lastReview && (p.status === "ReturnedForChanges" || p.status === "Rejected")) {
      var lr = p.lastReview;
      box.appendChild(el("div", "banner warn", (lr.action === "rejected" ? "Rejected" : "Returned for changes") + " by " + lr.by + " on " + when(lr.at) + (lr.note ? ": " + lr.note : "")));
    }
    errBanner(box);
    var uncertain = st.uncertainLineIds || [];
    if (uncertain.length) {
      box.appendChild(el("div", "banner err", "Check " + plural(uncertain.length, "line") + " in Lacerte: " + uncertain.join(", ") + ". An error happened while each was being written, so the row may or may not be on the return. Open the client in Lacerte, look for the row, then press It's in the return or It's missing on the line. These lines are never written again automatically."));
    }

    // Lines
    box.appendChild(el("h3", null, "Lines to write (" + p.lines.length + ")"));
    if (!p.lines.length) box.appendChild(el("p", "empty", "No lines."));
    var reviewing = p.status === "Submitted";
    p.lines.forEach(function (l) { box.appendChild(lineCard(p, st, l, reviewing, uncertain.indexOf(l.lineId) >= 0)); });

    // Manual items
    box.appendChild(el("h3", null, "Enter these by hand in Lacerte (" + p.manualItems.length + ")"));
    if (!p.manualItems.length) box.appendChild(el("p", "empty", "None."));
    else box.appendChild(el("p", "muted small", "The agent couldn't map these to a field it may write. The commit does not enter them."));
    p.manualItems.forEach(function (m) {
      var c = el("div", "pline");
      var h = el("div", "lh"); h.appendChild(el("span", "lid", m.itemId)); h.appendChild(el("span", null, m.description)); c.appendChild(h);
      if (m.amount != null) c.appendChild(el("div", "val", money(m.amount)));
      var g = el("div", "meta");
      metaRow(g, "Why by hand", m.reason);
      if (m.source) { metaRow(g, "Source", sourceText(m.source)); if (m.source.excerpt) metaRow(g, "Excerpt", "“" + m.source.excerpt + "”", "excerpt"); }
      c.appendChild(g); box.appendChild(c);
    });

    // Questions
    box.appendChild(el("h3", null, "Questions from the agent (" + p.questions.length + ")"));
    if (!p.questions.length) box.appendChild(el("p", "empty", "None."));
    p.questions.forEach(function (q) {
      var c = el("div", "pline");
      var h = el("div", "lh"); h.appendChild(el("span", "lid", q.questionId)); h.appendChild(el("span", null, q.text)); c.appendChild(h);
      if (q.lineIds && q.lineIds.length) c.appendChild(el("div", "muted small", "About " + q.lineIds.join(", ")));
      box.appendChild(c);
    });

    box.appendChild(actions(p, st));
    if (prop.report && prop.report.proposalId === p.id) box.appendChild(reportView(prop.report));

    var hist = el("details"); hist.appendChild(el("summary", null, "History (" + p.history.length + ")"));
    p.history.slice().reverse().forEach(function (e) {
      hist.appendChild(el("div", "small", when(e.at) + " · " + e.action + " · " + e.by + " (" + e.channel + ") · v" + e.version + (e.note ? " · " + e.note : "")));
    });
    box.appendChild(hist);

    if (fk) {
      var inputs = box.querySelectorAll("input");
      for (var i = 0; i < inputs.length; i++) {
        if (inputs[i].dataset.nk === fk) { inputs[i].focus(); try { inputs[i].setSelectionRange(ss, se); } catch (x) { } break; }
      }
    }
  }

  function lineCard(p, st, l, reviewing, isUncertain) {
    var c = el("div", "pline dec-" + l.reviewerDecision + (l.preview.ok ? "" : " invalid"));
    var h = el("div", "lh");
    h.appendChild(el("span", "lid", l.lineId));
    h.appendChild(el("span", "chip", l.kind === "Field" ? "Field" : "Detail row"));
    h.appendChild(el("span", "chip " + (l.reviewerDecision === "Approved" ? "ok" : l.reviewerDecision === "Rejected" ? "bad" : "warn"), l.reviewerDecision === "Pending" ? "Not decided" : l.reviewerDecision));
    if (l.confidence === "Low") h.appendChild(el("span", "chip bad", "Low confidence"));
    if (!l.preview.ok) h.appendChild(el("span", "chip bad", "Dry run failed: can't be approved"));
    c.appendChild(h);
    c.appendChild(el("div", "tgt", targetText(l)));
    if (l.kind === "Field") c.appendChild(el("div", "val", describeValue(l.value)));
    else { var d = (l.target && l.target.detail) || {}; c.appendChild(el("div", "val", money(d.amount) + (d.description ? " · " + d.description : ""))); }

    var g = el("div", "meta");
    metaRow(g, "Source", sourceText(l.source));
    if (l.source && l.source.excerpt) metaRow(g, "Excerpt", "“" + l.source.excerpt + "”", "excerpt");
    if (l.reasoning) metaRow(g, "Reasoning", l.reasoning);
    metaRow(g, "Confidence", l.confidence === "Low" ? "Low: check the source carefully" : l.confidence, l.confidence === "Low" ? "t-bad" : null);
    if (l.dictionaryId) metaRow(g, "Dictionary", l.dictionaryId);
    var pv = l.preview || {};
    var pvMsg = pv.message ? String(pv.message).replace(/[.\s]+$/, "") : "";
    metaRow(g, "Dry run", pv.ok ? "Passed" + (pvMsg ? ": " + pvMsg : "") : "FAILED" + (pv.statusName ? " (" + pv.statusName + ")" : "") + (pvMsg ? ": " + pvMsg : "") + ". Reject it or return the proposal so the agent fixes it.", pv.ok ? "t-ok" : "t-bad");
    (l.warnings || []).forEach(function (w) { metaRow(g, "Warning", w, "t-warn"); });
    if (l.reviewerNote) metaRow(g, "Your note", l.reviewerNote);
    if (l.commit) {
      var cr = l.commit;
      metaRow(g, "Commit", (COMMIT_LABEL[cr.status] || cr.status)
        + (cr.status === "SucceededUnverified" ? ": accepted but not confirmed by a read-back; check it in Lacerte" : "")
        + (cr.statusName ? " · " + cr.statusName : "") + (cr.note ? " · " + cr.note : ""), COMMIT_TONE[cr.status] || null);
    }
    c.appendChild(g);

    if (reviewing) {
      var ctrl = el("div", "ctrl");
      ctrl.appendChild(noteInput("line:" + l.lineId, "Note on this line (optional)", l.reviewerNote));
      var decide = function (dec) {
        act("line", { lineId: l.lineId, decision: dec, note: noteVal("line:" + l.lineId, l.reviewerNote), expectedVersion: p.version });
      };
      var ab = el("button", "primary", "Approve");
      ab.disabled = !!prop.busy || !l.preview.ok || l.reviewerDecision === "Approved";
      if (!l.preview.ok) ab.title = "This line failed its dry run and can't be approved.";
      ab.addEventListener("click", function () { decide("approved"); });
      var rb = el("button", "danger", "Reject");
      rb.disabled = !!prop.busy || l.reviewerDecision === "Rejected";
      rb.addEventListener("click", function () { decide("rejected"); });
      ctrl.appendChild(ab); ctrl.appendChild(rb);
      if (l.reviewerDecision !== "Pending") {
        var ub = el("button", null, "Undo");
        ub.disabled = !!prop.busy;
        ub.addEventListener("click", function () { decide("pending"); });
        ctrl.appendChild(ub);
      }
      c.appendChild(ctrl);
    }

    if (isUncertain) {
      var rz = el("div", "confirm");
      var dd = (l.target && l.target.detail) || {};
      rz.appendChild(el("p", null, "This row may already be on the return. In Lacerte, open client " + p.clientNumber + " and look for: " + targetText(l) + ", amount " + money(dd.amount) + (dd.description ? ", “" + dd.description + "”" : "") + ". Then say what you found."));
      rz.appendChild(el("p", "small", "It's in the return: marks the line written; it is not written again. It's missing: the next commit writes it once."));
      var rctl = el("div", "ctrl");
      rctl.appendChild(noteInput("resolve:" + l.lineId, "What you checked (optional)"));
      var yes = el("button", null, "It's in the return");
      var no = el("button", "danger", "It's missing");
      yes.disabled = no.disabled = !!prop.busy;
      var resolve = function (inReturn) {
        act("resolve", { lineId: l.lineId, rowIsInReturn: inReturn, note: noteVal("resolve:" + l.lineId) },
          l.lineId + (inReturn ? " marked as in the return." : " marked as missing; the next commit writes it."),
          function () { delete prop.notes[nk("resolve:" + l.lineId)]; });
      };
      yes.addEventListener("click", function () { resolve(true); });
      no.addEventListener("click", function () { resolve(false); });
      rctl.appendChild(yes); rctl.appendChild(no); rz.appendChild(rctl);
      c.appendChild(rz);
    }
    return c;
  }

  function actions(p, st) {
    var box = el("div", "pact");
    box.appendChild(el("h3", null, "Decision"));
    if (prop.busy) box.appendChild(el("div", "banner info", prop.busy === "commit" ? "Writing to Lacerte… keep this page open until the result appears." : "Saving…"));
    var blockersList = function (title) {
      if (!st.blockers || !st.blockers.length) return;
      box.appendChild(el("div", "small muted", title));
      var ul = el("ul", "blockers small");
      st.blockers.forEach(function (b) { ul.appendChild(el("li", null, b)); });
      box.appendChild(ul);
    };

    if (p.status === "Draft") box.appendChild(el("div", "banner info", "The agent is still preparing this proposal. You can review it once it is submitted."));
    if (p.status === "Rejected") box.appendChild(el("p", "muted", "Rejected. Nothing from it is written; the agent must start a new proposal."));
    if (p.status === "Committed") box.appendChild(el("div", "banner ok", "Committed: every approved line is written. Close and reopen the client in Lacerte to see the changes."));
    if (p.status === "Committing") box.appendChild(el("div", "banner info", "A commit is running."));

    if (p.status === "Submitted") {
      box.appendChild(el("p", null, st.approvedLineCount + " approved · " + st.rejectedLineCount + " rejected · " + st.pendingLineCount + " not decided" + (st.invalidLineCount ? " · " + st.invalidLineCount + " failed dry run" : "")));
      var r1 = el("div", "ctrl");
      r1.appendChild(noteInput("approve", "Note for the approval record (optional)"));
      var apb = el("button", "primary", "Approve proposal (version " + p.version + ")");
      apb.disabled = !!prop.busy || !st.readyToApprove;
      apb.addEventListener("click", function () {
        act("approve", { expectedVersion: p.version, note: noteVal("approve") }, "Approved version " + p.version + ". Commit it when you're ready.",
          function () { delete prop.notes[nk("approve")]; });
      });
      r1.appendChild(apb); box.appendChild(r1);
      if (!st.readyToApprove) blockersList("Can't approve yet:");
    }

    var canReturn = p.status === "Submitted" || p.status === "Approved";
    var canReject = canReturn || p.status === "ReturnedForChanges";
    if (canReject) {
      var r2 = el("div", "ctrl");
      r2.appendChild(noteInput("review", "Note to the agent: what to change, or why it's rejected (required)"));
      var send = function (action, done) {
        var note = noteVal("review").trim();
        if (!note) { prop.err = { text: "Write a note first: the agent needs to know what to change (or why the proposal is rejected)." }; renderDetail(); return; }
        act(action, { note: note }, done, function () { delete prop.notes[nk("review")]; });
      };
      if (canReturn) {
        var rtb = el("button", null, "Return for changes");
        rtb.disabled = !!prop.busy;
        rtb.addEventListener("click", function () { send("return", "Returned to the agent with your note."); });
        r2.appendChild(rtb);
      }
      var rjb = el("button", "danger", "Reject proposal");
      rjb.disabled = !!prop.busy;
      rjb.addEventListener("click", function () { send("reject", "Proposal rejected. Nothing was written."); });
      r2.appendChild(rjb);
      box.appendChild(r2);
    }

    if (p.status === "Approved" || p.status === "PartiallyCommitted" || p.status === "CommitFailed") {
      box.appendChild(el("h3", null, "Write to Lacerte"));
      box.appendChild(el("p", "small", "Writes the approved lines still to do (" + st.unfinishedApprovedLineCount + ") to client " + p.clientNumber + " in Lacerte. Field values are read back to verify them. Rejected lines are never written."));
      var cb = el("button", "primary", "Commit to Lacerte…");
      cb.disabled = !!prop.busy || !st.readyToCommit || prop.confirm;
      cb.addEventListener("click", function () { prop.confirm = true; prop.err = null; renderDetail(); });
      box.appendChild(cb);
      if (!st.readyToCommit) blockersList("Can't commit:");
      if (prop.confirm && st.readyToCommit) {
        var cf = el("div", "confirm");
        cf.appendChild(el("p", null, "Write " + plural(st.unfinishedApprovedLineCount, "approved line") + " to client " + p.clientNumber + "'s " + p.context.taxYear + " return (module " + p.context.module + ", " + p.context.dataPath + ") now?"));
        cf.appendChild(el("p", "small", "This changes the real return. If the client is open in Lacerte, close it first."));
        var yes = el("button", "danger", "Yes, write to the return");
        yes.disabled = !!prop.busy;
        yes.addEventListener("click", function () { act("commit", { expectedVersion: p.version }, "Commit finished. See the result below."); });
        var no = el("button", null, "Cancel");
        no.addEventListener("click", function () { prop.confirm = false; renderDetail(); });
        var row = el("div", "ctrl"); row.appendChild(yes); row.appendChild(no); cf.appendChild(row);
        box.appendChild(cf);
      }
    }
    return box;
  }

  function reportView(r) {
    var box = el("div");
    box.appendChild(el("h3", null, "Commit result"));
    var text = r.status === "Committed"
      ? "Every approved line is written. Close and reopen the client in Lacerte to see the changes. Lines marked not verified were accepted but not read back: check them in Lacerte."
      : r.status === "PartiallyCommitted"
        ? "Some lines are written. Fix the cause shown on each line that wasn't, then commit again: only unfinished lines run. Lines that may already be on the return must be checked and resolved first."
        : r.status === "CommitFailed" ? "Nothing was written. Fix the cause shown on each line (for example, close the client in Lacerte) and commit again."
        : "The commit finished with status " + r.status + ".";
    box.appendChild(el("div", "banner " + (r.status === "Committed" ? "ok" : r.status === "PartiallyCommitted" ? "warn" : "err"), text));
    box.appendChild(el("p", "small", "This commit ran " + plural(r.ranCount, "line") + ": " + r.succeededCount + " written and verified, " + r.succeededUnverifiedCount + " written but not verified, " + r.failedCount + " not written, " + r.uncertainCount + " may be on the return, " + r.skippedCount + " skipped. " + r.alreadyDoneCount + " were written by an earlier commit."));
    var tbl = el("table", "wide"); var th = el("thead"); var hr = el("tr");
    ["Line", "Kind", "This commit", "Result", "Details"].forEach(function (x) { hr.appendChild(el("th", null, x)); });
    th.appendChild(hr); tbl.appendChild(th);
    var tb = el("tbody");
    (r.lines || []).forEach(function (x) {
      var res = x.result || {};
      var tr = el("tr");
      tr.appendChild(el("td", null, x.lineId));
      tr.appendChild(el("td", null, x.kind === "Field" ? "Field" : "Detail row"));
      tr.appendChild(el("td", null, x.ran ? "Ran" : "Not run"));
      tr.appendChild(el("td", COMMIT_TONE[res.status] || null, (COMMIT_LABEL[res.status] || res.status || "") + (res.verified === true ? " (read back)" : "")));
      tr.appendChild(el("td", "small", (res.statusName ? res.statusName + (res.note ? ": " : "") : "") + (res.note || "")));
      tb.appendChild(tr);
    });
    tbl.appendChild(tb);
    var sc = el("div", "scroll"); sc.appendChild(tbl); box.appendChild(sc);
    return box;
  }

  document.querySelectorAll("nav.tabs button").forEach(function (b) {
    b.addEventListener("click", function () {
      activeTab = b.dataset.tab;
      document.querySelectorAll("nav.tabs button").forEach(function (x) { x.setAttribute("aria-selected", String(x === b)); });
      document.querySelectorAll("section.tab").forEach(function (x) { x.classList.toggle("active", x.id === "tab-" + b.dataset.tab); });
      if (activeTab === "proposals") refreshProposals();
    });
  });
  $("propRefresh").addEventListener("click", function () { loadProposals(); });
  $("propStatus").addEventListener("change", function () { loadProposals(); });
  $("propClient").addEventListener("change", function () { loadProposals(); });
  $("propClient").addEventListener("keydown", function (e) { if (e.key === "Enter") loadProposals(); });
  $("pdClose").addEventListener("click", closeProposal);
  $("btnSignin").addEventListener("click", function () {
    post("/api/signin").then(function (r) { flash("info", r.message); }).catch(function (e) { flash("err", e.message); });
  });
  $("btnReconnect").addEventListener("click", function () {
    post("/api/reconnect").then(function (r) { flash(r.ok ? "ok" : "err", r.message || (r.ok ? "Connected." : "Failed.")); load(); }).catch(function (e) { flash("err", e.message); });
  });
  load();
  setInterval(function () {
    if (document.hidden) return;
    load();
    if (activeTab === "proposals") refreshProposals();
  }, 5000);
})();
</script>
</body>
</html>
""";
    }
}
