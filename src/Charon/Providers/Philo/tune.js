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
