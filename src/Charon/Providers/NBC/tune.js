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
                        section.links-selectable-group, .live-page__links-selectable-group, .livePage__filter,
                        .live-guide__wrapper, .live-guide__nav, .live-guide__schedules, .live-guide__streams, .live-guide__inner,
                        footer.footer, .footer, #onetrust-consent-sdk, .video-overlay, .brand-header, .video-player__controls,
                        [class*="header" i], [class*="controls" i] {
                          opacity: 0 !important; pointer-events: none !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };

                  const checkPlayer = () => {
                    injectStaticCss();

                    const v = document.querySelector('video');
                    if (!v) return;

                    if (v.muted || v.volume === 0) {
                      const unmute = document.querySelector('button[aria-label*="Unmute" i], [class*="unmute" i]');
                      if (unmute) unmute.click();
                      v.muted = false;
                      v.volume = 1.0;
                    } else if (v.volume < 1.0) {
                      v.volume = 1.0;
                    }

                    if (v.paused && v.readyState >= 1) {
                      const play = document.querySelector('button[aria-label*="Play" i]');
                      if (play) play.click();
                      v.play().catch(() => {});
                    }
                  };

                  checkPlayer();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  setInterval(checkPlayer, 300);
                })();