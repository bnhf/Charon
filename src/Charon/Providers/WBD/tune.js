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

  const injectStaticCss = () => {
    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
      const s = document.createElement('style');
      s.id = 'charon-static-css';
      s.textContent = `
        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
        #header, #footer, #portal-group,
        [class*="ControlsWrapper"],
        [class*="TopControlsContainerView"],
        [class*="BottomControlsContainerView"] {
          opacity: 0 !important;
        }
      `;
      (document.head || document.documentElement).appendChild(s);
    }
  };

  const findMainVideo = () =>
    Array.from(document.querySelectorAll('video'))
      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

  window.__charonIsPaused = () => {
    const v = findMainVideo();
    if (!v) return false;
    return v.paused;
  };

  const checkPlayer = () => {
    injectStaticCss();

    const v = findMainVideo();
    if (!v) return;

    if (v.muted) v.muted = false;
    if (v.volume < 1.0) v.volume = 1.0;

    if (v.paused) {
      const playBtn = document.querySelector(
        '[class*="playerBackground"] button[aria-label*="Play" i], ' +
        '[class*="ControlsWrapper"] button[aria-label*="Play" i], ' +
        'button[data-testid*="play" i]'
      );
      if (playBtn) playBtn.click();
      v.play().catch(() => {});
      return;
    }

    // Click WBD's top-right expand toggle once (matches IconExpandS path M20 25...)
    if (!window.__charonWbdExpanded) {
      const topBtn = document.querySelector('[class*="TopControlsContainerView"] button');
      const pathD = topBtn?.querySelector('svg path')?.getAttribute('d') || '';
      if (topBtn && pathD.startsWith('M20 25')) {
        window.__charonWbdExpanded = true;
        topBtn.click();
      }
    }

    if (v.readyState >= 2 && v.videoWidth > 0 && !v.__charonStyled) {
      v.__charonStyled = true;
      v.style.cssText = [
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

  checkPlayer();

  if (window.__charonTunerActive) return;
  window.__charonTunerActive = true;

  setInterval(checkPlayer, 300);
})();