using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Charon.Config;
using Charon.Providers;

namespace Charon.Tuner;

public sealed class ChromeTuner
{
    private readonly CharonConfig _config;
    private static readonly HttpClient Http = new();
    private static readonly object ProfileSyncLock = new();

    private int WindowWidth => _config.Enable1080p ? 1920 : 1280;
    private int WindowHeight => _config.Enable1080p ? 1112 : 752;
    private int ViewportWidth => _config.Enable1080p ? 1920 : 1280;
    private int ViewportHeight => _config.Enable1080p ? 1080 : 720;
    private int VideoBitrate => _config.Enable1080p ? 12000000 : 8000000;

    public ChromeTuner(CharonConfig config)
    {
        _config = config;
    }

    public string GetTunerProfilePath(int tunerId)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Profiles", $"Tuner_{tunerId}");
        Directory.CreateDirectory(path);
        return path;
    }

    public Process? LaunchInteractiveLogin(string loginUrl, Action<string>? logCallback = null)
    {
        string primaryTunerProfile = GetTunerProfilePath(1);
        KillChromeProcessesForPortOrProfile(48601, primaryTunerProfile);
        TryDeleteFile(Path.Combine(primaryTunerProfile, "SingletonLock"));

        var psi = new ProcessStartInfo
        {
            FileName = _config.ResolvedChromePath,
            Arguments = string.Join(" ",
            [
                $"--user-data-dir=\"{primaryTunerProfile}\"",
				"--remote-debugging-port=48601",
                "--no-first-run",
                "--no-default-browser-check",
                "--autoplay-policy=no-user-gesture-required",
                "--disable-features=HardwareMediaKeyHandling,MediaSessionService,Translate",
                "--window-position=50,50",     
                "--window-size=1280,800",       
                $"\"{loginUrl}\""
            ]),
            UseShellExecute = false
        };

        var proc = Process.Start(psi);
        if (proc != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await proc.WaitForExitAsync();
                    await Task.Delay(500);
                    SyncMasterAuthToAllTuners(logCallback);
                }
                catch { }
            });
        }

        return proc;
    }

    public void SyncMasterAuthToAllTuners(Action<string>? logCallback = null)
    {
        for (int id = 2; id <= 4; id++)
        {
            try
            {
                KillChromeProcessesForPortOrProfile(48600 + id, GetTunerProfilePath(id));
                SyncAuthFromTuner1(id, forceFullSync: true);
            }
            catch { }
        }
        logCallback?.Invoke("[Auth] Single Sign-In session synced from Tuner 1 to Tuners 2, 3, and 4.");
    }

    public async Task StreamChannelAsync(
        int tunerId,
        LoadedProvider provider,
        ProviderChannel channel,
        Stream outputStream,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        var tuneTimer = Stopwatch.StartNew();
        int debugPort = 48600 + tunerId;
        int wsPort = 48700 + tunerId;
        string tunerProfile = PrepareTunerProfile(tunerId, debugPort);

        (string extDir, string extensionId, string extVersion) = EnsureTabCaptureExtension(tunerId, wsPort, provider);

        using var httpListener = new HttpListener();
        httpListener.Prefixes.Add($"http://127.0.0.1:{wsPort}/");
        httpListener.Start();

        string chromeArgs = string.Join(" ",
        [
            $"--user-data-dir=\"{tunerProfile}\"",
            $"--remote-debugging-port={debugPort}",
            "--remote-allow-origins=*",
            "--enable-unsafe-extension-debugging",
            $"--whitelisted-extension-id={extensionId}",
            $"--allowlisted-extension-id={extensionId}",
            $"--app=\"{channel.Url}\"",
            $"--window-size={WindowWidth},{WindowHeight}",
            "--force-device-scale-factor=1",
            "--force-color-profile=srgb",
            "--autoplay-policy=no-user-gesture-required",
            "--enable-gpu-rasterization",
            "--enable-zero-copy",
            "--ignore-gpu-blocklist",
            "--enable-accelerated-video-decode",
            "--enable-accelerated-video-encode",
            "--disable-background-media-suspend",
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--disable-ipc-flooding-protection",
            "--disable-features=HardwareMediaKeyHandling,MediaSessionService,Translate,CalculateNativeWinOcclusion,WindowOcclusion,IntensiveWakeUpThrottling,ThrottleDisplayNoneAndVisibilityHiddenCrossOriginIframes,UseEcoQoSForBackgroundProcess,PreloadMediaEngagementData,MediaEngagementBypassAutoplayPolicies",
            "--disable-session-crashed-bubble",
            "--disable-infobars",
            "--disable-notifications",
            "--hide-crash-restore-bubble",
            "--hide-scrollbars",
            "--no-first-run",
            "--no-default-browser-check"
        ]);

        logCallback?.Invoke($"[Tuner {tunerId}] Launching Chrome (Ext v{extVersion}) for {provider.Manifest.Name} -> {channel.Name}...");

        using var chromeProcess = Process.Start(new ProcessStartInfo
        {
            FileName = _config.ResolvedChromePath,
            Arguments = chromeArgs,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start Chrome process.");

        try { chromeProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

        Process? ffmpegProcess = null;

        try
        {
            // 1. Load extension and place window off-screen
            _ = Task.Run(() => LoadExtensionAndPlaceWindowAsync(chromeProcess.Id, debugPort, extDir, provider, logCallback, tunerId, ct), ct);

            // 2. Launch FFmpeg with smooth 60fps frame pacing
            string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            string ffmpegArgs = BuildFfmpegArguments();

            ffmpegProcess = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = ffmpegArgs,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Failed to start FFmpeg.");

            try { ffmpegProcess.PriorityClass = ProcessPriorityClass.High; } catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested &&
                           await ffmpegProcess.StandardError.ReadLineAsync(ct) is { } line)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            logCallback?.Invoke($"[FFmpeg {tunerId}] {line}");
                    }
                }
                catch { }
            }, ct);

            // 3. Create bounded memory queue & dedicated FFmpeg stdin writer pump
            var pipeChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true
            });

            var ffmpegWriterTask = Task.Run(async () =>
            {
                try
                {
                    Stream ffmpegIn = ffmpegProcess.StandardInput.BaseStream;
                    await foreach (var chunk in pipeChannel.Reader.ReadAllAsync(ct))
                    {
                        await ffmpegIn.WriteAsync(chunk.AsMemory(0, chunk.Length), ct);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                }
                finally
                {
                    try { ffmpegProcess.StandardInput.Close(); } catch { }
                }
            }, ct);

            // 4. Accept WebSocket stream connection from offscreen.js
            var listenerTask = Task.Run(async () =>
            {
                try
                {
                    using var reg = ct.Register(() => { try { httpListener.Stop(); } catch { } });
                    long totalBytesWritten = 0;

                    while (!ct.IsCancellationRequested)
                    {
                        var ctx = await httpListener.GetContextAsync();
                        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

                        if (ctx.Request.Url?.AbsolutePath == "/log")
                        {
                            string msg = ctx.Request.QueryString["msg"] ?? "";
                            logCallback?.Invoke($"[Ext {tunerId}] {msg}");
                            ctx.Response.StatusCode = 200;
                            ctx.Response.Close();
                            continue;
                        }

                        if (ctx.Request.IsWebSocketRequest)
                        {
                            var wsWrapper = await ctx.AcceptWebSocketAsync(null, receiveBufferSize: 65536, keepAliveInterval: TimeSpan.FromSeconds(30));
                            using var ws = wsWrapper.WebSocket;
                            byte[] buffer = new byte[65536];
                            bool loggedStart = false;

                            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                            {
                                var result = await ws.ReceiveAsync(buffer, ct);
                                if (result.MessageType == WebSocketMessageType.Close)
                                    break;

                                if (result.Count > 0)
                                {
                                    if (!loggedStart)
                                    {
                                        loggedStart = true;
                                        logCallback?.Invoke($"[Tuner {tunerId}] WebM stream active at +{tuneTimer.Elapsed.TotalSeconds:F1}s -> piping to FFmpeg...");
                                    }

                                    byte[] chunk = new byte[result.Count];
                                    Buffer.BlockCopy(buffer, 0, chunk, 0, result.Count);

                                    await pipeChannel.Writer.WriteAsync(chunk, ct);
                                    totalBytesWritten += result.Count;
                                }
                            }

                            if (totalBytesWritten > 0)
                                break;
                        }
                        else
                        {
                            ctx.Response.StatusCode = 400;
                            ctx.Response.Close();
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or WebSocketException)
                {
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"[Tuner {tunerId}] Listener error: {ex.Message}");
                }
                finally
                {
                    pipeChannel.Writer.TryComplete();
                }
            }, ct);

            // 5. Read FFmpeg's MPEG-TS stdout and flush immediately on first packet
            Stream ffmpegOut = ffmpegProcess.StandardOutput.BaseStream;
            byte[] outBuf = new byte[32768];
            bool firstTsPacketLogged = false;

            while (!ct.IsCancellationRequested)
            {
                int read = await ffmpegOut.ReadAsync(outBuf, ct);
                if (read <= 0) break;

                await outputStream.WriteAsync(outBuf.AsMemory(0, read), ct);

                if (!firstTsPacketLogged)
                {
                    firstTsPacketLogged = true;
                    await outputStream.FlushAsync(ct);
                    logCallback?.Invoke($"[Tuner {tunerId}] First MPEG-TS packet delivered to Client at +{tuneTimer.Elapsed.TotalSeconds:F1}s!");
                }
            }

            await listenerTask;
            await ffmpegWriterTask;
        }
        catch (OperationCanceledException)
        {
            logCallback?.Invoke($"[Tuner {tunerId}] Stream closed by client ({channel.Name}).");
        }
        finally
        {
            try { httpListener.Stop(); } catch { }

            try
            {
                if (ffmpegProcess is { HasExited: false })
                    ffmpegProcess.Kill(entireProcessTree: true);
            }
            catch { }

            KillChromeProcessesForPortOrProfile(debugPort, tunerProfile);

            try
            {
                chromeProcess.Kill(entireProcessTree: true);
            }
            catch { }

            TryDeleteFile(Path.Combine(tunerProfile, "SingletonLock"));
        }
    }

    private static string BuildFfmpegArguments()
    {
        return string.Join(" ",
        [
            "-hide_banner -loglevel warning",
            "-fflags +genpts+igndts+discardcorrupt",
            "-probesize 524288 -analyzeduration 1000000",
            "-f webm -i pipe:0",
            "-map 0:v:0 -map 0:a:0?",
            "-fps_mode passthrough -c:v copy -bsf:v h264_mp4toannexb,dump_extra",
            "-c:a aac -b:a 192k -ar 48000 -ac 2",
            "-af \"aresample=async=1:min_hard_comp=0.100000:first_pts=0\"",
            "-avoid_negative_ts make_zero -flush_packets 1",
            "-pat_period 0.25 -sdt_period 0.25 -mpegts_flags +initial_discontinuity+resend_headers -f mpegts pipe:1"
        ]);
    }

    private async Task LoadExtensionAndPlaceWindowAsync(
        int chromeProcessId,
        int debugPort,
        string extDir,
        LoadedProvider provider,
        Action<string>? logCallback,
        int tunerId,
        CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            IntPtr hwnd = await WaitForChromeWindowAsync(chromeProcessId, ct);
            if (hwnd != IntPtr.Zero)
            {
                ApplyWindowPlacement(hwnd, tunerId);
            }
        }, ct);

        try
        {
            string? browserWsUrl = null;
            for (int attempt = 0; attempt < 200; attempt++) // Up to 10 seconds for slower PCs
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string verJson = await Http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/version", ct);
                    using var verDoc = JsonDocument.Parse(verJson);
                    if (verDoc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var browserWsProp))
                    {
                        browserWsUrl = browserWsProp.GetString();
                        if (!string.IsNullOrWhiteSpace(browserWsUrl)) break;
                    }
                }
                catch { }
                await Task.Delay(50, ct);
            }

            if (!string.IsNullOrWhiteSpace(browserWsUrl))
            {
                using var bws = new ClientWebSocket();
                await bws.ConnectAsync(new Uri(browserWsUrl), ct);
                string? extResp = await SendCdpCommandAndWaitAsync(bws, 1, "Extensions.loadUnpacked", new { path = extDir }, ct);
                if (extResp != null && extResp.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
                {
                    logCallback?.Invoke($"[Tuner {tunerId}] Extensions.loadUnpacked error: {extResp}");
                }
            }
            else
            {
                logCallback?.Invoke($"[Tuner {tunerId}] ERROR: Timed out waiting for Chrome debug port {debugPort} (is another Chrome window open in Profile Tuner_{tunerId}?).");
            }

            string? pageWsUrl = null;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string json = await Http.GetStringAsync($"http://127.0.0.1:{debugPort}/json", ct);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        if (elem.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "page" &&
                            elem.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                        {
                            pageWsUrl = wsProp.GetString();
                            break;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(pageWsUrl)) break;
                }
                catch { }
                await Task.Delay(15, ct);
            }

            if (!string.IsNullOrWhiteSpace(pageWsUrl))
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(pageWsUrl), ct);
                await SendCdpCommandAsync(ws, 1, "Page.enable", new { }, ct);
                await SendCdpCommandAsync(ws, 2, "Emulation.setFocusEmulationEnabled", new { enabled = true }, ct);

                // Lock the viewport to exact dimensions with zero border!
                await SendCdpCommandAsync(ws, 3, "Emulation.setDeviceMetricsOverride", new
                {
                    width = ViewportWidth,
                    height = ViewportHeight,
                    deviceScaleFactor = 1,
                    mobile = false
                }, ct);

                var cdpScriptBuilder = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(provider.StyleCss))
                {
                    string escapedCss = JsonEncodedText.Encode(provider.StyleCss).ToString();
                    cdpScriptBuilder.AppendLine($"(function(){{ let s = document.getElementById('charon-css'); if (!s && (document.head || document.documentElement)) {{ s = document.createElement('style'); s.id = 'charon-css'; (document.head || document.documentElement).appendChild(s); }} if (s && s.textContent !== \"{escapedCss}\") s.textContent = \"{escapedCss}\"; }})();");
                }
                if (!string.IsNullOrWhiteSpace(provider.TuneJs))
                {
                    cdpScriptBuilder.AppendLine(provider.TuneJs);
                }
                string combinedCdpScript = cdpScriptBuilder.ToString();

                if (!string.IsNullOrWhiteSpace(combinedCdpScript))
                {
                    await SendCdpCommandAsync(ws, 4, "Page.addScriptToEvaluateOnNewDocument", new { source = combinedCdpScript }, ct);
                }

                int msgId = 10;
                for (int i = 0; i < 18; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(350, ct);

                    if (!string.IsNullOrWhiteSpace(combinedCdpScript))
                    {
                        await SendCdpCommandAsync(ws, msgId++, "Runtime.evaluate", new
                        {
                            expression = combinedCdpScript,
                            userGesture = true
                        }, ct);
                    }

                    if (i is 4 or 8)
                    {
                        bool isPaused = await CheckIfVideoPausedAsync(ws, msgId++, ct);
                        if (isPaused)
                        {
                            logCallback?.Invoke($"[Tuner {tunerId}] Sending trusted CDP hardware click to unpause player...");
                            await SendCdpCommandAsync(ws, msgId++, "Input.dispatchMouseEvent", new
                            {
                                type = "mousePressed",
                                x = 640,
                                y = 360,
                                button = "left",
                                clickCount = 1
                            }, ct);
                            await Task.Delay(30, ct);
                            await SendCdpCommandAsync(ws, msgId++, "Input.dispatchMouseEvent", new
                            {
                                type = "mouseReleased",
                                x = 640,
                                y = 360,
                                button = "left",
                                clickCount = 1
                            }, ct);
                        }
                    }
                }

                await Task.Delay(Timeout.Infinite, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logCallback?.Invoke($"[Tuner {tunerId}] CDP setup warning: {ex.Message}");
        }
    }

    private static async Task<bool> CheckIfVideoPausedAsync(ClientWebSocket ws, int id, CancellationToken ct)
    {
        try
        {
            const string expr = """
            (function() {
              if (typeof window.__charonIsPaused === 'function') {
                return Boolean(window.__charonIsPaused());
              }
              const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
              if (player && typeof player.getPlayerState === 'function') {
                const st = player.getPlayerState();
                if (st === 1) return false;
                if (st === 2 || st === -1 || st === 5) {
                  try { player.unMute(); player.setVolume(100); player.playVideo(); } catch {}
                }
              }
              const v = Array.from(document.querySelectorAll('video')).find(x => !x.closest('ytu-epg-row, ytu-guide, .tenx-thumb'));
              if (!v) return false;
              return v.paused;
            })()
            """;

            await SendCdpCommandAsync(ws, id, "Runtime.evaluate", new { expression = expr, returnByValue = true, userGesture = true }, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(200);
            byte[] buf = new byte[8192];
            while (ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(buf, timeoutCts.Token);
                string json = Encoding.UTF8.GetString(buf, 0, res.Count);
                if (json.Contains($"\"id\":{id}", StringComparison.Ordinal))
                {
                    return json.Contains("\"value\":true", StringComparison.Ordinal);
                }
            }
        }
        catch { }
        return false;
    }

    private static async Task SendCdpCommandAsync(ClientWebSocket ws, int id, string method, object parameters, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        string payload = JsonSerializer.Serialize(new
        {
            id,
            method,
            @params = parameters
        });
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> SendCdpCommandAndWaitAsync(ClientWebSocket ws, int id, string method, object parameters, CancellationToken ct)
    {
        await SendCdpCommandAsync(ws, id, method, parameters, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(3000);
        byte[] buf = new byte[8192];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(buf, timeoutCts.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;
                string json = Encoding.UTF8.GetString(buf, 0, res.Count);
                if (json.Contains($"\"id\":{id}", StringComparison.Ordinal))
                    return json;
            }
        }
        catch { }
        return null;
    }

    private (string ExtDir, string ExtensionId, string ExtVersion) EnsureTabCaptureExtension(int tunerId, int wsPort, LoadedProvider provider)
    {
        string baseExtDir = Path.Combine(AppContext.BaseDirectory, "TabCaptureExt");
        Directory.CreateDirectory(baseExtDir);

        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Charon.exe");
        DateTime buildUtc = File.Exists(exePath) ? File.GetLastWriteTimeUtc(exePath) : DateTime.UtcNow;
        string extVersion = $"35.{buildUtc.Month * 100 + buildUtc.Day}.{buildUtc.Hour * 100 + buildUtc.Minute}.{buildUtc.Second}";

        string keyFile = Path.Combine(baseExtDir, "rsa_spki_v20.b64");
        byte[] spkiBytes;
        if (File.Exists(keyFile))
        {
            spkiBytes = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
        }
        else
        {
            using var rsa = RSA.Create(2048);
            spkiBytes = rsa.ExportSubjectPublicKeyInfo();
            File.WriteAllText(keyFile, Convert.ToBase64String(spkiBytes));
        }

        string publicKeyBase64 = Convert.ToBase64String(spkiBytes);
        byte[] hash = SHA256.HashData(spkiBytes);
        var extIdChars = new char[32];
        for (int i = 0; i < 16; i++)
        {
            extIdChars[i * 2] = (char)('a' + (hash[i] >> 4));
            extIdChars[i * 2 + 1] = (char)('a' + (hash[i] & 0x0F));
        }
        string extensionId = new(extIdChars);

        string extDir = Path.Combine(baseExtDir, $"Tuner_{tunerId}");
        Directory.CreateDirectory(extDir);

        string manifestJson = $$"""
        {
          "manifest_version": 3,
          "name": "Charon TabCapture Bridge",
          "version": "{{extVersion}}",
          "key": "{{publicKeyBase64}}",
          "permissions": [
            "tabCapture",
            "offscreen",
            "tabs",
            "scripting"
          ],
          "host_permissions": [
            "<all_urls>"
          ],
          "background": {
            "service_worker": "background.js"
          },
          "content_scripts": [
            {
              "matches": ["<all_urls>"],
              "js": ["main_world.js"],
              "run_at": "document_start",
              "world": "MAIN",
              "all_frames": false
            },
            {
              "matches": ["<all_urls>"],
              "js": ["content.js"],
              "run_at": "document_start",
              "all_frames": false
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(extDir, "manifest.json"), manifestJson);

        var mainWorldBuilder = new StringBuilder();
        mainWorldBuilder.AppendLine("(function(){ try { Object.defineProperty(document, 'hidden', { get: () => false }); Object.defineProperty(document, 'visibilityState', { get: () => 'visible' }); Object.defineProperty(document, 'hasFocus', { value: () => true }); } catch {} })();");
        if (!string.IsNullOrWhiteSpace(provider.StyleCss))
        {
            string escapedCss = JsonEncodedText.Encode(provider.StyleCss).ToString();
            mainWorldBuilder.AppendLine($"(function(){{ const inject = () => {{ if (!document.getElementById('charon-css') && (document.head || document.documentElement)) {{ const s = document.createElement('style'); s.id = 'charon-css'; s.textContent = \"{escapedCss}\"; (document.head || document.documentElement).appendChild(s); }} }}; inject(); document.addEventListener('DOMContentLoaded', inject); }})();");
        }
        if (!string.IsNullOrWhiteSpace(provider.TuneJs))
        {
            mainWorldBuilder.AppendLine(provider.TuneJs);
        }
        File.WriteAllText(Path.Combine(extDir, "main_world.js"), mainWorldBuilder.ToString());

        const string notifyLifecycleJs = """
        (function() {
          if (window.__charonLifecycleNotifier) return;
          window.__charonLifecycleNotifier = true;
          if (location.protocol.startsWith('http')) {
            try { chrome.runtime.sendMessage({ type: 'PAGE_COMMITTED' }); } catch {}
          }
        })();
        """;
        File.WriteAllText(Path.Combine(extDir, "content.js"), notifyLifecycleJs);

        string backgroundJs = $$"""
        const log = (msg) => fetch('http://127.0.0.1:{{wsPort}}/log?msg=' + encodeURIComponent('BG_v{{extVersion}}: ' + msg)).catch(() => {});
        log('Service worker started');

        let streamLocked = false;
        let busyMinting = false;
        let knownTabId = null;
        const swStart = Date.now();

        const offscreenReady = (async () => {
          const existingContexts = await chrome.runtime.getContexts({
            contextTypes: ['OFFSCREEN_DOCUMENT']
          });
          if (existingContexts.length === 0) {
            await chrome.offscreen.createDocument({
              url: 'offscreen.html',
              reasons: ['USER_MEDIA'],
              justification: 'Tab capture streaming to Charon'
            });
          }
        })();

        async function mintAndSendStreamId(tabId) {
          if (streamLocked || busyMinting || !tabId) return;
          busyMinting = true;
          streamLocked = true;

          try {
            await offscreenReady;
            const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tabId });
            log('Got streamId for tab ' + tabId);

            chrome.runtime.sendMessage({
              type: 'START_IMMEDIATE_STREAM',
              streamId: streamId,
              wsPort: {{wsPort}}
            }).catch(() => {});
          } catch (err) {
            streamLocked = false;
          } finally {
            busyMinting = false;
          }
        }

        chrome.runtime.onMessage.addListener((msg, sender) => {
          if (msg.type === 'STREAM_LOCKED_OK') {
            streamLocked = true;
            return;
          }
          if (msg.type === 'PAGE_COMMITTED' && sender && sender.tab && sender.tab.id) {
            knownTabId = sender.tab.id;
            mintAndSendStreamId(knownTabId);
          } else if (msg.type === 'RETRY_CAPTURE') {
            streamLocked = false;
            if (knownTabId) {
              setTimeout(() => mintAndSendStreamId(knownTabId), 250);
            } else {
              setTimeout(pollForCommittedTab, 250);
            }
          } else if (msg.type === 'OFFSCREEN_READY') {
            if (!streamLocked && knownTabId) {
              mintAndSendStreamId(knownTabId);
            }
          }
        });

        function pollForCommittedTab() {
          if (streamLocked || busyMinting) return;
          chrome.tabs.query({}, (tabs) => {
            if (streamLocked || busyMinting) return;
            const elapsed = Date.now() - swStart;
            const t = (tabs || []).find(x =>
              x.id &&
              (!x.url || !x.url.startsWith('chrome-extension://')) &&
              ((x.url && x.url.startsWith('http')) || elapsed >= 350)
            );
            if (t && t.id) {
              knownTabId = t.id;
              mintAndSendStreamId(t.id);
            }
          });
        }

        const timer = setInterval(() => {
          if (streamLocked) {
            clearInterval(timer);
            return;
          }
          if (!knownTabId) {
            pollForCommittedTab();
          }
        }, 50);
        """;
        File.WriteAllText(Path.Combine(extDir, "background.js"), backgroundJs);

        const string offscreenHtml = """
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"><script src="offscreen.js"></script></head>
        <body></body>
        </html>
        """;
        File.WriteAllText(Path.Combine(extDir, "offscreen.html"), offscreenHtml);

        string offscreenJs = $$"""
        let activeRecorder = null;
        let isStarting = false;

        chrome.runtime.onMessage.addListener(async (msg) => {
          if (msg.type !== 'START_IMMEDIATE_STREAM' || activeRecorder || isStarting) return;
          isStarting = true;
          const wsPort = msg.wsPort;
          const log = (m) => fetch('http://127.0.0.1:' + wsPort + '/log?msg=' + encodeURIComponent('OFFSCREEN: ' + m)).catch(() => {});

          try {
            const stream = await navigator.mediaDevices.getUserMedia({
              audio: {
                mandatory: {
                  chromeMediaSource: 'tab',
                  chromeMediaSourceId: msg.streamId
                }
              },
              video: {
                mandatory: {
                  chromeMediaSource: 'tab',
                  chromeMediaSourceId: msg.streamId,
                  minWidth: {{ViewportWidth}},
                  maxWidth: {{ViewportWidth}},
                  minHeight: {{ViewportHeight}},
                  maxHeight: {{ViewportHeight}},
                  maxFrameRate: 60
                }
              }
            });

            chrome.runtime.sendMessage({ type: 'STREAM_LOCKED_OK' }).catch(() => {});

            const videoTrack = stream.getVideoTracks()[0];
            if (videoTrack && 'contentHint' in videoTrack) {
              videoTrack.contentHint = 'motion';
            }

            const ws = new WebSocket('ws://127.0.0.1:' + wsPort + '/');
            ws.binaryType = 'arraybuffer';

            ws.onopen = () => {
              const mimeTypes = [
                'video/webm;codecs=avc1.4D4020,opus',
                'video/webm;codecs=avc1.64002A,opus',
                'video/webm;codecs=h264,opus',
                'video/webm'
              ];
              const mimeType = mimeTypes.find((t) => MediaRecorder.isTypeSupported(t)) || 'video/webm';
              log('Selected MediaRecorder codec profile: ' + mimeType);

              activeRecorder = new MediaRecorder(stream, {
                mimeType: mimeType,
                videoBitsPerSecond: {{VideoBitrate}},
                audioBitsPerSecond: 256000,
                videoKeyFrameIntervalDuration: 2000
              });

              activeRecorder.ondataavailable = (e) => {
                if (e.data && e.data.size > 0 && ws.readyState === WebSocket.OPEN) {
                  ws.send(e.data);
                }
              };

              activeRecorder.start(100);
            };
          } catch (err) {
            activeRecorder = null;
            isStarting = false;
            log('Retrying getUserMedia: ' + (err && err.message ? err.message : String(err)));
            chrome.runtime.sendMessage({ type: 'RETRY_CAPTURE' }).catch(() => {});
          }
        });

        chrome.runtime.sendMessage({ type: 'OFFSCREEN_READY' }).catch(() => {});
        """;
        File.WriteAllText(Path.Combine(extDir, "offscreen.js"), offscreenJs);

        return (extDir, extensionId, extVersion);
    }
            
    private string PrepareTunerProfile(int tunerId, int debugPort)
    {
        string tunerDir = GetTunerProfilePath(tunerId);
        KillChromeProcessesForPortOrProfile(debugPort, tunerDir);
        TryDeleteFile(Path.Combine(tunerDir, "SingletonLock"));

        try
        {
            string swCache = Path.Combine(tunerDir, "Default", "Service Worker");
            if (Directory.Exists(swCache))
                Directory.Delete(swCache, recursive: true);
        }
        catch { }

        if (tunerId == 1)
            return tunerDir;

        SyncAuthFromTuner1(tunerId, forceFullSync: false);
        return tunerDir;
    }

    private void SyncAuthFromTuner1(int targetTunerId, bool forceFullSync)
    {
        if (targetTunerId == 1) return;

        lock (ProfileSyncLock)
        {
            string tuner1Dir = GetTunerProfilePath(1);
            string tuner1Default = Path.Combine(tuner1Dir, "Default");
            if (!Directory.Exists(tuner1Default))
                return;

            string targetDir = GetTunerProfilePath(targetTunerId);
            string targetDefault = Path.Combine(targetDir, "Default");

            string localStateSrc = Path.Combine(tuner1Dir, "Local State");
            string localStateDst = Path.Combine(targetDir, "Local State");
            if (File.Exists(localStateSrc))
            {
                CopyFileUnlocked(localStateSrc, localStateDst);
            }

            string tuner1Cookies = Path.Combine(tuner1Default, "Network", "Cookies");
            string targetCookies = Path.Combine(targetDefault, "Network", "Cookies");

            // Only do a heavy full directory copy if explicitly requested via Sync button or if the target profile doesn't exist yet!
            // Never copy the entire profile directory during a live channel tune when Tuner 1 is running!
            bool needsFullSync = forceFullSync ||
                                 !Directory.Exists(targetDefault) ||
                                 !File.Exists(targetCookies);

            if (needsFullSync)
            {
                CopyDirectorySafe(tuner1Default, targetDefault);
            }
            else
            {
                if (File.Exists(tuner1Cookies))
                {
                    Directory.CreateDirectory(Path.Combine(targetDefault, "Network"));
                    CopyFileUnlocked(tuner1Cookies, targetCookies);
                }
            }
        }
    }

    private static void CopyFileUnlocked(string sourcePath, string destPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);
        }
        catch { }
    }

    private static void KillChromeProcessesForPortOrProfile(int debugPort, string profileDir)
    {
        try
        {
            using var cts = new CancellationTokenSource(200);
            string verJson = Http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/version", cts.Token).GetAwaiter().GetResult();
            using var verDoc = JsonDocument.Parse(verJson);
            if (verDoc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
            {
                string? wsUrl = wsProp.GetString();
                if (!string.IsNullOrWhiteSpace(wsUrl))
                {
                    using var ws = new ClientWebSocket();
                    ws.ConnectAsync(new Uri(wsUrl), cts.Token).GetAwaiter().GetResult();
                    SendCdpCommandAsync(ws, 1, "Browser.close", new { }, cts.Token).GetAwaiter().GetResult();
                    Thread.Sleep(120);
                }
            }
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void CopyDirectorySafe(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith("LOCK", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("Singleton", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            CopyFileUnlocked(file, Path.Combine(targetDir, fileName));
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(subDir);
            if (dirName is "Cache" or "Code Cache" or "GPUCache" or "Service Worker" or "Sessions" or "Session Storage" or "DawnCache" or "GrShaderCache" or "ShaderCache")
                continue;
            CopyDirectorySafe(subDir, Path.Combine(targetDir, dirName));
        }
    }

    private static async Task<IntPtr> WaitForChromeWindowAsync(int processId, CancellationToken ct)
    {
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            IntPtr foundHwnd = IntPtr.Zero;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint winPid);
                if (winPid == (uint)processId)
                {
                    foundHwnd = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (foundHwnd != IntPtr.Zero)
                return foundHwnd;

            await Task.Delay(25, ct);
        }

        return IntPtr.Zero;
    }

    private void ApplyWindowPlacement(IntPtr hwnd, int tunerId = 1)
    {
        ShowWindow(hwnd, SW_RESTORE);

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);

        if (_config.WindowPlacement == WindowPlacementMode.VisibleDebug)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TOOLWINDOW);
            SetWindowPos(hwnd, IntPtr.Zero, 50 + (tunerId * 30), 50 + (tunerId * 30), WindowWidth, WindowHeight, SWP_NOACTIVATE | SWP_NOZORDER);
            return;
        }

        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        int offset = (tunerId - 1) * 24;
        int targetX, targetY;
        if (_config.WindowPlacement == WindowPlacementMode.CustomCoordinates)
        {
            targetX = _config.CustomX + offset;
            targetY = _config.CustomY + offset;
        }
        else
        {
            int virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            targetX = (virtualLeft + virtualWidth) - 16 + offset;
            targetY = (virtualTop + virtualHeight) - 16 + offset;
        }

        SetWindowPos(hwnd, HWND_BOTTOM, targetX, targetY, WindowWidth, WindowHeight, SWP_NOACTIVATE);
    }

    #region Win32 Interop

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int SW_RESTORE = 9;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    #endregion
}