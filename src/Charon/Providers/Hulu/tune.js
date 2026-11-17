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

  // A channel link (/watch/<channel id>) immediately redirects to the current airing (/watch/<airing id>).
  // Remember the URL this page was loaded with, so the tuner can return to the channel if Hulu leaves the player.
  const watchPathRe = /^\/watch\/[0-9a-f-]{36}/i;
  try {
    const nav = performance.getEntriesByType('navigation')[0];
    const loadedPath = nav ? new URL(nav.name).pathname : location.pathname;
    if (watchPathRe.test(loadedPath)) sessionStorage.setItem('__charonHuluTarget', loadedPath);
  } catch {}

  // Picks the first profile on "Who's watching?" (only shown on accounts with several profiles; that picker has not
  // been observed live, so its selectors are generic), reloads the channel link once if Hulu leaves the player page,
  // and answers Hulu's "Are you still watching?" panel.
  const dismissPrompts = () => {
    try {
      const path = location.pathname;
      if (path.startsWith('/profiles')) {
        const candidates = Array.from(document.querySelectorAll(
          '[data-automationid*="profile" i] button, [data-automationid*="profile" i] a, ' +
          '[class*="profile" i] button, [class*="profile" i] a, [class*="Profile" i][role="button"]'))
          .filter((el) => !/add|edit|manage|kids/i.test((el.textContent || '') + ' ' + (el.getAttribute('aria-label') || '')));
        const pick = candidates[0];
        if (pick && Date.now() - (window.__charonHuluProfileClickAt || 0) > 3000) {
          window.__charonHuluProfileClickAt = Date.now();
          pick.click();
        }
        window.__charonHuluOffSince = 0;
        return;
      }

      if (watchPathRe.test(path)) {
        window.__charonHuluOffSince = 0;
      } else {
        const target = sessionStorage.getItem('__charonHuluTarget');
        if (target && !sessionStorage.getItem('__charonHuluReturned')) {
          if (!window.__charonHuluOffSince) window.__charonHuluOffSince = Date.now();
          else if (Date.now() - window.__charonHuluOffSince > 5000) {
            sessionStorage.setItem('__charonHuluReturned', '1');
            location.replace(target);
            return;
          }
        }
      }

      // The panel (.long-time-tips-container) is always in the player's DOM, kept at display: none until Hulu shows it.
      // Click only while it is visible: clicking its YES button at other times stops the live stream from starting.
      // (Don't match it by text through a generic "modal" selector: Hulu puts class "player-modal" on <html>.)
      const tips = document.querySelector('.long-time-tips-container');
      if (tips && getComputedStyle(tips).display !== 'none' && Date.now() - (window.__charonHuluTipsClickAt || 0) > 3000) {
        const yes = tips.querySelector('.long-time-tips__continue-button-container button');
        if (yes) {
          window.__charonHuluTipsClickAt = Date.now();
          yes.click();
        }
      }
    } catch {}
  };
  dismissPrompts();

  if (window.__charonTunerActive) return;
  window.__charonTunerActive = true;

  // Hulu's web player (#web-player-app) stacks ContentPlayer, AdPlayer and IntroPlayer, each with its own <video>,
  // under ControlsContainer (controls, pause ads) and AdUnitView (the "Ad" badge and countdown bar).
  // Overlays are made invisible rather than removed, so their buttons can still be clicked from script.
  const injectStaticCss = () => {
    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
      const s = document.createElement('style');
      s.id = 'charon-static-css';
      s.textContent = `
        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
        #web-player-app .ControlsContainer, #web-player-app .AdUnitView, #web-player-app .PauseAd {
          opacity: 0 !important; pointer-events: none !important;
        }
        #web-player-app .ContentPlayer, #web-player-app .AdPlayer:not(.AdPlayer--hidden) {
          position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important;
          transform: none !important; margin: 0 !important; background: #000 !important;
        }
      `;
      (document.head || document.documentElement).appendChild(s);
    }
  };
  injectStaticCss();

  // The ad player only plays client-side ad breaks; otherwise the content player is the one to show
  const findMainVideo = () => {
    const isLive = (v) => v && v.readyState >= 2 && v.videoWidth > 0 && !v.paused;
    const content = document.getElementById('content-video-player');
    const ad = document.getElementById('ad-video-player');
    if (isLive(ad) && !(ad.closest('.AdPlayer--hidden'))) return ad;
    if (content) return content;
    return Array.from(document.querySelectorAll('video'))
      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];
  };

  // Called by ChromeTuner before it sends a trusted click to unpause the player. Report "paused" only once the video
  // has data: while Hulu is still loading the stream a click can't help, and a click on the player toggles pause.
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
    // ContentPlayer__item--stretched centers the video with translate(-50%, -50%)
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

    if (activeVideo.readyState < 2 || activeVideo.videoWidth === 0 || activeVideo.paused) return;

    try { sessionStorage.removeItem('__charonHuluReturned'); } catch {}

    // Restyle whichever <video> is playing, and drop the previous one below it
    if (window.__charonHuluActive !== activeVideo) {
      const prev = window.__charonHuluActive;
      if (prev && prev.__charonStyled) {
        prev.__charonStyled = false;
        prev.style.removeProperty('z-index');
      }
      window.__charonHuluActive = activeVideo;
    }

    if (!activeVideo.__charonStyled) {
      activeVideo.__charonStyled = true;
      activeVideo.style.cssText = fullBleedCss;
    }
  };

  setInterval(checkPlayer, 300);
})();
