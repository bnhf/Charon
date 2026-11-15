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
                        *:has(video) { transform: none !important; filter: none !important; perspective: none !important; contain: none !important; z-index: 2147483647 !important; background: #000 !important; }
                        video { position: fixed !important; inset: 0 !important; top: 0 !important; left: 0 !important; width: 100vw !important; height: 100vh !important; max-width: none !important; max-height: none !important; margin: 0 !important; object-fit: fill !important; z-index: 2147483647 !important; background: #000 !important; cursor: none !important; }
                        header, footer, nav,
                        .header, .footer,
                        [class*="control-bar" i],
                        [class*="controls" i],
                        [data-testid*="controls" i],
                        #onetrust-consent-sdk {
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

                    const allVideos = Array.from(document.querySelectorAll('video'));
                    if (allVideos.length === 0) return;

                    for (const v of allVideos) {
                      if (v.muted || v.volume === 0) {
                        const unmuteBtn = document.querySelector(
                          'button[aria-label*="Unmute" i], ' +
                          'button[title*="Unmute" i], ' +
                          '[class*="unmute" i], ' +
                          '[data-testid*="unmute" i]'
                        );
                        if (unmuteBtn) unmuteBtn.click();
                        v.muted = false;
                        v.volume = 1.0;
                      } else if (v.volume < 1.0) {
                        v.volume = 1.0;
                      }

                      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
                        const playBtn = document.querySelector(
                          'button[aria-label*="Play" i], ' +
                          'button[title*="Play" i], ' +
                          '[data-testid*="play" i]'
                        );
                        if (playBtn) playBtn.click();
                        v.play().catch(() => {});
                      }
                    }

                    const activeVideo = findMainVideo();
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
                        'z-index: 2147483647 !important'
                      ].join('; ');
                    }
                  };

                  checkPlayer();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  setInterval(checkPlayer, 300);
                })();