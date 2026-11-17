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

  // A channel link (link.fubo.tv/al1?...channel_id=<id>) redirects to www.fubo.tv/al/..., which the web app then
  // routes to the player at /p/watch?channelId=<id>&type=live. A channel the account doesn't carry lands on /p/home.
  // Remember the /al/ link this page was loaded with, so the tuner can retry it once if Fubo leaves the player.
  const watchPathRe = /^\/p\/watch/i;
  try {
    const nav = performance.getEntriesByType('navigation')[0];
    const loaded = nav ? new URL(nav.name) : new URL(location.href);
    if (/^\/al\//i.test(loaded.pathname)) sessionStorage.setItem('__charonFuboTarget', loaded.pathname + loaded.search);
  } catch {}

  // Reloads the channel link once if Fubo leaves the player page, answers "Are you still watching?" prompts
  // (generic selectors; that prompt has not been observed live yet), and presses "Try Again" on Fubo's
  // output-protection error ("Your current video setup is not permitted"), at most every 15 seconds.
  const dismissPrompts = () => {
    try {
      if (watchPathRe.test(location.pathname)) {
        window.__charonFuboOffSince = 0;
      } else {
        const target = sessionStorage.getItem('__charonFuboTarget');
        if (target && !sessionStorage.getItem('__charonFuboReturned')) {
          if (!window.__charonFuboOffSince) window.__charonFuboOffSince = Date.now();
          else if (Date.now() - window.__charonFuboOffSince > 8000) {
            sessionStorage.setItem('__charonFuboReturned', '1');
            location.replace(target);
            return;
          }
        }
      }

      const dialogs = document.querySelectorAll('[role="alertdialog"], [role="dialog"], [data-testid="modal-overlay"]');
      for (const dlg of dialogs) {
        const text = dlg.textContent || '';
        const buttons = Array.from(dlg.querySelectorAll('button, [role="button"]'));
        if (/still (?:watching|there)/i.test(text)) {
          const btn = buttons.find((b) => /continue|keep watching|still (?:here|watching)|^\s*yes\b|resume/i.test(b.textContent || ''));
          if (btn) btn.click();
        } else if (/not permitted/i.test(text) && Date.now() - (window.__charonFuboRetryAt || 0) > 15000) {
          const btn = buttons.find((b) => /try again/i.test(b.textContent || ''));
          if (btn) {
            window.__charonFuboRetryAt = Date.now();
            btn.click();
          }
        }
      }
    } catch {}
  };
  dismissPrompts();

  if (window.__charonTunerActive) return;
  window.__charonTunerActive = true;

  // The player renders into #portal: a fixed layer holding div#video (the stream's <video> plus
  // #bumper-video-element for Fubo's intro bumper) under the controls and metadata overlay. Lift div#video
  // above its siblings and hide the controls, which Fubo marks with data-testid="player-*".
  const injectStaticCss = () => {
    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
      const s = document.createElement('style');
      s.id = 'charon-static-css';
      s.textContent = `
        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
        #portal #video {
          position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important;
          z-index: 2147483000 !important; background: #000 !important;
        }
        [data-testid^="player-"], [data-testid="typeform-embed"] { opacity: 0 !important; pointer-events: none !important; }
      `;
      (document.head || document.documentElement).appendChild(s);
    }
  };
  injectStaticCss();

  const isPlaying = (v) => v && v.readyState >= 2 && v.videoWidth > 0 && !v.paused;

  // Show the bumper only while it plays; otherwise the stream's <video> is the one to use
  const findMainVideo = () => {
    const bumper = document.getElementById('bumper-video-element');
    if (isPlaying(bumper)) return bumper;
    const main = document.querySelector('#video video:not(#bumper-video-element)');
    if (main) return main;
    return Array.from(document.querySelectorAll('video'))
      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];
  };

  // Called by ChromeTuner before it sends a trusted click to unpause the player. Report "paused" only once the
  // video has data: while Fubo is still loading the stream a click can't help.
  window.__charonIsPaused = () => {
    dismissPrompts();
    const v = findMainVideo();
    if (!v || v.readyState < 2) return false;
    return v.paused;
  };

  const fullBleedCss = [
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
    'transform: none !important',
    'z-index: 999999 !important'
  ].join('; ');

  const checkPlayer = () => {
    injectStaticCss();
    dismissPrompts();

    const activeVideo = findMainVideo();
    if (!activeVideo) return;

    if (activeVideo.muted) activeVideo.muted = false;
    if (activeVideo.volume < 1.0) activeVideo.volume = 1.0;
    if (activeVideo.paused && (activeVideo.src || activeVideo.currentSrc || activeVideo.readyState >= 1)) {
      activeVideo.play().catch(() => {});
    }

    if (!isPlaying(activeVideo)) return;

    try { sessionStorage.removeItem('__charonFuboReturned'); } catch {}

    // Restyle whichever <video> is playing, and drop the previous one below it
    if (window.__charonFuboActive !== activeVideo) {
      const prev = window.__charonFuboActive;
      if (prev && prev.__charonStyled) {
        prev.__charonStyled = false;
        prev.style.removeProperty('z-index');
      }
      window.__charonFuboActive = activeVideo;
    }

    if (!activeVideo.__charonStyled) {
      activeVideo.__charonStyled = true;
      activeVideo.style.cssText = fullBleedCss;
    }
  };

  setInterval(checkPlayer, 300);
})();
