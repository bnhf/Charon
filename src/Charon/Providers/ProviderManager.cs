using System.IO;
using System.Text;
using System.Text.Json;
using Charon.Config;

namespace Charon.Providers;

public class ProviderManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string GroupTitle { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string LoginUrl { get; set; } = string.Empty;
    public int TuneScriptDelayMs { get; set; } = 1500;
}

public class ProviderChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ChannelNumber { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
	public bool IsHidden { get; set; } = false;
}

public class LoadedProvider
{
    public required ProviderManifest Manifest { get; init; }
    public required List<ProviderChannel> Channels { get; init; }
    public required string FolderPath { get; init; }
    public string? TuneJs { get; init; }
    public string? StyleCss { get; init; }
}

public class ProviderManager
{
    private readonly string _providersRoot;
    private readonly List<LoadedProvider> _providers = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public IReadOnlyList<LoadedProvider> Providers => _providers;
    public string ProvidersRoot => _providersRoot;

    public ProviderManager()
    {
        _providersRoot = Path.Combine(AppContext.BaseDirectory, "Providers");
        ReloadProviders();
    }

    public void ReloadProviders()
    {
        _providers.Clear();
        Directory.CreateDirectory(_providersRoot);

        // Ensure both default provider directories exist out of the box
        string defaultYttvDir = Path.Combine(_providersRoot, "YouTubeTV");
        string defaultSpectrumDir = Path.Combine(_providersRoot, "Spectrum");
		string defaultXfinityDir = Path.Combine(_providersRoot, "Xfinity");
        string defaultSlingDir = Path.Combine(_providersRoot, "Sling");
        string defaultPhiloDir = Path.Combine(_providersRoot, "Philo");
        Directory.CreateDirectory(defaultYttvDir);
        Directory.CreateDirectory(defaultSpectrumDir);
		Directory.CreateDirectory(defaultXfinityDir);
        Directory.CreateDirectory(defaultSlingDir);
        Directory.CreateDirectory(defaultPhiloDir);

        // If someone placed channels.json directly in Providers\ root instead of Providers\YouTubeTV\, move/copy it into Providers\YouTubeTV\
        string rootChannelsFile = Path.Combine(_providersRoot, "channels.json");
        if (File.Exists(rootChannelsFile))
        {
            string targetYttvChannels = Path.Combine(defaultYttvDir, "channels.json");
            if (!File.Exists(targetYttvChannels))
                File.Copy(rootChannelsFile, targetYttvChannels);
        }

        // Scan all subdirectories in Providers\
        foreach (var dir in Directory.GetDirectories(_providersRoot))
        {
            LoadOrRepairProviderDirectory(dir);
        }
    }

    private void LoadOrRepairProviderDirectory(string dir)
    {
        string folderName = Path.GetFileName(dir);
        bool isYttv = folderName.Equals("YouTubeTV", StringComparison.OrdinalIgnoreCase);
        bool isSpectrum = folderName.Equals("Spectrum", StringComparison.OrdinalIgnoreCase);
		bool isXfinity = folderName.Equals("Xfinity", StringComparison.OrdinalIgnoreCase);
        bool isSling = folderName.Equals("Sling", StringComparison.OrdinalIgnoreCase);
        bool isPhilo = folderName.Equals("Philo", StringComparison.OrdinalIgnoreCase);

        string manifestPath = Path.Combine(dir, "provider.json");
        string altManifestPath = Path.Combine(dir, "manifest.json");
        string channelsPath = Path.Combine(dir, "channels.json");
        string tuneJsPath = Path.Combine(dir, "tune.js");
        string styleCssPath = Path.Combine(dir, "style.css");

        string defaultName = isYttv ? "YouTube TV" : isSpectrum ? "Spectrum TV" : isXfinity ? "Xfinity Stream" : isSling ? "Sling TV" : isPhilo ? "Philo" : folderName;
        string defaultLoginUrl = isSpectrum
            ? "https://watch.spectrum.net/guide"
            : isXfinity
            ? "https://www.xfinity.com/stream"
            : isSling
            ? "https://watch.sling.com"
            : isPhilo
            ? "https://www.philo.com/player"
            : "https://tv.youtube.com";
        int defaultDelayMs = isSpectrum || isSling || isPhilo ? 2000 : 1500;

        // Support manifest.json if present, or auto-create provider.json if missing!
        if (!File.Exists(manifestPath) && File.Exists(altManifestPath))
        {
            File.Copy(altManifestPath, manifestPath);
        }
        else if (!File.Exists(manifestPath))
        {
            var autoManifest = new ProviderManifest
            {
                Id = folderName.ToLowerInvariant(),
                Name = defaultName,
                GroupTitle = defaultName,
                Enabled = true,
                LoginUrl = defaultLoginUrl,
                TuneScriptDelayMs = defaultDelayMs
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(autoManifest, JsonOptions), new UTF8Encoding(false));
        }

        if (!File.Exists(channelsPath))
        {
            File.WriteAllText(channelsPath, "[]", new UTF8Encoding(false));
        }
		
		// Auto-create/update YouTube TV's tune.js and style.css
        if (isYttv)
        {
            const string yttvTuneJs = """
                (function() {
                  if (location.pathname.startsWith('/live')) return;

                  const queryAllDeep = (selector, root = document) => {
                    const results = Array.from(root.querySelectorAll(selector));
                    const all = root.querySelectorAll('*');
                    for (const el of all) {
                      if (el.shadowRoot) {
                        results.push(...queryAllDeep(selector, el.shadowRoot));
                      }
                    }
                    return results;
                  };

                 // 1. Control the YouTube HTML5 player API
                  const players = queryAllDeep('#movie_player, .html5-video-player');
                  for (const player of players) {
                    try {
                      if (typeof player.unMute === 'function') player.unMute();
                      if (typeof player.setVolume === 'function') player.setVolume(100);
                      if (typeof player.playVideo === 'function' && typeof player.getPlayerState === 'function' && player.getPlayerState() !== 1) {
                        player.playVideo();
                      }
                      if (typeof player.hideControls === 'function') player.hideControls();
                      player.classList.add('ytp-autohide');
                      if (typeof player.setSize === 'function') {
                        player.setSize(window.innerWidth, window.innerHeight);
                      }
                      if (typeof player.setPlaybackQualityRange === 'function') {
                        player.setPlaybackQualityRange('hd720', 'hd1080');
                      }
                    } catch (e) {}
                  }

                  // 2. Find the active live video element (piercing Shadow DOMs)
                  const videos = queryAllDeep('video');
                  const v = videos.find(x => x.videoWidth > 0 || !x.closest('ytu-epg-row, ytu-guide, .tenx-thumb')) || videos[0];
                  if (!v) return;

                  v.muted = false;
                  v.volume = 1.0;
                  if (v.paused) v.play().catch(() => {});

                  // 3. Force the video element itself to fill 1280x720 at (0,0)
                  const fullBoxCss = [
                    ['position', 'fixed'],
                    ['inset', '0px'],
                    ['top', '0px'],
                    ['left', '0px'],
                    ['right', '0px'],
                    ['bottom', '0px'],
                    ['margin', '0px'],
                    ['margin-left', '0px'],
                    ['padding', '0px'],
                    ['padding-left', '0px'],
                    ['width', '100vw'],
                    ['height', '100vh'],
                    ['min-width', '100vw'],
                    ['min-height', '100vh'],
                    ['max-width', '100vw'],
                    ['max-height', '100vh'],
                    ['transform', 'none'],
                    ['translate', 'none'],
                    ['contain', 'none'],
                    ['will-change', 'auto']
                  ];

                  for (const [prop, val] of fullBoxCss) {
                    v.style.setProperty(prop, val, 'important');
                  }
                  v.style.setProperty('object-fit', 'fill', 'important');
                  v.style.setProperty('z-index', '2147483647', 'important');
                  v.style.setProperty('background', '#000', 'important');

                  // 4. Walk ALL ancestors (including across ShadowRoot boundaries!) to strip any left offset or containing block trap
                  let curr = v.parentElement || (v.getRootNode && v.getRootNode().host);
                  while (curr && curr !== document && curr !== document.documentElement) {
                    if (curr.style) {
                      for (const [prop, val] of fullBoxCss) {
                        curr.style.setProperty(prop, val, 'important');
                      }
                      // Also zero out any CSS custom variables YouTube TV uses for the left guide drawer width!
                      curr.style.setProperty('--ytu-guide-width', '0px', 'important');
                      curr.style.setProperty('--app-drawer-width', '0px', 'important');
                      curr.style.setProperty('--ytu-watch-sidebar-width', '0px', 'important');
                    }
                    if (curr.parentElement) {
                      curr = curr.parentElement;
                    } else {
                      const root = curr.getRootNode && curr.getRootNode();
                      curr = root && root.host ? root.host : null;
                    }
                  }

                  // 5. Hide any sibling sidebars/drawers inside all Shadow Roots
                  const sidebars = queryAllDeep('tp-yt-app-drawer, ytu-guide, ytu-mini-guide, ytu-nav-bar, ytu-header-bar, ytu-player-bar, ytu-player-controls, .ytp-chrome-bottom, .ytp-chrome-top');
                  for (const sb of sidebars) {
                    if (sb.style) {
                      sb.style.setProperty('display', 'none', 'important');
                      sb.style.setProperty('width', '0px', 'important');
                    }
                  }
                })();
                """;
            File.WriteAllText(tuneJsPath, yttvTuneJs, new UTF8Encoding(false));

            const string yttvStyleCss = """
                :root, html, body, ytu-app {
                  --ytu-guide-width: 0px !important;
                  --app-drawer-width: 0px !important;
                  --ytu-watch-sidebar-width: 0px !important;
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, yttvStyleCss, new UTF8Encoding(false));
        }

        // Auto-create Spectrum's tune.js and style.css if missing
        if (isSpectrum)
        {
            const string spectrumTuneJs = """
                (function() {
                  const queryAllDeep = (selector, root = document) => {
                    const results = Array.from(root.querySelectorAll(selector));
                    const all = root.querySelectorAll('*');
                    for (const el of all) {
                      if (el.shadowRoot) {
                        results.push(...queryAllDeep(selector, el.shadowRoot));
                      }
                    }
                    return results;
                  };

                  const videos = queryAllDeep('video');
                  const v = videos.find(x => x.readyState > 0 || x.src || x.srcObject) || videos[0];
                  if (!v) return;

                  v.muted = false;
                  v.volume = 1.0;
                  if (v.paused) {
                    v.play().catch(() => {});
                  }

                  v.style.setProperty('position', 'fixed', 'important');
                  v.style.setProperty('inset', '0px', 'important');
                  v.style.setProperty('top', '0px', 'important');
                  v.style.setProperty('left', '0px', 'important');
                  v.style.setProperty('width', '100vw', 'important');
                  v.style.setProperty('height', '100vh', 'important');
                  v.style.setProperty('object-fit', 'fill', 'important');
                  v.style.setProperty('z-index', '2147483647', 'important');
                  v.style.setProperty('opacity', '1', 'important');
                  v.style.setProperty('visibility', 'visible', 'important');

                  // Walk up the ancestor chain from <video> to <body>:
                  // Keep every direct ancestor visible, and hide EVERY sibling of those ancestors (all overlays, controls, headers, spinners)!
                  const ancestors = new Set();
                  let curr = v;
                  while (curr) {
                    ancestors.add(curr);
                    curr = curr.parentElement || (curr.getRootNode && curr.getRootNode().host);
                  }

                  for (const anc of ancestors) {
                    if (anc !== v && anc.style) {
                      anc.style.setProperty('transform', 'none', 'important');
                      anc.style.setProperty('contain', 'none', 'important');
                      anc.style.setProperty('z-index', '2147483647', 'important');
                    }
                    const parent = anc.parentElement || (anc.getRootNode && anc.getRootNode());
                    if (parent && parent.children) {
                      for (const sibling of parent.children) {
                        if (!ancestors.has(sibling) && sibling.tagName !== 'STYLE' && sibling.tagName !== 'SCRIPT' && sibling.style) {
                          sibling.style.setProperty('opacity', '0', 'important');
                          sibling.style.setProperty('visibility', 'hidden', 'important');
                          sibling.style.setProperty('pointer-events', 'none', 'important');
                        }
                      }
                    }
                  }
                })();
                """;
            File.WriteAllText(tuneJsPath, spectrumTuneJs, new UTF8Encoding(false));
        
		const string spectrumStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                *:has(video) {
                  transform: none !important;
                  filter: none !important;
                  perspective: none !important;
                  contain: none !important;
                  isolation: auto !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                }
                video {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  object-fit: fill !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                  pointer-events: none !important;
                }
                body *:not(:has(video)):not(video) {
                  opacity: 0 !important;
                  visibility: hidden !important;
                  pointer-events: none !important;
                }
                header, nav, footer, aside, button, svg,
                .site-header, .player-controls, .control-bar, .mini-guide,
                .vjs-control-bar, .vjs-big-play-button, .vjs-loading-spinner, .vjs-text-track-display,
                [class*="control" i], [class*="overlay" i], [class*="header" i], [class*="guide" i],
                [class*="navbar" i], [class*="toolbar" i], [class*="scrim" i], [class*="gradient" i],
                [class*="banner" i], [class*="modal" i], [class*="tooltip" i], [role="toolbar"], [role="navigation"] {
                  display: none !important;
                  opacity: 0 !important;
                  visibility: hidden !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, spectrumStyleCss, new UTF8Encoding(false));
		}
		
		if (isXfinity)
        {
            const string xfinityStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                tv-player-nav, tv-player-rating, tv-player-mini-controls, tv-player-buffer-indicator,
                tv-player-notification, tv-player-network-alert, tv-player-debug-panel, tv-player-panel-loading,
                tv-player-panel-next-episode, tv-player-panel-caster, tv-overlay-player-info, tv-overlay-player-ooh-info,
                #playerControlPanel, #nextEpisodeControls, #playerLastWatchedPanel, #inner-cc-container {
                  display: none !important;
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                /* Kept in the DOM so tune.js can click Continue */
                tv-player-still-watching {
                  opacity: 0 !important;
                }
                """;
            File.WriteAllText(styleCssPath, xfinityStyleCss, new UTF8Encoding(false));

            const string xfinityTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      const lower = mimeType.toLowerCase();
                      return lower.includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}

                  const dismissPlayerDialogs = () => {
                    try {
                      const errorPanel = document.querySelector('tv-player-panel-error#errorPanel');
                      if (errorPanel && !errorPanel.hidden) {
                        const page = errorPanel.querySelector('tv-page-error-content.iron-selected');
                        const title = page && page.querySelector('#title');
                        if (title && /paused playback/i.test(title.textContent)) {
                          const resume = page.querySelector('tv-button button');
                          if (resume) resume.click();
                        }
                      }
                      const stillWatching = document.getElementById('playerStillWatching');
                      if (stillWatching && getComputedStyle(stillWatching).display !== 'none') {
                        const cont = stillWatching.querySelector('#continueButton button');
                        if (cont) cont.click();
                        else if (typeof stillWatching._onContinue === 'function') stillWatching._onContinue();
                      }
                    } catch {}
                  };
                  dismissPlayerDialogs();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        tv-player-nav, tv-player-rating, tv-player-mini-controls, tv-player-buffer-indicator,
                        tv-player-notification, tv-player-network-alert, tv-player-debug-panel, tv-player-panel-loading,
                        tv-player-panel-next-episode, tv-player-panel-caster, tv-overlay-player-info, tv-overlay-player-ooh-info,
                        #playerControlPanel, #nextEpisodeControls, #playerLastWatchedPanel, #inner-cc-container {
                          display: none !important;
                        }
                        tv-player-still-watching { opacity: 0 !important; }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };
                  injectStaticCss();

                  const findMainVideo = () => document.querySelector('#viper-player-container video') ||
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  window.__charonIsPaused = () => {
                    dismissPlayerDialogs();
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();
                    dismissPlayerDialogs();
                    const allVideos = Array.from(document.querySelectorAll('video'));
                    if (allVideos.length === 0) return;
                    for (const v of allVideos) {
                      if (v.muted) v.muted = false;
                      if (v.volume < 1.0) v.volume = 1.0;
                      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
                        v.play().catch(() => {});
                      }
                    }
                    const readyVideos = allVideos
                      .filter((v) => v.readyState >= 2 && v.videoWidth > 0 && !v.paused)
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight));
                    const activeVideo = readyVideos[0];
                    if (!activeVideo) return;
                    for (const v of allVideos) {
                      if (v !== activeVideo && !v.paused) {
                        v.muted = true;
                        try { v.pause(); } catch {}
                      }
                    }
                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 999999 !important'
                      ].join('; ');
                    }
                  };
                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, xfinityTuneJs, new UTF8Encoding(false));
        }

        if (isSling)
        {
            const string slingStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Sling's metadata/controls overlay, the Bitmovin UI layer, and toast notifications */
                [data-testid="player-controls-container"],
                .player-controls,
                .bmpui-ui-uicontainer,
                .bitmovinplayer-poster,
                .Toastify {
                  display: none !important;
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, slingStyleCss, new UTF8Encoding(false));

            const string slingTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      const lower = mimeType.toLowerCase();
                      return lower.includes('av01');
                    };

                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }

                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }

                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }

                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}
                  
                  // The watch page embeds a hidden same-origin iframe of itself with a second Bitmovin player a few seconds
                  // off from the visible one. Tab capture records every frame's audio, so that player would be heard as a
                  // repeat of each moment (and its extra 1080p decode stalls the main player). Keep it muted and paused, and
                  // run everything below only in the top frame.
                  if (window.top !== window) {
                    if (!window.__charonSubframeSilenced) {
                      window.__charonSubframeSilenced = true;
                      setInterval(() => {
                        for (const v of document.querySelectorAll('video, audio')) {
                          if (!v.muted) v.muted = true;
                          if (!v.paused) { try { v.pause(); } catch {} }
                        }
                      }, 300);
                    }
                    return;
                  }

                  // Sling redirects every fresh page load to "Who's Watching?" (/switch_user_profile) unless
                  // "Don't Ask Again" was chosen. Remember the channel deep link before that redirect replaces it.
                  const watchPathRe = /^\/\d+\/channel\/[0-9a-f]{32}\/watch/i;
                  try {
                    if (watchPathRe.test(location.pathname)) sessionStorage.setItem('__charonSlingTarget', location.pathname);
                  } catch {}

                  // Picks the first profile on "Who's Watching?", which returns Sling to the deep link. If Sling lands
                  // somewhere else instead, reload the deep link once. Also answers "Are you still watching?" prompts
                  // (those selectors are generic; the prompt has not been observed live yet).
                  const dismissPrompts = () => {
                    try {
                      const path = location.pathname;
                      if (path.startsWith('/switch_user_profile')) {
                        const avatar = document.querySelector('[data-testid^="SwitchUserProfilesIcons-0"] .Card-Avatar-Interaction-Container');
                        if (avatar && Date.now() - (window.__charonSlingProfileClickAt || 0) > 3000) {
                          window.__charonSlingProfileClickAt = Date.now();
                          avatar.click();
                        }
                        window.__charonSlingOffSince = 0;
                        return;
                      }

                      if (watchPathRe.test(path)) {
                        window.__charonSlingOffSince = 0;
                      } else {
                        const target = sessionStorage.getItem('__charonSlingTarget');
                        if (target && !sessionStorage.getItem('__charonSlingReturned')) {
                          if (!window.__charonSlingOffSince) window.__charonSlingOffSince = Date.now();
                          else if (Date.now() - window.__charonSlingOffSince > 5000) {
                            sessionStorage.setItem('__charonSlingReturned', '1');
                            location.replace(target);
                            return;
                          }
                        }
                      }

                      const dialogs = document.querySelectorAll('[role="dialog"], [class*="modal" i], [class*="dialog" i], [class*="popup" i]');
                      for (const dlg of dialogs) {
                        if (!/still (?:watching|there)/i.test(dlg.textContent || '')) continue;
                        const btn = Array.from(dlg.querySelectorAll('button, [role="button"], [class*="button" i]'))
                          .find((b) => /continue|keep watching|still (?:here|watching)|^\s*yes\b|resume/i.test(b.textContent || ''));
                        if (btn) btn.click();
                      }
                    } catch {}
                  };
                  dismissPrompts();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        [data-testid="player-controls-container"], .player-controls, .bmpui-ui-uicontainer,
                        .bitmovinplayer-poster, .Toastify {
                          display: none !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };
                  injectStaticCss();

                  // Sling uses the Bitmovin player; its <video> id starts with "bitmovinplayer-video"
                  const findMainVideo = () => document.querySelector('video[id^="bitmovinplayer-video"]') ||
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  // Called by ChromeTuner before it sends a trusted click to unpause the player.
                  window.__charonIsPaused = () => {
                    dismissPrompts();
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();
                    dismissPrompts();

                    const allVideos = Array.from(document.querySelectorAll('video'));
                    if (allVideos.length === 0) return;

                    for (const v of allVideos) {
                      if (v.muted) v.muted = false;
                      if (v.volume < 1.0) v.volume = 1.0;
                      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
                        v.play().catch(() => {});
                      }
                    }

                    const readyVideos = allVideos
                      .filter((v) => v.readyState >= 2 && v.videoWidth > 0 && !v.paused)
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight));

                    const activeVideo = readyVideos[0];
                    if (!activeVideo) return;

                    try { sessionStorage.removeItem('__charonSlingReturned'); } catch {}

                    for (const v of allVideos) {
                      if (v !== activeVideo && !v.paused) {
                        v.muted = true;
                        try { v.pause(); } catch {}
                      }
                    }

                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 999999 !important'
                      ].join('; ');
                    }
                  };

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, slingTuneJs, new UTF8Encoding(false));
        }

        if (isPhilo)
        {
            const string philoStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Philo's player overlays: title/metadata, playback controls, and the ads overlay.
                   Hidden with CSS only, so tune.js can still reach the JUMP TO LIVE button. */
                [class*="overlaysContainer"] {
                  display: none !important;
                  opacity: 0 !important;
                  pointer-events: none !important;
                }

                /* The video's container is centered with a translate(-50%, -50%) transform, which would make the
                   video's position: fixed relative to it; pin the container to the viewport instead. */
                [class*="videoElementContainer"] {
                  position: fixed !important;
                  transform: none !important;
                  left: 0 !important;
                  top: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  margin: 0 !important;
                }
                """;
            File.WriteAllText(styleCssPath, philoStyleCss, new UTF8Encoding(false));

            const string philoTuneJs = """
                (function () {
                  try {
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }

                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}

                  if (window.top !== window) return;

                  // Channel deep links (/player/player/channel/<id>) redirect to the airing's /player/player/broadcast/<id> page.
                  // Remember the channel link so the tuner can return to it if Philo navigates away from the player.
                  const channelPathRe = /^\/player\/player\/channel\//;
                  const playerPathRe = /^\/player\/player\//;
                  try {
                    if (channelPathRe.test(location.pathname)) sessionStorage.setItem('__charonPhiloTarget', location.pathname);
                  } catch {}

                  // Answers "Are you still watching?" style prompts (generic selectors; the prompt has not been observed live yet)
                  // and reloads the channel link once if Philo leaves the player page.
                  const dismissPrompts = () => {
                    try {
                      if (playerPathRe.test(location.pathname)) {
                        window.__charonPhiloOffSince = 0;
                      } else {
                        const target = sessionStorage.getItem('__charonPhiloTarget');
                        if (target && !sessionStorage.getItem('__charonPhiloReturned')) {
                          if (!window.__charonPhiloOffSince) window.__charonPhiloOffSince = Date.now();
                          else if (Date.now() - window.__charonPhiloOffSince > 5000) {
                            sessionStorage.setItem('__charonPhiloReturned', '1');
                            location.replace(target);
                            return;
                          }
                        }
                      }

                      const dialogs = document.querySelectorAll('[role="dialog"], [role="alertdialog"], [class*="modal" i], [class*="dialog" i], [class*="popup" i]');
                      for (const dlg of dialogs) {
                        if (!/still (?:watching|there)/i.test(dlg.textContent || '')) continue;
                        const btn = Array.from(dlg.querySelectorAll('button, [role="button"]'))
                          .find((b) => /continue|keep watching|still (?:here|watching)|^\s*yes\b|resume/i.test(b.textContent || ''));
                        if (btn) btn.click();
                      }
                    } catch {}
                  };
                  dismissPrompts();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        [class*="overlaysContainer"] { display: none !important; }
                        [class*="videoElementContainer"] {
                          position: fixed !important; transform: none !important; left: 0 !important; top: 0 !important;
                          width: 100vw !important; height: 100vh !important; margin: 0 !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };
                  injectStaticCss();

                  // Philo's web player has a single <video id="video">
                  const findMainVideo = () => document.getElementById('video') ||
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  // Called by ChromeTuner before it sends a trusted click to unpause the player.
                  window.__charonIsPaused = () => {
                    dismissPrompts();
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  // Philo starts the current airing from its beginning. The player's controls (and its JUMP TO LIVE button) only
                  // mount while the mouse is moving, so wake them with a synthetic mousemove, then jump once per airing. The
                  // remaining-time label reads "LIVE" when the player is at the live edge.
                  const jumpToLive = () => {
                    const key = location.pathname;
                    if (window.__charonPhiloLiveFor === key) return;

                    const overlay = document.querySelector('[class*="playerOverlayContainer"]');
                    if (!overlay) return;
                    overlay.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 640, clientY: 360, view: window }));

                    const remaining = document.querySelector('[class*="remainingTimeContainer"]');
                    if (remaining && /^\s*live\s*$/i.test(remaining.textContent || '')) {
                      window.__charonPhiloLiveFor = key;
                      return;
                    }

                    const btn = document.querySelector('[class*="jtlButton"]') ||
                      Array.from(document.querySelectorAll('button')).find((b) => /jump to live/i.test(b.textContent || ''));
                    if (btn && Date.now() - (window.__charonPhiloJumpAt || 0) > 2000) {
                      window.__charonPhiloJumpAt = Date.now();
                      btn.click();
                    }
                  };

                  const checkPlayer = () => {
                    injectStaticCss();
                    dismissPrompts();

                    const allVideos = Array.from(document.querySelectorAll('video'));
                    if (allVideos.length === 0) return;

                    for (const v of allVideos) {
                      if (v.muted) v.muted = false;
                      if (v.volume < 1.0) v.volume = 1.0;
                      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
                        v.play().catch(() => {});
                      }
                    }

                    const activeVideo = findMainVideo();
                    if (!activeVideo || activeVideo.readyState < 2 || activeVideo.videoWidth === 0 || activeVideo.paused) return;

                    try { sessionStorage.removeItem('__charonPhiloReturned'); } catch {}
                    jumpToLive();

                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 999999 !important'
                      ].join('; ');
                    }
                  };

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, philoTuneJs, new UTF8Encoding(false));
        }

        ProviderManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<ProviderManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch { }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
        {
            manifest = new ProviderManifest
            {
                Id = folderName.ToLowerInvariant(),
                Name = defaultName,
                GroupTitle = defaultName,
                Enabled = true,
                LoginUrl = defaultLoginUrl,
                TuneScriptDelayMs = defaultDelayMs
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        }

        List<ProviderChannel> channels = [];
        try
        {
            channels = JsonSerializer.Deserialize<List<ProviderChannel>>(File.ReadAllText(channelsPath), JsonOptions) ?? [];
        }
        catch { }

        _providers.Add(new LoadedProvider
        {
            Manifest = manifest,
            Channels = channels,
            FolderPath = dir,
            TuneJs = File.Exists(tuneJsPath) ? File.ReadAllText(tuneJsPath) : null,
            StyleCss = File.Exists(styleCssPath) ? File.ReadAllText(styleCssPath) : null
        });
    }

    public static List<ProviderChannel> ParseChannelsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var channels = JsonSerializer.Deserialize<List<ProviderChannel>>(json, JsonOptions);
        return channels ?? throw new InvalidOperationException("JSON did not contain a valid channel array.");
    }

    public static string FormatChannelsJson(IEnumerable<ProviderChannel> channels)
    {
        return JsonSerializer.Serialize(channels, JsonOptions);
    }

    public string SaveProviderChannels(LoadedProvider provider, List<ProviderChannel> updatedChannels)
    {
        Directory.CreateDirectory(provider.FolderPath);
        string channelsPath = Path.Combine(provider.FolderPath, "channels.json");
        string json = FormatChannelsJson(updatedChannels);

        // 1. Save to the active runtime provider folder (e.g., C:\Charon\Providers\YouTubeTV\channels.json)
        File.WriteAllText(channelsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 2. Also mirror to the source repo folder if present on this machine
        try
        {
            string folderName = Path.GetFileName(provider.FolderPath);
            string devRepoChannelsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Charon", "Providers", folderName, "channels.json");

            string? devDir = Path.GetDirectoryName(devRepoChannelsPath);
            if (devDir is not null && Directory.Exists(devDir) &&
                !string.Equals(Path.GetFullPath(channelsPath), Path.GetFullPath(devRepoChannelsPath), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(devRepoChannelsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch { }

        provider.Channels.Clear();
        provider.Channels.AddRange(updatedChannels);

        return channelsPath;
    }

    public (LoadedProvider? Provider, ProviderChannel? Channel) FindChannel(string providerId, string channelId)
    {
        var provider = _providers.FirstOrDefault(p =>
            p.Manifest.Enabled && p.Manifest.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));

        var channel = provider?.Channels.FirstOrDefault(c =>
            c.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase));

        return (provider, channel);
    }

    /// <param name="providerFilter">
    /// Optional set of provider IDs to include (case-insensitive). Null or empty includes all enabled providers.
    /// </param>
    public string GenerateM3uPlaylist(CharonConfig config, IReadOnlySet<string>? providerFilter = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        string baseUrl = $"http://{config.ResolvedLanIp}:{config.ResolvedPort}";

        bool filterActive = providerFilter is { Count: > 0 };

        foreach (var provider in _providers.Where(p => p.Manifest.Enabled))
        {
            if (filterActive && !providerFilter!.Contains(provider.Manifest.Id))
                continue;

            string group = string.IsNullOrWhiteSpace(provider.Manifest.GroupTitle)
                ? provider.Manifest.Name
                : provider.Manifest.GroupTitle;

            foreach (var ch in provider.Channels)
            {
                if (ch.IsHidden)
                    continue;

                string uniqueChannelId = $"{provider.Manifest.Id}-{ch.Id}";
                var extInf = new StringBuilder($"#EXTINF:-1 channel-id=\"{uniqueChannelId}\"");

                if (!string.IsNullOrWhiteSpace(ch.ChannelNumber))
                    extInf.Append($" channel-number=\"{ch.ChannelNumber}\"");

                if (!string.IsNullOrWhiteSpace(ch.StationId))
                {
                    extInf.Append($" tvg-id=\"{ch.StationId}\"");
                    extInf.Append($" tvc-guide-stationid=\"{ch.StationId}\"");
                }

                extInf.Append($" tvg-name=\"{ch.Name}\"");
                extInf.Append($" group-title=\"{group}\",{ch.Name}");

                sb.AppendLine(extInf.ToString());
                sb.AppendLine($"{baseUrl}/stream/{provider.Manifest.Id}/{ch.Id}");
            }
        }

        return sb.ToString();
    }
}