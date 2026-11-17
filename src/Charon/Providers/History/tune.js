(function () {
  try {
    const blockHeavyCodecs = (mimeType) => {
      if (typeof mimeType !== 'string') return false;
      return mimeType.toLowerCase().includes('av01');
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
  } catch {}

  const injectStaticCss = () => {
    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
      const s = document.createElement('style');
      s.id = 'charon-static-css';
      s.textContent = `
        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
        *:has(video) { transform: none !important; contain: none !important; }
        video { position: fixed !important; inset: 0 !important; top: 0 !important; left: 0 !important; width: 100vw !important; height: 100vh !important; margin: 0 !important; max-width: none !important; max-height: none !important; object-fit: fill !important; z-index: 2147483647 !important; background: #000 !important; cursor: none !important; opacity: 1 !important; }
        header, [class*="headerStack" i], [class*="SkipToMainContent" i], #engagement-wrapper, footer, [class*="footer" i], #onetrust-consent-sdk, [class*="controls" i], [class*="overlay" i] {
          opacity: 0 !important; pointer-events: none !important;
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

    // 1. Play FIRST while muted so Chrome autoplay never blocks it
    if (v.paused) {
      const playBtn = document.querySelector(
        'button[aria-label*="Play" i], button[title*="Play" i], [class*="play" i]'
      );
      if (playBtn) playBtn.click();
      v.play().catch(() => {});
    }

    // 2. Unmute once playing
    if (!v.paused && (v.muted || v.volume === 0)) {
      const unmuteBtn = document.querySelector(
        'button[aria-label*="Unmute" i], button[title*="Unmute" i], [class*="unmute" i]'
      );
      if (unmuteBtn) unmuteBtn.click();
      v.muted = false;
      v.volume = 1.0;
    } else if (!v.paused && v.volume < 1.0) {
      v.volume = 1.0;
    }

    if (!v.__charonStyled) {
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
        'z-index: 2147483647 !important'
      ].join('; ');
    }
  };

  checkPlayer();
  if (window.__charonTunerActive) return;
  window.__charonTunerActive = true;
  setInterval(checkPlayer, 300);
})();