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
