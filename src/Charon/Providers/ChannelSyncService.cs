using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Charon.Config;

namespace Charon.Providers;

public class ChannelSyncService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<(List<ProviderChannel> Channels, int MatchedCount)> ScanAndMatchAsync(
        CharonConfig config,
        LoadedProvider provider,
        string dvrIp,
        int dvrPort,
        Action<string> log,
        Action<string> statusCallback)
    {
        statusCallback($"Step 1/2: Scraping live channel lineup from {provider.Manifest.Name}...");
        log($"[Sync] Launching browser scan for {provider.Manifest.Name} using authenticated Tuner profile...");

        List<ProviderChannel> scrapedChannels = await ScrapeProviderLineupViaCdpAsync(config, provider, log, statusCallback);
        if (scrapedChannels.Count == 0)
        {
            throw new InvalidOperationException(
                $"No channels were found on {provider.Manifest.Name}. Please click '1. Login to Provider', sign in, CLOSE the Chrome login window completely, and then click '2. Scan & Sync Lineup'.");
        }

        // Apply the user's current hide choices before matching, so hidden channels (e.g. Sling Freestream) skip the DVR lookup
        var previousHidden = provider.Channels
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IsHidden, StringComparer.OrdinalIgnoreCase);
        foreach (var ch in scrapedChannels)
        {
            if (previousHidden.TryGetValue(ch.Id, out bool hidden))
                ch.IsHidden = hidden;
        }

        // If the provider (like Spectrum) already supplies native Gracenote tmsid values, skip the Channels DVR lookup!
        int prePopulated = scrapedChannels.Count(c => !string.IsNullOrWhiteSpace(c.StationId));
        if (prePopulated == scrapedChannels.Count)
        {
            log($"[Sync] Scraped {scrapedChannels.Count} channels from {provider.Manifest.Name} with native Gracenote tmsid values already populated.");
            return (scrapedChannels, prePopulated);
        }

        log($"[Sync] Scraped {scrapedChannels.Count} channels from {provider.Manifest.Name}. Resolving Gracenote IDs via http://{dvrIp}:{dvrPort}...");
        int matchedCount = await MatchGracenoteStationsAsync(scrapedChannels, dvrIp, dvrPort, log, statusCallback);

        return (scrapedChannels, matchedCount);
    }

    private static async Task<List<ProviderChannel>> ScrapeProviderLineupViaCdpAsync(
        CharonConfig config,
        LoadedProvider provider,
        Action<string> log,
        Action<string> statusCallback)
    {
        string baseProfilesDir = ResolveExistingProfilesRoot();
        string selectedProfile = SelectUnlockedAuthenticatedProfile(baseProfilesDir, log);

        log($"[Sync] Using authenticated Chrome profile: {selectedProfile}");

        int cdpPort = FindFreeLoopbackPort(9330);
       bool isSpectrum = provider.Manifest.Id.Contains("spectrum", StringComparison.OrdinalIgnoreCase);
        bool isXfinity = provider.Manifest.Id.Contains("xfinity", StringComparison.OrdinalIgnoreCase);
        bool isSling = provider.Manifest.Id.Contains("sling", StringComparison.OrdinalIgnoreCase);
        string targetUrl = isSpectrum
            ? "https://watch.spectrum.net/guide"
            : isXfinity
            ? "https://www.xfinity.com/stream/listings"
            : isSling
            ? "https://watch.sling.com/dashboard/grid_guide/grid_guide_a_z"
            : "https://tv.youtube.com/live";

        int posX = config.WindowPlacement == WindowPlacementMode.VisibleDebug ? 80 : -3200;
        int posY = config.WindowPlacement == WindowPlacementMode.VisibleDebug ? 80 : 0;

        // Add --disable-extensions during lineup scan so TabCaptureExt CSS/scripts never lock the guide grid!
        string args = string.Join(" ",
        [
            $"--user-data-dir=\"{selectedProfile}\"",
            $"--remote-debugging-port={cdpPort}",
            "--remote-allow-origins=*",
            "--disable-extensions",
            $"--window-position={posX},{posY}",
            "--window-size=1280,1000",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-blink-features=AutomationControlled",
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--mute-audio",
            $"\"{targetUrl}\""
        ]);

        Process? chromeProc = null;
        string? wsUrl = null;

        try
        {
            chromeProc = Process.Start(new ProcessStartInfo
            {
                FileName = config.ResolvedChromePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            wsUrl = await WaitForPageWebSocketUrlAsync(cdpPort, TimeSpan.FromSeconds(15));
            if (string.IsNullOrWhiteSpace(wsUrl))
                throw new TimeoutException("Timed out connecting to Chrome DevTools Protocol. Make sure the Login window is closed before scanning.");

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

            if (isSpectrum)
            {
                string spectrumJs = GetSpectrumScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for {provider.Manifest.Name} guide to load (attempt {attempt})...");
                    string? jsonResult = await EvaluateJsStringAsync(ws, attempt, spectrumJs);
                    if (!string.IsNullOrWhiteSpace(jsonResult) && jsonResult.StartsWith("[", StringComparison.Ordinal) && jsonResult != "[]")
                    {
                        var parsed = ProviderManager.ParseChannelsJson(jsonResult);
                        if (parsed.Count > 0) return parsed;
                    }
                    await Task.Delay(1500);
                }
                return [];
            }
			else if (isXfinity)
            {
                string xfinityJs = GetXfinityScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for Xfinity Stream guide data (<tv-page-grid>) to load (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, xfinityJs);
                    if (!string.IsNullOrWhiteSpace(res) && res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                    {
                        var parsed = ProviderManager.ParseChannelsJson(res);
                        if (parsed.Count > 0)
                            return parsed;
                    }
                    await Task.Delay(1500);
                }

                return [];
            }
            else if (isSling)
            {
                // Sling: pass "Who's Watching?", open the A-Z guide, then read the full lineup from its virtualized list
                string slingJs = GetSlingScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(60);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for Sling TV A-Z guide to load (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, slingJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0)
                                return parsed;
                        }
                    }

                    await Task.Delay(1500);
                }

                return [];
            }
            else
            {
                // YouTube TV: Wait for <ytu-epg-row> data models to mount and auto-scroll #id-bottom-main-content
                string yttvJs = GetYouTubeTvScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(45);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for YouTube TV live guide (<ytu-epg-row>) to load and harvest (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, yttvJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0)
                                return parsed;
                        }
                    }

                    await Task.Delay(1500);
                }

                return [];
            }
        }
        finally
        {
            if (wsUrl is not null)
            {
                try
                {
                    using var closeWs = new ClientWebSocket();
                    await closeWs.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                    await SendCdpCommandAsync(closeWs, 9999, "Browser.close", new { });
                    await Task.Delay(300);
                }
                catch { }
            }

            if (chromeProc is { HasExited: false })
            {
                try { chromeProc.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static string ResolveExistingProfilesRoot()
    {
        // Check standard Profiles path and ChromeProfiles path in case ChromeTuner uses either naming
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Profiles"),
            Path.Combine(AppContext.BaseDirectory, "ChromeProfiles")
        ];

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
                return path;
        }

        return candidates[0];
    }

    private static string SelectUnlockedAuthenticatedProfile(string baseProfilesDir, Action<string> log)
    {
        Directory.CreateDirectory(baseProfilesDir);

        // Try Tuner_1 first, then Tuner_2, Tuner_3, Tuner_4
        for (int i = 1; i <= 4; i++)
        {
            string candidate = Path.Combine(baseProfilesDir, $"Tuner_{i}");
            if (!Directory.Exists(candidate))
                continue;

            if (!IsProfileInUseByChrome(candidate))
            {
                return candidate;
            }
            else if (i == 1)
            {
                log("[Sync] Notice: Tuner_1 is currently open in Chrome; checking synced backup tuner profiles...");
            }
        }

        // If Tuner_1 was left open from clicking "1. Login to Provider", close the idle login browser so Tuner_1 can be used
        string tuner1 = Path.Combine(baseProfilesDir, "Tuner_1");
        Directory.CreateDirectory(tuner1);
        KillChromeUsingProfile(tuner1, log);
        return tuner1;
    }

    private static bool IsProfileInUseByChrome(string profileDir)
    {
        string cookiesPath = Path.Combine(profileDir, "Default", "Network", "Cookies");
        if (!File.Exists(cookiesPath))
            return false;

        try
        {
            using var fs = new FileStream(cookiesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void KillChromeUsingProfile(string profileDir, Action<string> log)
    {
        try
        {
            // Clean up stale lockfile if present
            string lockFile = Path.Combine(profileDir, "lockfile");
            if (File.Exists(lockFile))
            {
                foreach (var proc in Process.GetProcessesByName("chrome"))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                Thread.Sleep(400);
            }
        }
        catch (Exception ex)
        {
            log($"[Sync] Warning releasing profile lock: {ex.Message}");
        }
    }

    private static string GetYouTubeTvScraperJs() => """
        (async () => {
          if (location.pathname.includes('/welcome')) {
            return JSON.stringify({ status: 'not_logged_in', url: location.href });
          }

          if (!location.pathname.startsWith('/live')) {
            location.href = 'https://tv.youtube.com/live';
            return JSON.stringify({ status: 'navigating_to_live', url: location.href });
          }

          const container = document.querySelector('#id-bottom-main-content') 
            || document.querySelector('.bottom-main-content');
          const initialRows = document.querySelectorAll('ytu-epg-row');

          if (!container || initialRows.length === 0) {
            return JSON.stringify({ status: 'waiting_for_grid', url: location.href, title: document.title });
          }

          // Ensure Polymer has populated .data on the mounted rows before starting the scroll pass
          const firstData = initialRows[0]?.data;
          if (!firstData || (!firstData.station && !firstData.navigationEndpoint)) {
            return JSON.stringify({ status: 'waiting_for_polymer_data', url: location.href });
          }

          const channels = new Map();
          let lastTop = -1;
          let stagnantPasses = 0;
          const maxPasses = 120;
          let chNum = 1;

          container.scrollTop = 0;
          await new Promise(r => setTimeout(r, 200));

          for (let i = 0; i < maxPasses; i++) {
            const rows = container.querySelectorAll('ytu-epg-row');

            for (const r of rows) {
              const d = r.data || {};
              const st = d.station?.epgStationRenderer || {};

              const ep = d.navigationEndpoint?.watchEndpoint 
                || st.navigationEndpoint?.watchEndpoint 
                || d.airings?.[0]?.navigationEndpoint?.watchEndpoint;
              const videoId = ep?.videoId;

              if (!videoId) continue;

              // Key by permanent YouTube stationId (UC...) so duplicate display names (e.g. PBS NC, ESPN)
              // stay separate and M3U channel-id stays permanent across program changes
              const ytStationId = d.stationId || st.stationId || videoId;
              if (channels.has(ytStationId)) continue;

              const name = st.name?.runs?.[0]?.text 
                || st.callSign?.runs?.[0]?.text
                || st.icon?.accessibility?.accessibilityData?.label
                || r.getAttribute('aria-label')?.replace(/^watch\s+/i, '').trim()
                || ytStationId;

              const safeSlug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
              const shortHash = ytStationId.length > 6 ? ytStationId.slice(-6).toLowerCase() : ytStationId.toLowerCase();

              channels.set(ytStationId, {
                Id: `${safeSlug}-${shortHash}`,
                Name: name,
                ChannelNumber: String(chNum++),
                StationId: "",
                Url: `https://tv.youtube.com/watch/${videoId}`
              });
            }

            container.scrollTop += 650;
            await new Promise(r => setTimeout(r, 120));

            if (container.scrollTop === lastTop) {
              stagnantPasses++;
              if (stagnantPasses >= 3) break;
            } else {
              stagnantPasses = 0;
              lastTop = container.scrollTop;
            }
          }

          container.scrollTop = 0;
          return JSON.stringify(Array.from(channels.values()));
        })()
        """;

    private static string GetSpectrumScraperJs() => """
        (async () => {
          const clean = (s) => (s || '').replace(/^["']|["']$/g, '');
          const token = clean(localStorage.getItem('oauth_token'));
          const devId = clean(localStorage.getItem('device_id'));
          const list = [];

          if (token) {
            try {
              const hdrs = {
                'Authorization': `Bearer ${token}`,
                'device_id': devId,
                'x-client-id': 'stva-ovp',
                'x-client-version': '17.32.0.289483649'
              };

              // Check location (behindOwnModem / inMarket) just like FastChannels spectrum.py
              let availFlag = null;
              try {
                const locRes = await fetch('https://apis.spectrum.net/pinxt/customer/location/v1', { headers: hdrs });
                if (locRes.ok) {
                  const loc = await locRes.json();
                  if (loc && !loc.behindOwnModem) {
                    availFlag = loc.inMarket ? 'availableInMarket' : 'availableOutOfMarket';
                  }
                }
              } catch (e) {}

              const r = await fetch('https://apis-vid.spectrum.net/lantern/lrs/api/smarttv/channels/v3?streamVersion=5', { headers: hdrs });
              if (r.ok) {
                const rows = await r.json();
                const seen = new Set();
                for (const ch of rows) {
                  const name = (ch.networkName || ch.callSign || '').trim();
                  const callSign = (ch.callSign || '').trim();
                  const tms = String(ch.tmsGuideId || '').trim();
                  if (!ch.online || ch.onlineEntitled === false || !tms || !name || seen.has(tms)) continue;
                  if (availFlag && ch[availFlag] === false) continue;
                  if (/^~mc\d+/i.test(name) || /^SPCTRM\d*$/i.test(callSign)) continue;
                  seen.add(tms);
                  list.push({
                    Id: `${(callSign || name).toLowerCase().replace(/[^a-z0-9]+/g, '-')}-${tms}`,
                    Name: name.replace(/ (?:HD|DT)(?: (?:East|West))?$/, ''),
                    ChannelNumber: String((ch.channelNumbers && ch.channelNumbers[0]) || ''),
                    StationId: tms,
                    Url: `https://watch.spectrum.net/livetv?tmsid=${tms}`
                  });
                }
              }
            } catch (e) {}
          }

          // Fallback to guide DOM if needed
          if (list.length === 0) {
            const seen = new Set();
            document.querySelectorAll('li.channel-header-row:not(.nonstreamable)').forEach(li => {
              const cs = li.querySelector('p.callsign')?.textContent?.trim() || '';
              const num = li.querySelector('p.channel-number')?.textContent?.trim() || '';
              const tms = (li.querySelector('img.channel-logo')?.getAttribute('src') || '').match(/\/guide\/(\d+)\?/)?.[1] || '';
              const idx = li.getAttribute('channel-index') || '';
              const rawRow = document.getElementById(`rowheader-${idx}`)?.textContent?.trim() || '';
              const name = rawRow.replace(/^Channel \d+ /, '') || cs;
              if (!tms || seen.has(tms) || name.startsWith('~') || /^Spectrum\d/i.test(name)) return;
              seen.add(tms);
              list.push({
                Id: `${cs.toLowerCase().replace(/[^a-z0-9]+/g, '-')}-${tms}`,
                Name: name.replace(/ (?:HD|DT)(?: (?:East|West))?$/, ''),
                ChannelNumber: num,
                StationId: tms,
                Url: `https://watch.spectrum.net/livetv?tmsid=${tms}`
              });
            });
          }

          if (list.length === 0) {
            return JSON.stringify({ status: token ? 'waiting_for_spectrum_guide' : 'not_logged_in_no_oauth_token', url: location.href });
          }

          return JSON.stringify(list);
        })()
        """;
		
	private static string GetXfinityScraperJs() => """
        (() => {
          const queryAllDeep = (selector, root = document) => {
            const list = Array.from(root.querySelectorAll(selector));
            const all = root.querySelectorAll('*');
            for (const el of all) {
              if (el.shadowRoot) list.push(...queryAllDeep(selector, el.shadowRoot));
            }
            return list;
          };

          const grid = document.querySelector('tv-page-grid') || queryAllDeep('tv-page-grid')[0];
          const rawChannels = grid && Array.isArray(grid.channels) ? grid.channels : null;
          if (!rawChannels || rawChannels.length === 0) {
            return JSON.stringify([]);
          }

          // Clean HD/SD suffixes only when the base callSign remains >= 3 chars (protects ID, WE, E!, FX)
          const normalizeCallSign = (cs) => {
            const clean = String(cs || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
            const stripped = clean.replace(/(?:HD|DT\d*|D|H)$/, '');
            return stripped.length >= 3 ? stripped : clean;
          };

          // Map known sister channels that share a parent companyId into distinct sub-keys
          const getSisterSubKey = (upperCall) => {
            // ESPN family (all share companyId 7642769970651506105)
            if (['ESPN2', 'ES2HD'].includes(upperCall)) return 'ESPN2';
            if (['ESPNN', 'ESWHD', 'ESPNEWS'].includes(upperCall)) return 'ESPNEWS';
            if (upperCall.startsWith('ESPNU')) return 'ESPNU';
            if (upperCall.startsWith('ESPN')) return 'ESPN';

            // Hallmark family (all share companyId 7889068523651137105)
            if (['HMYS', 'HMYSD'].includes(upperCall)) return 'HMYS';
            if (['HFM', 'HFMSD'].includes(upperCall)) return 'HFM';
            if (['HLMRK', 'HCHD'].includes(upperCall)) return 'HLMRK';

            // Nickelodeon family (share companyId 7294331549102474105)
            if (upperCall === 'NTOON') return 'NTOON';
            if (['NICM', 'NICK2'].includes(upperCall)) return 'NICK2';

            return null;
          };

          const scoreChannel = (c) => {
            const num = Number(c.number || 0);
            let score = 0;
            // Strongly prefer the primary 1000-1899 HD tier
            if (num >= 1000 && num <= 1899) score += 500;
            if (c.isHD) score += 100;
            if (!Array.isArray(c.restrictStreaming) || !c.restrictStreaming.includes('out-of-home')) score += 20;
            return score;
          };

          const bestByNetwork = new Map();

          for (const c of rawChannels) {
            if (!c || c.entitled === false) continue;

            const num = Number(c.number || 0);
            // Keep only core linear channels (1-1899); drops 3000+ international, 10000+ duplicates, 13000+ League Pass, 14000+ FAST
            if (num <= 0 || num > 1899) continue;

            // Drop local OTA subchannel diginets (1150-1195: MeTV, Buzzr, Catchy, Story, Grit, Comet, Charge, PBS Kids/Create)
            // Remove this single line if you WANT local OTA subchannels included
            if (num >= 1150 && num <= 1195) continue;

            const rawCall = String(c.callSign || '').trim();
            const companyId = String(c.companyId || c.networkAffiliateCompanyId || '').trim();
            if (!rawCall || !companyId) continue;

            const hint = String(c.callSignVoiceOverHint || '').trim();
            const upperHint = hint.toUpperCase();
            const upperCall = rawCall.toUpperCase();

            // 1. Filter out Sports Overflow, League Pass, and Extra Innings
            if (upperCall.endsWith('LP') || /^BTFB\d/i.test(upperCall) || upperHint.includes('LEAGUE PASS')) continue;

            // 2. Filter out Music Choice, FAST, Xumo, Local/Public Access, and ScreenPix barkers
            if (/^MUSIC CHOICE/i.test(upperHint) || /^MC\d+$/.test(upperCall) || upperCall.startsWith('~MC')) continue;
            if (upperHint === 'XUMO PLAY' || upperCall.includes('XUMO') || upperCall.startsWith('FAST')) continue;
            if (upperHint.includes('LOCAL PROGRAMMING') || upperHint.includes('LOCAL NEWS AND WEATHER')) continue;
            if (upperHint.includes('PUBLIC AFFAIRS') || upperHint.includes('PUBLIC ACCESS') || upperHint.includes('PEG')) continue;
            if (upperCall.includes('EVENTS') || upperHint.includes('SPECIALS AND EVENTS')) continue;
            if (upperHint.includes('SCREENPIX') || upperCall.startsWith('SCPX') || upperCall.startsWith('SPX')) continue;
            if (upperHint.includes('ENTERTAINMENT STUDIOS')) continue; // JusticeCentral/Recipe.TV/Comedy.TV fillers

            // 3. Filter out Shopping, Jewelry & Religious networks
            if (/^(?:QVC|HSN|JTV|SHPLC|SHOPL|GEMS)/i.test(upperCall)) continue;
            if (upperHint.includes('SHOPPING') || upperHint.includes('QVC') || upperHint.includes('HSN') || upperHint.includes('JEWELRY') || upperHint.includes('GEMSTONE')) continue;
            if (['TBN', 'TBNHD', 'EWTN', 'EWTNH', 'GLC', 'SBN', 'WHMB', 'POSTV', 'KONCS', 'KONCH', 'INSPD', 'IMPCT', 'JBSHD', 'FETVH', 'NTD24'].includes(upperCall)) continue;
            if (upperHint.includes('TRINITY BROADCASTING') || upperHint.includes('CHRISTIAN') || upperHint.includes('GOD TV') || upperHint.includes('ETERNAL WORD')) continue;

            // 4. Determine deduplication key:
            // Because Xfinity gives SD (COM/HIST/NGEO) and HD (CMDYD/HISHD/NGCHD) the exact same companyId,
            // keying by companyId automatically merges SD + HD pairs!
            const isPremium = /^(?:HBO|SHO|PSHO|STZ|MAX|ENC|TMC|MGM|FLIX)/i.test(upperCall);
            const sisterKey = getSisterSubKey(upperCall);

            let mapKey;
            if (sisterKey) {
              mapKey = `SISTER_${sisterKey}`;
            } else if (isPremium) {
              // Collapse only exact SD/HD pairs of the same premium feed (e.g. HBO-E + HBOHD, HBO2 + HBO2H, TMCe + TMCHD)
              mapKey = `PREM_${normalizeCallSign(upperCall)}`;
            } else {
              mapKey = `COMP_${companyId}`;
            }

            const existing = bestByNetwork.get(mapKey);
            if (!existing || scoreChannel(c) > scoreChannel(existing)) {
              bestByNetwork.set(mapKey, c);
            }
          }

          const results = [];
          const sorted = Array.from(bestByNetwork.values())
            .sort((a, b) => Number(a.number || 99999) - Number(b.number || 99999));

          for (const c of sorted) {
            const callSign = String(c.callSign).trim();
            const upperCall = callSign.toUpperCase();
            const companyId = String(c.companyId || c.networkAffiliateCompanyId).trim();
            const num = String(c.number || results.length + 1);

            const hint = String(c.callSignVoiceOverHint || '').trim();
            let displayName = hint || callSign;

            // Clean up shared parent voiceover names so Channels DVR and the user see the true channel name
            if (['ESPN2', 'ES2HD'].includes(upperCall)) displayName = 'ESPN2';
            else if (['ESPNN', 'ESWHD'].includes(upperCall)) displayName = 'ESPNews';
            else if (['HMYS', 'HMYSD'].includes(upperCall)) displayName = 'Hallmark Mystery';
            else if (['HFM', 'HFMSD'].includes(upperCall)) displayName = 'Hallmark Family';
            else if (upperCall === 'NTOON') displayName = 'Nicktoons';
            else if (['HBO', 'SHOWTIME', 'PARAMOUNT+ WITH SHOWTIME', 'CINEMAX', 'STARZ', 'MGM+', 'THE MOVIE CHANNEL'].includes(hint.toUpperCase()) && upperCall !== hint.toUpperCase()) {
              displayName = `${hint} (${callSign})`;
            }

            const safeId = callSign.toLowerCase().replace(/[^a-z0-9]+/g, '') + '-xf';

            results.push({
              Id: safeId,
              Name: displayName,
              ChannelNumber: num,
              StationId: '',
              Url: `https://www.xfinity.com/stream/live/${encodeURIComponent(callSign)}/${companyId}/${encodeURIComponent(callSign)}`
            });
          }

          return JSON.stringify(results);
        })()
        """;

    private static string GetSlingScraperJs() => """
        (() => {
          const path = location.pathname;
          const status = (s, extra) => JSON.stringify(Object.assign({ status: s, url: location.href }, extra || {}));

          // "Who's Watching?" appears on every fresh page load unless "Don't Ask Again" was chosen
          if (path.startsWith('/switch_user_profile')) {
            const avatar = document.querySelector('[data-testid^="SwitchUserProfilesIcons-0"] .Card-Avatar-Interaction-Container');
            if (avatar) avatar.click();
            return status(avatar ? 'selecting_profile' : 'waiting_for_profile_picker');
          }

          if (!path.startsWith('/dashboard/grid_guide')) {
            if (path.startsWith('/dashboard')) {
              history.pushState({}, '', '/dashboard/grid_guide/grid_guide_a_z');
              dispatchEvent(new PopStateEvent('popstate'));
              return status('opening_guide');
            }
            return status(location.hostname === 'watch.sling.com' ? 'waiting_for_sling' : 'not_logged_in');
          }

          // The guide is a react-window list; its itemData.getChannel(i) holds every row, not just the rendered ones
          const cell = document.querySelector('img.grid-cell-image') || document.querySelector('[data-testid^="channel-"]');
          const fiberKey = cell && Object.keys(cell).find((k) => k.startsWith('__reactFiber'));
          let fiber = fiberKey ? cell[fiberKey] : null;
          let list = null;
          for (let i = 0; fiber && i < 40; i++, fiber = fiber.return) {
            const p = fiber.memoizedProps;
            if (p && p.itemData && typeof p.itemData.getChannel === 'function') { list = p; break; }
          }
          if (!list) return status('waiting_for_guide');

          const total = Number(list.itemCount || 0);
          const results = [];
          const seen = new Set();
          let missing = 0;

          for (let i = 0; i < total; i++) {
            let c = null;
            try { c = list.itemData.getChannel(i); } catch (e) {}
            if (!c || !c.id) { missing++; continue; }
            if (c.format && c.format !== 'GRID_GUIDE_CHANNEL') continue;

            const guid = String(c.id).toLowerCase();
            if (!/^[0-9a-f]{32}$/.test(guid) || seen.has(guid)) continue;
            seen.add(guid);

            const adobe = (c.actions && c.actions.DETAIL_VIEW && c.actions.DETAIL_VIEW.adobe) || {};
            const name = String(c.title || adobe.ChannelName || '').trim();
            const callSign = String(adobe.CallSign || '').trim();
            // e.g. "Starz:224" for a subscription, "Freestream:658" for Sling's free channels
            const pkg = String(adobe.PackageName || '');
            if (!name) continue;

            results.push({
              Id: `${(callSign || name).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}-${guid.slice(0, 8)}`,
              Name: name,
              ChannelNumber: '',
              StationId: '',
              Url: `https://watch.sling.com/1/channel/${guid}/watch`,
              IsHidden: /^Freestream\b/i.test(pkg)
            });
          }

          // Rows load a page at a time; scroll the list until they all have data (the last row is a placeholder)
          if (missing > 1) {
            let scroller = cell.parentElement;
            while (scroller && !(scroller.scrollHeight > scroller.clientHeight + 10 && /auto|scroll/.test(getComputedStyle(scroller).overflowY))) {
              scroller = scroller.parentElement;
            }
            const atBottom = !scroller || scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 2;
            window.__charonSlingBottomHits = atBottom ? (window.__charonSlingBottomHits || 0) + 1 : 0;
            if (window.__charonSlingBottomHits < 2) {
              if (scroller) scroller.scrollTop += scroller.clientHeight * 5;
              return status('loading_guide_rows', { loaded: results.length, total });
            }
          }

          return JSON.stringify(results);
        })()
        """;

    // Direct, verified Gracenote TMS StationId table (East/Central/Mountain ID, Pacific ID)
    // Bypasses fuzzy text search completely for core national networks!
    private static readonly Dictionary<string, (string EastId, string WestId)> VerifiedGracenoteStationIds =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Kids & Family
        { "Disney Channel",             ("59684",  "59685") },
        { "Disney Junior",              ("74885",  "75004") },
        { "Disney XD",                  ("60006",  "60007") },
        { "Nickelodeon",                ("59432",  "59433") },
        { "Nick Jr.",                   ("82649",  "82650") },
        { "Nicktoons",                  ("30420",  "30420") },
        { "TeenNick",                   ("59036",  "59036") },
        { "Cartoon Network",            ("60048",  "60049") },
        { "Discovery Family",           ("67749",  "67749") },
        { "Discovery Family Channel",   ("67749",  "67749") },
        { "Universal Kids",             ("70225",  "70225") },
        { "PBS Kids",                   ("101364", "101364") },
        { "Public Broadcasting System Kids", ("101364", "101364") },

        // General Entertainment
        { "E!",                         ("61812",  "94563") },
        { "E! Entertainment Television",("61812",  "94563") },
        { "Freeform",                   ("59615",  "59616") },
        { "USA",                        ("58452",  "74030") },
        { "USA Network",                ("58452",  "74030") },
        { "TNT",                        ("42642",  "61340") },
        { "Turner Network Television",  ("42642",  "61340") },
        { "TBS",                        ("58515",  "67890") },
        { "Turner Broadcasting System", ("58515",  "67890") },
        { "truTV",                      ("64490",  "65717") },
        { "FX",                         ("58574",  "58575") },
        { "FXX",                        ("66379",  "82571") },
        { "FXM",                        ("70253",  "70253") },
        { "FXM (FX Movie Channel)",     ("70253",  "70253") },
        { "Bravo",                      ("58625",  "73994") },
        { "SYFY",                       ("58623",  "65626") },
        { "Oxygen",                     ("70522",  "70522") },
        { "Oxygen True Crime",          ("70522",  "70522") },
        { "AMC",                        ("59337",  "92041") },
        { "American Movie Classics",    ("59337",  "92041") },
        { "BBC America",                ("64492",  "64492") },
        { "IFC",                        ("59444",  "59444") },
        { "Independent Film Channel",   ("59444",  "59444") },
        { "SundanceTV",                 ("71280",  "71280") },
        { "WE tv",                      ("59296",  "59296") },
        { "A&E",                        ("51529",  "73992") },
        { "Arts and Entertainment",     ("51529",  "73992") },
        { "History",                    ("57708",  "73993") },
        { "Lifetime",                   ("60150",  "73997") },
        { "Lifetime Networks",          ("60150",  "73997") },
        { "LMN",                        ("55887",  "55887") },
        { "Lifetime Movies",            ("55887",  "55887") },
        { "FYI",                        ("58988",  "58988") },
        { "fyi,",                       ("58988",  "58988") },
        { "VICE",                       ("18822",  "18822") },
        { "Comedy Central",             ("62420",  "62421") },
        { "Paramount",                  ("59186",  "59187") },
        { "Paramount Network",          ("59186",  "59187") },
        { "TV Land",                    ("73541",  "73541") },
        { "MTV",                        ("60964",  "60965") },
        { "Music Television",           ("60964",  "60965") },
        { "MTV2",                       ("75077",  "75077") },
        { "Music Television 2",         ("75077",  "75077") },
        { "MTV Classic",                ("88870",  "88870") },
        { "Music Television Classic",   ("88870",  "88870") },
        { "VH1",                        ("60046",  "60047") },
        { "VH-1: Video Hits One",       ("60046",  "60047") },
        { "BET",                        ("63236",  "63237") },
        { "Black Entertainment Television", ("63236", "63237") },
        { "BET Her",                    ("63220",  "63220") },
        { "CMT",                        ("59440",  "59440") },
        { "Logo",                       ("46762",  "46762") },
        { "Pop",                        ("68796",  "68796") },
        { "Hallmark Channel",           ("66268",  "66268") },
        { "Hallmark Mystery",           ("61522",  "61522") },
        { "Hallmark Family",            ("105723", "105723") },
        { "GFAM",                       ("82892",  "82892") },
        { "Great American Family",      ("82892",  "82892") },
        { "Game Show Network",          ("68827",  "68827") },
        { "Turner Classic Movies",      ("64312",  "64312") },
        { "TCM",                        ("64312",  "64312") },
        { "TV One",                     ("61960",  "61960") },
        { "Cleo TV",                    ("110288", "110288") },
        { "Bounce",                     ("73067",  "73067") },
        { "Comet TV",                   ("97051",  "97051") },
        { "Comet",                      ("97051",  "97051") },
        { "CHARGE!",                    ("102148", "102148") },
        { "Cozi",                       ("89994",  "89994") },
        { "Start TV",                   ("109454", "109454") },
        { "Start TV Network",           ("109454", "109454") },
        { "Dabl",                       ("112157", "112157") },
        { "ION",                        ("62746",  "62746") },
        { "ION Media Networks",         ("62746",  "62746") },
        { "Reelz",                      ("119136", "119136") },
        { "UP (Uplifting Entertainment)",("66143", "66143") },

        // Discovery / Nat Geo / Lifestyle
        { "Discovery Channel",          ("56905",  "56906") },
        { "Discovery",                  ("56905",  "56906") },
        { "Food Network",               ("50747",  "50748") },
        { "HGTV",                       ("49788",  "49789") },
        { "Home & Garden Television",   ("49788",  "49789") },
        { "TLC",                        ("57391",  "57392") },
        { "The Learning Channel",       ("57391",  "57392") },
        { "ID",                         ("65342",  "65342") },
        { "Investigation Discovery",    ("65342",  "65342") },
        { "Animal Planet",              ("57394",  "57395") },
        { "Travel Channel",             ("59303",  "59303") },
        { "Science",                    ("57390",  "57390") },
        { "Science Channel",            ("57390",  "57390") },
        { "The Science Channel",        ("57390",  "57390") },
        { "Discovery Turbo",            ("31046",  "31046") },
        { "MotorTrend",                 ("31046",  "31046") },
        { "OWN",                        ("70388",  "70389") },
        { "Oprah Winfrey Network",      ("70388",  "70389") },
        { "Magnolia Network",           ("67454",  "67454") },
        { "Cooking Channel",            ("68065",  "68065") },
        { "Destination America",        ("60468",  "60468") },
        { "American Heroes Channel",    ("78808",  "78808") },
        { "American Heroes",            ("78808",  "78808") },
        { "Discovery Life",             ("92204",  "92204") },
        { "Nat Geo",                    ("49438",  "71601") },
        { "National Geographic",        ("49438",  "71601") },
        { "National Geographic Channel",("49438",  "71601") },
        { "Nat Geo Wild",               ("67375",  "67375") },
        { "National Geographic Wild",   ("67375",  "67375") },
        { "Smithsonian Channel",        ("65799",  "65799") },

        // News & Business
        { "CNN",                        ("58646",  "58646") },
        { "Cable News Network",         ("58646",  "58646") },
        { "HLN",                        ("64549",  "64549") },
        { "Headline News",              ("64549",  "64549") },
        { "FOX News",                   ("60179",  "60179") },
        { "Fox News Channel",           ("60179",  "60179") },
        { "FOX Business",               ("58718",  "58718") },
        { "Fox Business Network",       ("58718",  "58718") },
        { "MSNBC",                      ("64241",  "64241") },
        { "MS NOW",                     ("64241",  "64241") },
        { "CNBC",                       ("58780",  "58780") },
        { "NewsNation",                 ("91096",  "91096") },
        { "NEWSMAX",                    ("97163",  "97163") },
        { "Newsmax TV",                 ("97163",  "97163") },
        { "BBC News",                   ("107709", "107709") },
        { "The Weather Channel",        ("58812",  "58812") },
        { "FOX Weather",                ("93141",  "93141") },
        { "C-SPAN",                     ("68344",  "68344") },
        { "C-SPAN2",                    ("68334",  "68334") },
        { "C-SPAN3",                    ("68332",  "68332") },

        // Sports
        { "ESPN",                       ("32645",  "32645") },
        { "Entertainment Sports Programming Network", ("32645", "32645") },
        { "ESPN2",                      ("45507",  "45507") },
        { "ESPNews",                    ("59976",  "59976") },
        { "ESPNU",                      ("60696",  "60696") },
        { "SEC Network",                ("89714",  "89714") },
        { "ACC Network",                ("111871", "111871") },
        { "FS1",                        ("82547",  "82547") },
        { "FOX Sports 1",               ("82547",  "82547") },
        { "FS2",                        ("59305",  "59305") },
        { "FOX Sports 2",               ("59305",  "59305") },
        { "BTN",                        ("58321",  "58321") },
        { "Big Ten Network",            ("58321",  "58321") },
        { "CBS Sports Network",         ("59250",  "59250") },
        { "NFL Network",                ("45399",  "45399") },
        { "National Football League Network", ("45399", "45399") },
        { "NBA TV",                     ("45526",  "45526") },
        { "National Basketball Association TV", ("45526", "45526") },
        { "MLB Network",                ("62081",  "62081") },
        { "Major League Baseball Network", ("62081", "62081") },
        { "NHL Network",                ("58690",  "58690") },
        { "National Hockey League Network", ("58690", "58690") },
        { "Golf Channel",               ("61854",  "61854") },
        { "Tennis Channel",             ("60316",  "60316") },

        // Premium (Starz family, as titled in the Sling guide; East and West are separate channels there)
        { "STARZ",                      ("34941",  "34941") },
        { "Starz West",                 ("34949",  "34949") },
        { "Starz Kids & Family",        ("57581",  "57581") },
        { "Starz Comedy",               ("57569",  "57569") },
        { "Starz Edge",                 ("57573",  "57573") },
        { "Starz in Black",             ("67235",  "67235") },
        { "Starz Cinema",               ("67236",  "67236") },
        { "Starz Encore",               ("36225",  "36225") },
        { "Starz Encore West",          ("17125",  "17125") },
        { "Starz Encore Classic",       ("14764",  "14764") },
        { "Starz Encore Westerns",      ("102906", "102906") },
        { "Starz Encore Suspense",      ("14766",  "14766") },
        { "Starz Encore Black",         ("14870",  "14870") },
        { "Starz Encore Action",        ("72015",  "72015") },
        { "Starz Encore Family",        ("102903", "102903") },
        { "IndiePlex",                  ("65795",  "65795") },
        { "RetroPlex",                  ("65791",  "65791") },
        { "MoviePlex",                  ("83075",  "83075") },

        // Entertainment Studios & Shopping
        { "Comedy.TV",                  ("82470",  "82470") },
        { "JusticeCentral.TV",          ("78850",  "78850") },
        { "Recipe.TV",                  ("71769",  "71769") },
        { "Cars.TV",                    ("69062",  "69062") },
        { "theGRIO",                    ("121310", "121310") },
        { "QVC",                        ("60222",  "60222") },
		{ "AWE",                        ("59436",  "59436") },
        { "HBCU GO",                    ("125163", "125163") },
        { "HSN",                        ("62077",  "62077") }
    };

    private static async Task<int> MatchGracenoteStationsAsync(
        List<ProviderChannel> channels,
        string dvrIp,
        int dvrPort,
        Action<string> log,
        Action<string> statusCallback)
    {
        string serverUrl = $"http://{dvrIp}:{dvrPort}";
        int updatedCount = 0;

        // Pacific (UTC-8), Alaska (UTC-9), Hawaii (UTC-10) use West/Pacific feeds;
        // Eastern (UTC-5), Central (UTC-6), and Mountain (UTC-7) use East feeds on YTTV.
        bool preferPacific = TimeZoneInfo.Local.BaseUtcOffset.TotalHours <= -8.0;

        int hiddenCount = channels.Count(c => c.IsHidden && string.IsNullOrWhiteSpace(c.StationId));
        if (hiddenCount > 0)
            log($"[Gracenote] Skipping {hiddenCount} hidden channels (unhide them and scan again to match their Gracenote IDs)");

        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            statusCallback($"Step 2/2: Matching Gracenote IDs ({i + 1} / {channels.Count}) — {ch.Name}...");

            if (!string.IsNullOrWhiteSpace(ch.StationId))
            {
                updatedCount++;
                continue;
            }

            if (ch.IsHidden)
                continue;

            string rawName = ch.Name.Trim();
            TmsStation? match = null;

            // 1. Check Verified National Network Gracenote StationId table first!
            if (VerifiedGracenoteStationIds.TryGetValue(rawName, out var verifiedIds))
            {
                ch.StationId = preferPacific ? verifiedIds.WestId : verifiedIds.EastId;
                updatedCount++;
                log($"[Gracenote] [OK] {ch.Name} -> Verified US HD [StationId: {ch.StationId}]");
                continue;
            }

            // 2. Skip vague local affiliate names that lack real FCC call letters (e.g. "FOX 46", "CW Charlotte", "TV64", "PBS NC", "SCETV")
            var xfMatch = System.Text.RegularExpressions.Regex.Match(ch.Url ?? "", @"/stream/live/([^/]+)/\d+/");
            string? urlCallSign = xfMatch.Success ? Uri.UnescapeDataString(xfMatch.Groups[1].Value) : null;

            if (string.IsNullOrWhiteSpace(urlCallSign) && IsVagueLocalStationName(rawName))
            {
                log($"[Gracenote] [--] {ch.Name}: Local station without FCC call letters (leaving blank for manual mapping in Channels DVR)");
                continue;
            }

            // 3. For Xfinity, query by the URL callSign first
            if (!string.IsNullOrWhiteSpace(urlCallSign))
            {
                var results = await QueryChannelsDvrTmsAsync(serverUrl, urlCallSign);
                match = SelectBestTmsStation(results, urlCallSign, rawName, preferPacific);

                if (match is null)
                {
                    string cleanCs = System.Text.RegularExpressions.Regex.Replace(
                        urlCallSign, @"(?:HD|DT\d*|D)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (cleanCs.Length >= 3 && !string.Equals(cleanCs, urlCallSign, StringComparison.OrdinalIgnoreCase))
                    {
                        results = await QueryChannelsDvrTmsAsync(serverUrl, cleanCs);
                        match = SelectBestTmsStation(results, cleanCs, rawName, preferPacific);
                    }
                }
            }

            // 4. If the channel starts with a real W/K broadcast call sign (e.g. "WBTV", "WSOC 9", "WCNC NBC"), query that exact call sign
            if (match is null && HasRealBroadcastCallSign(rawName, out string fccCallSign))
            {
                var results = await QueryChannelsDvrTmsAsync(serverUrl, fccCallSign);
                match = SelectBestTmsStation(results, fccCallSign, fccCallSign, preferPacific);
            }

            // 5. Fallback: Search by raw display name for remaining national/FAST networks
            if (match is null)
            {
                var results = await QueryChannelsDvrTmsAsync(serverUrl, rawName);
                match = SelectBestTmsStation(results, null, rawName, preferPacific);
            }

            if (match is not null && !string.IsNullOrWhiteSpace(match.StationId))
            {
                ch.StationId = match.StationId;
                updatedCount++;
                log($"[Gracenote] [OK] {ch.Name} -> {match.Name} ({match.CallSign}) [StationId: {match.StationId}]");
            }
            else
            {
                log($"[Gracenote] [??] {ch.Name}: No valid US match found (leaving blank for manual map)");
            }

            await Task.Delay(40);
        }

        return updatedCount;
    }

    private static bool HasRealBroadcastCallSign(string rawName, out string extractedCallSign)
    {
        extractedCallSign = "";
        string firstToken = rawName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        if (System.Text.RegularExpressions.Regex.IsMatch(firstToken, @"^[WK][A-Z]{2,3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            string upper = firstToken.ToUpperInvariant();
            if (upper is not ("WEST" or "KIDS" or "WILD" or "WE" or "WETV"))
            {
                extractedCallSign = upper;
                return true;
            }
        }

        return false;
    }

    private static bool IsVagueLocalStationName(string rawName)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(
            rawName,
            @"^(?:FOX|CBS|NBC|ABC|CW|MY|TV|CH|CHANNEL)\s*\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(
            rawName,
            @"^(?:CW|PBS|Telemundo|Univision|UniMas|MyNetworkTV)\s+[A-Za-z]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (rawName.Equals("SCETV", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static TmsStation? SelectBestTmsStation(
        List<TmsStation>? candidates,
        string? targetCallSign,
        string rawName,
        bool preferPacific)
    {
        if (candidates is null || candidates.Count == 0)
            return null;

        bool isSpanishChannel = rawName.Contains("Univision", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Telemundo", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("UniMas", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Galavision", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Galavisión", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("TUDN", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Deportes", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Universo", StringComparison.OrdinalIgnoreCase);

        TmsStation? best = null;
        int bestScore = int.MinValue;

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.StationId))
                continue;

            string name = c.Name?.Trim() ?? "";
            string call = c.CallSign?.Trim() ?? "";
            var langs = c.BcastLangs ?? [];

            // 1. Hard Reject: Inactive stations
            if (name.Contains("(inactive)", StringComparison.OrdinalIgnoreCase))
                continue;

            // 2. Hard Reject: Known international & FAST clone callSigns that pollute US searches
            if (call.EndsWith("UK", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("KR", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("IND", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("LAT", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("MUE", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("DAE", StringComparison.OrdinalIgnoreCase) ||
                call.StartsWith("VIZ", StringComparison.OrdinalIgnoreCase) ||
                call.StartsWith("PTV", StringComparison.OrdinalIgnoreCase) ||
                call.StartsWith("XSM", StringComparison.OrdinalIgnoreCase) ||
                call.Contains("FIFA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 3. Hard Reject: Non-English feeds (unless the channel itself is a Spanish network)
            if (!isSpanishChannel && langs.Count > 0)
            {
                bool hasUsEnglish = langs.Any(l =>
                    l.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                    l.Equals("en-US", StringComparison.OrdinalIgnoreCase));

                if (!hasUsEnglish)
                    continue;
            }

            int score = 0;

            // 4. CallSign match scoring
            if (!string.IsNullOrWhiteSpace(targetCallSign))
            {
                if (string.Equals(call, targetCallSign, StringComparison.OrdinalIgnoreCase))
                    score += 200;
                else if (call.StartsWith(targetCallSign, StringComparison.OrdinalIgnoreCase))
                    score += 120;
                else if (string.Equals(System.Text.RegularExpressions.Regex.Replace(call, @"DT\d*$", ""), targetCallSign, StringComparison.OrdinalIgnoreCase))
                    score += 150;
            }

            // 5. Name match scoring
            string cleanCandidateName = System.Text.RegularExpressions.Regex.Replace(name, @"\s+HD$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (string.Equals(cleanCandidateName, rawName, StringComparison.OrdinalIgnoreCase))
                score += 100;
            else if (name.Contains(rawName, StringComparison.OrdinalIgnoreCase))
                score += 40;

            // 6. HDTV preference over SDTV
            if (string.Equals(c.VideoQuality?.VideoType, "HDTV", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("HD", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("HDP", StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }

            // 7. East vs. Pacific Timezone Feed Alignment
            bool isWestFeed = call.EndsWith("P", StringComparison.OrdinalIgnoreCase) ||
                              call.EndsWith("HDP", StringComparison.OrdinalIgnoreCase) ||
                              call.EndsWith("HDW", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Pacific", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("West", StringComparison.OrdinalIgnoreCase);

            if (preferPacific)
            {
                score += isWestFeed ? 50 : -20;
            }
            else
            {
                score += isWestFeed ? -60 : 40;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }

    private static async Task<List<TmsStation>?> QueryChannelsDvrTmsAsync(string serverUrl, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return null;

        string encoded = Uri.EscapeDataString(searchTerm.Trim());
        string url = $"{serverUrl}/tms/stations/{encoded}";

        try
        {
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<TmsStation>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static int FindFreeLoopbackPort(int startPort)
    {
        for (int p = startPort; p < startPort + 100; p++)
        {
            try
            {
                using var l = new TcpListener(System.Net.IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { }
        }
        return startPort;
    }

    private static async Task<string?> WaitForPageWebSocketUrlAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json");
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    string url = el.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (type == "page" && !url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                    {
                        if (el.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                            return wsProp.GetString();
                    }
                }
            }
            catch { }
            await Task.Delay(300);
        }
        return null;
    }

    private static async Task<string?> EvaluateJsStringAsync(ClientWebSocket ws, int id, string expression)
    {
        await SendCdpCommandAsync(ws, id, "Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true
        });

        var buffer = new byte[65536];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(buffer, CancellationToken.None);
            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage)
            {
                string msg = Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                using var doc = JsonDocument.Parse(msg);
                if (doc.RootElement.TryGetProperty("id", out var msgId) && msgId.GetInt32() == id)
                {
                    if (doc.RootElement.TryGetProperty("result", out var resultEl) &&
                        resultEl.TryGetProperty("result", out var innerResult) &&
                        innerResult.TryGetProperty("value", out var valEl) &&
                        valEl.ValueKind == JsonValueKind.String)
                    {
                        return valEl.GetString();
                    }
                    return null;
                }
            }
        }
        return null;
    }

    private static async Task SendCdpCommandAsync(ClientWebSocket ws, int id, string method, object parameters)
    {
        string payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

   private sealed class TmsStation
    {
        public string? StationId { get; set; }
        public string? Name { get; set; }
        public string? CallSign { get; set; }
        public string? Type { get; set; }
        public List<string>? BcastLangs { get; set; }
        public TmsVideoQuality? VideoQuality { get; set; }
    }

    private sealed class TmsVideoQuality
    {
        public string? SignalType { get; set; }
        public string? VideoType { get; set; }
        public string? TruResolution { get; set; }
    }
}