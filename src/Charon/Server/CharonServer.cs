using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Charon.Config;
using Charon.Providers;
using Charon.Tuner;

namespace Charon.Server;

public sealed class CharonServer : IAsyncDisposable
{
    private readonly CharonConfig _config;
    private readonly ProviderManager _providers;
    private readonly ChromeTuner _tuner;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<int, string> _activeTuners = new();
    private readonly ConcurrentQueue<string> _recentLogs = new();
    private WebApplication? _app;

    public IReadOnlyDictionary<int, string> ActiveTuners => _activeTuners;
    public event Action? TunersChanged;
    public event Action? ConfigOrLineupChanged;

    public CharonServer(CharonConfig config, ProviderManager providers, ChromeTuner tuner, Action<string> log)
    {
        _config = config;
        _providers = providers;
        _tuner = tuner;
        _log = log;
    }

    public void RecordLog(string formattedMessage)
    {
        _recentLogs.Enqueue(formattedMessage);
        while (_recentLogs.Count > 500)
        {
            _recentLogs.TryDequeue(out _);
        }
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.ListenAnyIP(_config.ResolvedPort);
        });
        builder.Logging.ClearProviders();

        _app = builder.Build();

        // 1. Web Dashboard (or Status JSON if requested via API client)
        _app.MapGet("/", async (HttpContext ctx) =>
        {
            string accept = ctx.Request.Headers.Accept.ToString();
            if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(GetStatusPayload());
                return;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(GetDashboardHtml());
        });

        // 2. Channels DVR M3U Playlist Endpoint
        //    Optional filter: ?provider=xfinity or ?provider=xfinity,youtubetv
        //    (repeated ?provider=a&provider=b also works). Matches provider IDs, case-insensitive.
        _app.MapGet("/playlist.m3u", (HttpContext ctx) =>
        {
            _providers.ReloadProviders();

            var providerFilter = ctx.Request.Query["provider"]
                .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string m3u = _providers.GenerateM3uPlaylist(_config, providerFilter);
            ctx.Response.ContentType = "audio/x-mpegurl; charset=utf-8";
            return ctx.Response.WriteAsync(m3u);
        });

        // 3. Remote Management REST APIs
        _app.MapGet("/api/status", () => Results.Ok(GetStatusPayload()));

        _app.MapGet("/api/logs", () => Results.Ok(_recentLogs.ToArray()));

        _app.MapGet("/api/providers", () =>
        {
            _providers.ReloadProviders();
            var result = _providers.Providers.Select(p => new
            {
                id = p.Manifest.Id,
                name = p.Manifest.Name,
                loginUrl = p.Manifest.LoginUrl,
                totalChannels = p.Channels.Count,
                activeChannels = p.Channels.Count(c => !c.IsHidden),
                channels = p.Channels
            });
            return Results.Ok(result);
        });

        _app.MapPost("/api/providers/{providerId}/channels", async (string providerId, HttpContext ctx) =>
        {
            var provider = _providers.Providers.FirstOrDefault(p =>
                string.Equals(p.Manifest.Id, providerId, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
                return Results.NotFound(new { error = $"Provider '{providerId}' not found." });

            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            string rawJson = await reader.ReadToEndAsync();

            try
            {
                var incoming = ProviderManager.ParseChannelsJson(rawJson);
                string savedPath = _providers.SaveProviderChannels(provider, incoming);
                _providers.ReloadProviders();
                ConfigOrLineupChanged?.Invoke();
                _log($"[WebRemote] Saved {incoming.Count} channel(s) for {provider.Manifest.Name} to {savedPath}");
                return Results.Ok(new { success = true, savedPath, count = incoming.Count });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        _app.MapPost("/api/settings", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;

            if (root.TryGetProperty("enable1080p", out var resProp))
                _config.Enable1080p = resProp.GetBoolean();

            if (root.TryGetProperty("maxConcurrentTuners", out var tunersProp))
                _config.MaxConcurrentTuners = Math.Clamp(tunersProp.GetInt32(), 1, 4);

            if (root.TryGetProperty("windowPlacement", out var winProp) &&
                Enum.TryParse<WindowPlacementMode>(winProp.GetString(), out var mode))
            {
                _config.WindowPlacement = mode;
            }

            if (root.TryGetProperty("customX", out var xProp))
                _config.CustomX = xProp.GetInt32();

            if (root.TryGetProperty("customY", out var yProp))
                _config.CustomY = yProp.GetInt32();

            if (root.TryGetProperty("channelsDvrIp", out var dvrIpProp))
                _config.ChannelsDvrIp = dvrIpProp.GetString()?.Trim() ?? string.Empty;

            if (root.TryGetProperty("channelsDvrPort", out var dvrPortProp))
                _config.ChannelsDvrPort = dvrPortProp.GetInt32();

            _config.Save();
            TunersChanged?.Invoke();
            ConfigOrLineupChanged?.Invoke();
            _log("[WebRemote] Updated configuration via Web Dashboard");
            return Results.Ok(new { success = true });
        });

        _app.MapPost("/api/auth/login/{providerId}", (string providerId) =>
        {
            var provider = _providers.Providers.FirstOrDefault(p =>
                string.Equals(p.Manifest.Id, providerId, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
                return Results.NotFound(new { error = "Provider not found." });

            _log($"[WebRemote] Launching interactive Chrome login window on host machine for {provider.Manifest.Name}...");
            _tuner.LaunchInteractiveLogin(provider.Manifest.LoginUrl, _log);
            return Results.Ok(new { success = true, message = $"Launched login window on host for {provider.Manifest.Name}." });
        });

        _app.MapPost("/api/auth/sync", () =>
        {
            _log("[WebRemote] Syncing Tuner 1 master profile across Tuners 2–4...");
            _tuner.SyncMasterAuthToAllTuners(_log);
            return Results.Ok(new { success = true, message = "Synced master auth profile to all tuners." });
        });

        _app.MapPost("/api/providers/{providerId}/scan", async (string providerId) =>
        {
            var provider = _providers.Providers.FirstOrDefault(p =>
                string.Equals(p.Manifest.Id, providerId, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
                return Results.NotFound(new { error = "Provider not found." });

            try
            {
                _log($"[WebRemote] Starting channel lineup scan for {provider.Manifest.Name}...");
                var (channels, matchedCount) = await ChannelSyncService.ScanAndMatchAsync(
                    _config,
                    provider,
                    _config.ChannelsDvrIp,
                    _config.ChannelsDvrPort,
                    _log,
                    status => _log($"[Sync] {status}"));

                var previousById = provider.Channels.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var ch in channels)
                {
                    if (previousById.TryGetValue(ch.Id, out var prev))
                    {
                        ch.IsHidden = prev.IsHidden;
                        if (string.IsNullOrWhiteSpace(ch.StationId) && !string.IsNullOrWhiteSpace(prev.StationId))
                            ch.StationId = prev.StationId;
                    }
                }

                string savedPath = _providers.SaveProviderChannels(provider, channels);
                _providers.ReloadProviders();
                ConfigOrLineupChanged?.Invoke();

                _log($"[WebRemote] Scan complete: {channels.Count} channels ({matchedCount} Gracenote IDs) saved to {savedPath}");
                return Results.Ok(new { success = true, total = channels.Count, matched = matchedCount });
            }
            catch (Exception ex)
            {
                _log($"[WebRemote] Scan failed: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 4. Live MPEG-TS Stream Endpoint
        _app.MapGet("/stream/{providerId}/{channelId}", async (string providerId, string channelId, HttpContext ctx) =>
        {
            var (provider, channel) = _providers.FindChannel(providerId, channelId);
            if (provider is null || channel is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync($"Channel '{providerId}/{channelId}' not found.");
                return;
            }

            int tunerId = AcquireTunerSlot($"{provider.Manifest.Name}: {channel.Name}");
            if (tunerId == -1)
            {
                _log($"[Server] All {_config.MaxConcurrentTuners} tuner slots are busy. Rejecting tune for {channel.Name}.");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "5";
                await ctx.Response.WriteAsync("All tuner slots are currently in use.");
                return;
            }

            try
            {
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers.CacheControl = "no-cache, no-store";

                await ctx.Response.StartAsync(ctx.RequestAborted);

                await _tuner.StreamChannelAsync(
                    tunerId,
                    provider,
                    channel,
                    ctx.Response.Body,
                    _log,
                    ctx.RequestAborted);
            }
            finally
            {
                ReleaseTunerSlot(tunerId);
            }
        });

        await _app.StartAsync();
        _log($"[Server] Charon listening on http://{_config.ResolvedLanIp}:{_config.ResolvedPort}");
        _log($"[Server] M3U URL: http://{_config.ResolvedLanIp}:{_config.ResolvedPort}/playlist.m3u");
    }

    private object GetStatusPayload() => new
    {
        application = "Charon",
        lanIp = _config.ResolvedLanIp,
        port = _config.ResolvedPort,
        playlistUrl = $"http://{_config.ResolvedLanIp}:{_config.ResolvedPort}/playlist.m3u",
        maxTuners = _config.MaxConcurrentTuners,
        enable1080p = _config.Enable1080p,
        windowPlacement = _config.WindowPlacement.ToString(),
        customX = _config.CustomX,
        customY = _config.CustomY,
        channelsDvrIp = _config.ChannelsDvrIp,
        channelsDvrPort = _config.ChannelsDvrPort,
        activeTuners = _activeTuners.ToDictionary(k => k.Key, v => v.Value)
    };

    private int AcquireTunerSlot(string description)
    {
        for (int i = 1; i <= _config.MaxConcurrentTuners; i++)
        {
            if (_activeTuners.TryAdd(i, description))
            {
                TunersChanged?.Invoke();
                return i;
            }
        }
        return -1;
    }

    private void ReleaseTunerSlot(int tunerId)
    {
        if (_activeTuners.TryRemove(tunerId, out _))
        {
            TunersChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static string GetDashboardHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
  <title>Charon — Browser Tuner Bridge</title>
  <style>
    :root {
      --bg: #18181b;
      --panel: #27272a;
      --dark: #09090b;
      --border: #3f3f46;
      --accent: #38bdf8;
      --primary: #0284c7;
      --success: #16a34a;
      --danger: #7f1d1d;
      --text: #f4f4f5;
      --muted: #a1a1aa;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body { background: var(--bg); color: var(--text); font-family: system-ui, -apple-system, sans-serif; padding: 18px; }
    .container { max-width: 1180px; margin: 0 auto; display: flex; flex-direction: column; gap: 12px; }
    .card { background: var(--panel); border-radius: 8px; padding: 14px; }
    .row { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 10px; }
    .title { font-size: 16px; font-weight: 700; color: var(--accent); }
    .mono { font-family: Consolas, monospace; }
    .btn { background: var(--primary); color: #fff; border: none; border-radius: 5px; padding: 8px 14px; font-weight: 600; cursor: pointer; font-size: 13px; }
    .btn:hover { opacity: 0.92; }
    .btn:disabled { opacity: 0.5; cursor: wait; }
    .btn-green { background: var(--success); }
    .btn-gray { background: var(--border); }
    .btn-red { background: #450a0a; color: #fca5a5; border: 1px solid var(--danger); }
    input, select, textarea { background: var(--dark); color: var(--text); border: 1px solid var(--border); border-radius: 4px; padding: 6px 8px; font-size: 13px; }
    label { font-size: 13px; color: var(--muted); margin-right: 6px; }
    .status-pill { color: #4ade80; font-weight: 600; font-size: 14px; }
    .tabs { display: flex; gap: 8px; margin-bottom: 10px; border-bottom: 1px solid var(--border); padding-bottom: 8px; }
    .tab { background: none; border: none; color: var(--muted); font-weight: 600; padding: 6px 12px; cursor: pointer; border-radius: 4px; }
    .tab.active { background: var(--border); color: var(--accent); }
    table { width: 100%; border-collapse: collapse; background: var(--dark); font-size: 13px; }
    th, td { padding: 8px 10px; border-bottom: 1px solid var(--panel); text-align: left; }
    th { background: var(--panel); color: var(--accent); position: sticky; top: 0; }
    tr.hidden-row { opacity: 0.42; }
    td input[type="text"] { width: 100%; border: none; background: transparent; }
    td input[type="text"]:focus { background: var(--panel); outline: 1px solid var(--accent); }
    #logBox { background: var(--dark); color: #d4d4d8; font-family: Consolas, monospace; font-size: 12px; height: 420px; overflow-y: auto; padding: 12px; border-radius: 6px; white-space: pre-wrap; }
    #rawJsonBox { width: 100%; height: 420px; font-family: Consolas, monospace; color: var(--accent); display: none; }
  </style>
</head>
<body>
<div class="container">
  <!-- Header -->
  <div class="card row">
    <div>
      <div class="title">CHARON STREAMING SERVER (WEB REMOTE)</div>
      <div id="m3uUrl" class="mono" style="color: var(--muted); margin-top: 4px; font-size: 14px;">http://0.0.0.0:48590/playlist.m3u</div>
    </div>
    <div class="row">
      <button class="btn" onclick="copyM3u()">Copy M3U URL</button>
    </div>
  </div>

  <!-- Provider Controls -->
  <div class="card row">
    <div class="row">
      <select id="cmbProviders" style="min-width: 210px;" onchange="onProviderChange()"></select>
      <button class="btn btn-green" onclick="triggerLogin()">1. Login on Host</button>
      <button id="btnScan" class="btn" onclick="triggerScan()">2. Scan &amp; Sync Lineup</button>
      <button class="btn btn-gray" onclick="syncAuth()">Sync Tuners 2–4</button>
      <button class="btn btn-gray" onclick="loadProviders()">Reload</button>
    </div>
    <div id="txtTunerStatus" class="status-pill">Active Tuners: 0 / 2</div>
  </div>

  <!-- Settings Bar -->
  <div class="card row" style="justify-content: flex-start; gap: 16px;">
    <div>
      <label>Resolution:</label>
      <select id="cmbRes" onchange="saveSettings()">
        <option value="720p">720p</option>
        <option value="1080p">1080p</option>
      </select>
    </div>
    <div>
      <label>Max Tuners:</label>
      <select id="cmbMaxTuners" onchange="saveSettings()">
        <option value="1">1</option><option value="2">2</option><option value="3">3</option><option value="4">4</option>
      </select>
    </div>
    <div>
      <label>Window Mode:</label>
      <select id="cmbWinMode" onchange="saveSettings()">
        <option value="AutoOffScreen">AutoOffScreen</option>
        <option value="CustomCoordinates">CustomCoordinates</option>
        <option value="VisibleDebug">VisibleDebug</option>
      </select>
    </div>
    <div>
      <label>Custom X/Y:</label>
      <input id="txtX" type="number" style="width:65px" onchange="saveSettings()"/>
      <input id="txtY" type="number" style="width:65px" onchange="saveSettings()"/>
    </div>
    <div>
      <label>DVR IP/Port:</label>
      <input id="txtDvrIp" type="text" placeholder="192.168.1.x" style="width:115px" onchange="saveSettings()"/>
      <input id="txtDvrPort" type="number" style="width:70px" onchange="saveSettings()"/>
    </div>
  </div>

  <!-- Workspace Tabs -->
  <div class="card">
    <div class="tabs">
      <button id="tabBtnLogs" class="tab" onclick="showTab('logs')">Live Server Logs</button>
      <button id="tabBtnEditor" class="tab active" onclick="showTab('editor')">Channel Lineup Editor (channels.json)</button>
    </div>

    <!-- Logs View -->
    <div id="viewLogs" style="display:none;">
      <div id="logBox">Loading server logs...</div>
    </div>

    <!-- Editor View -->
    <div id="viewEditor">
      <div class="row" style="margin-bottom: 10px;">
        <div class="row">
          <button class="btn btn-gray" onclick="addChannel()">+ Add</button>
          <button class="btn btn-gray" onclick="setAllHidden(false)">Show All</button>
          <button class="btn btn-gray" onclick="setAllHidden(true)">Hide All</button>
          <input id="txtFilter" type="text" placeholder="Filter channels..." oninput="renderTable()" style="width: 220px;"/>
        </div>
        <div class="row">
          <span id="txtStats" style="color: var(--muted); font-size: 12px; font-weight: 600;">0 active</span>
          <label><input type="checkbox" id="chkRaw" onchange="toggleRawJson()"/> Raw JSON</label>
          <button class="btn btn-green" onclick="saveChannels()">Save Lineup</button>
        </div>
      </div>

      <div id="tableContainer" style="max-height: 420px; overflow-y: auto;">
        <table>
          <thead>
            <tr>
              <th style="width: 55px;">Hide</th>
              <th style="width: 75px;">Ch #</th>
              <th style="width: 200px;">Channel Name</th>
              <th style="width: 130px;">Gracenote ID</th>
              <th style="width: 150px;">ID (Slug)</th>
              <th>Watch URL</th>
              <th style="width: 50px;">Del</th>
            </tr>
          </thead>
          <tbody id="tbodyChannels"></tbody>
        </table>
      </div>
      <textarea id="rawJsonBox"></textarea>
    </div>
  </div>
</div>

<script>
let providers = [];
let channels = [];

async function init() {
  await loadStatus();
  await loadProviders();
  await loadLogs();
  setInterval(loadStatus, 3000);
  setInterval(loadLogs, 3000);
}

async function loadStatus() {
  try {
    const r = await fetch('/api/status', { headers: { 'Accept': 'application/json' } });
    const d = await r.json();
    document.getElementById('m3uUrl').textContent = d.playlistUrl;
    document.getElementById('cmbRes').value = d.enable1080p ? '1080p' : '720p';
    document.getElementById('cmbMaxTuners').value = d.maxTuners;
    document.getElementById('cmbWinMode').value = d.windowPlacement;
    if (document.activeElement.id !== 'txtX') document.getElementById('txtX').value = d.customX;
    if (document.activeElement.id !== 'txtY') document.getElementById('txtY').value = d.customY;
    if (document.activeElement.id !== 'txtDvrIp') document.getElementById('txtDvrIp').value = d.channelsDvrIp || '';
    if (document.activeElement.id !== 'txtDvrPort') document.getElementById('txtDvrPort').value = d.channelsDvrPort || 8089;

    const activeCount = Object.keys(d.activeTuners || {}).length;
    document.getElementById('txtTunerStatus').textContent = `Active Tuners: ${activeCount} / ${d.maxTuners}`;
  } catch (e) {}
}

async function loadProviders() {
  const sel = document.getElementById('cmbProviders');
  const prev = sel.value;
  const r = await fetch('/api/providers');
  providers = await r.json();
  sel.innerHTML = providers.map(p => `<option value="${p.id}">${p.name} (${p.activeChannels}/${p.totalChannels} ch)</option>`).join('');
  if (prev && providers.some(p => p.id === prev)) sel.value = prev;
  onProviderChange();
}

function onProviderChange() {
  const p = providers.find(x => x.id === document.getElementById('cmbProviders').value);
  channels = p ? JSON.parse(JSON.stringify(p.channels)) : [];
  document.getElementById('rawJsonBox').value = JSON.stringify(channels, null, 2);
  renderTable();
}

function updateStats() {
  const total = channels.length;
  const hidden = channels.filter(c => c.isHidden).length;
  document.getElementById('txtStats').textContent = `${total - hidden} active in M3U (${hidden} hidden)`;
}

function escapeAttr(s) {
  return String(s ?? '').replace(/&/g, '&amp;').replace(/"/g, '&quot;');
}

function renderTable() {
  updateStats();
  const q = document.getElementById('txtFilter').value.trim().toLowerCase();
  const tbody = document.getElementById('tbodyChannels');

  tbody.innerHTML = channels
    .map((ch, realIdx) => ({ ch, realIdx }))
    .filter(({ ch }) => !q ||
      (ch.name || '').toLowerCase().includes(q) ||
      (ch.channelNumber || '').toLowerCase().includes(q) ||
      (ch.id || '').toLowerCase().includes(q) ||
      (ch.stationId || '').toLowerCase().includes(q))
    .map(({ ch, realIdx }) => `
      <tr class="${ch.isHidden ? 'hidden-row' : ''}">
        <td style="text-align:center"><input type="checkbox" ${ch.isHidden ? 'checked' : ''} onchange="editCh(${realIdx}, 'isHidden', this.checked, true)"/></td>
        <td><input type="text" class="mono" style="color:#38bdf8" value="${escapeAttr(ch.channelNumber)}" onchange="editCh(${realIdx}, 'channelNumber', this.value)"/></td>
        <td><input type="text" style="font-weight:600" value="${escapeAttr(ch.name)}" onchange="editCh(${realIdx}, 'name', this.value)"/></td>
        <td><input type="text" class="mono" style="color:#4ade80" value="${escapeAttr(ch.stationId)}" onchange="editCh(${realIdx}, 'stationId', this.value)"/></td>
        <td><input type="text" class="mono" value="${escapeAttr(ch.id)}" onchange="editCh(${realIdx}, 'id', this.value)"/></td>
        <td><input type="text" class="mono" value="${escapeAttr(ch.url)}" onchange="editCh(${realIdx}, 'url', this.value)"/></td>
        <td><button class="btn btn-red" style="padding:3px 8px" onclick="deleteCh(${realIdx})">✕</button></td>
      </tr>
    `).join('');
}

function editCh(realIdx, field, val, rerender = false) {
  channels[realIdx][field] = val;
  if (rerender) renderTable();
}

function addChannel() {
  channels.push({ id: `ch-${channels.length + 1}`, name: 'New Channel', channelNumber: '', stationId: '', url: 'https://', isHidden: false });
  renderTable();
}

function deleteCh(realIdx) {
  channels.splice(realIdx, 1);
  renderTable();
}

function setAllHidden(hide) {
  channels.forEach(c => c.isHidden = hide);
  renderTable();
}

function toggleRawJson() {
  const raw = document.getElementById('chkRaw').checked;
  const box = document.getElementById('rawJsonBox');
  const tbl = document.getElementById('tableContainer');
  if (raw) {
    box.value = JSON.stringify(channels, null, 2);
    tbl.style.display = 'none';
    box.style.display = 'block';
  } else {
    try {
      channels = JSON.parse(box.value);
      box.style.display = 'none';
      tbl.style.display = 'block';
      renderTable();
    } catch (e) {
      alert('Invalid JSON: ' + e.message);
      document.getElementById('chkRaw').checked = true;
    }
  }
}

async function saveChannels() {
  const pId = document.getElementById('cmbProviders').value;
  if (!pId) return;
  if (document.getElementById('chkRaw').checked) {
    try { channels = JSON.parse(document.getElementById('rawJsonBox').value); }
    catch (e) { alert('Invalid JSON: ' + e.message); return; }
  }
  const r = await fetch(`/api/providers/${pId}/channels`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(channels)
  });
  if (r.ok) {
    await loadProviders();
    alert('Saved channels.json! Your M3U playlist is updated immediately.');
  } else {
    const err = await r.json();
    alert('Save failed: ' + (err.error || 'Unknown error'));
  }
}

async function saveSettings() {
  await fetch('/api/settings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      enable1080p: document.getElementById('cmbRes').value === '1080p',
      maxConcurrentTuners: parseInt(document.getElementById('cmbMaxTuners').value),
      windowPlacement: document.getElementById('cmbWinMode').value,
      customX: parseInt(document.getElementById('txtX').value) || -3200,
      customY: parseInt(document.getElementById('txtY').value) || 0,
      channelsDvrIp: document.getElementById('txtDvrIp').value,
      channelsDvrPort: parseInt(document.getElementById('txtDvrPort').value) || 8089
    })
  });
}

async function triggerLogin() {
  const pId = document.getElementById('cmbProviders').value;
  if (!pId) return;
  const r = await fetch(`/api/auth/login/${pId}`, { method: 'POST' });
  const d = await r.json();
  alert(d.message || 'Login launched on host');
}

async function triggerScan() {
  const pId = document.getElementById('cmbProviders').value;
  if (!pId) return;
  const btn = document.getElementById('btnScan');
  btn.disabled = true;
  btn.textContent = 'Scanning...';
  try {
    const r = await fetch(`/api/providers/${pId}/scan`, { method: 'POST' });
    const d = await r.json();
    if (r.ok) {
      await loadProviders();
      alert(`Scan complete! Saved ${d.total} channels (${d.matched} Gracenote IDs).`);
    } else {
      alert('Scan failed: ' + (d.error || 'Unknown error'));
    }
  } finally {
    btn.disabled = false;
    btn.textContent = '2. Scan & Sync Lineup';
  }
}

async function syncAuth() {
  const r = await fetch('/api/auth/sync', { method: 'POST' });
  const d = await r.json();
  alert(d.message);
}

async function loadLogs() {
  try {
    const r = await fetch('/api/logs');
    const lines = await r.json();
    const box = document.getElementById('logBox');
    box.textContent = lines.join('\n');
    box.scrollTop = box.scrollHeight;
  } catch (e) {}
}

function showTab(t) {
  document.getElementById('viewLogs').style.display = t === 'logs' ? 'block' : 'none';
  document.getElementById('viewEditor').style.display = t === 'editor' ? 'block' : 'none';
  document.getElementById('tabBtnLogs').className = 'tab' + (t === 'logs' ? ' active' : '');
  document.getElementById('tabBtnEditor').className = 'tab' + (t === 'editor' ? ' active' : '');
}

function copyM3u() {
  const text = document.getElementById('m3uUrl').textContent;
  if (navigator.clipboard && window.isSecureContext) {
    navigator.clipboard.writeText(text).then(() => alert('Copied: ' + text));
  } else {
    const ta = document.createElement('textarea');
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand('copy');
    document.body.removeChild(ta);
    alert('Copied: ' + text);
  }
}

init();
</script>
</body>
</html>
""";
}