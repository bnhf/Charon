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
                        video, video#epg-player_html5_api, .vjs-tech { position: fixed !important; inset: 0 !important; top: 0 !important; left: 0 !important; width: 100vw !important; height: 100vh !important; margin: 0 !important; max-width: none !important; max-height: none !important; object-fit: fill !important; z-index: 2147483647 !important; background: #000 !important; cursor: none !important; opacity: 1 !important; }
                        .module-loader-wrapper:not(:has(video)), header, footer, nav, .vjs-control-bar, .vjs-big-play-button, .vjs-title-bar, .vjs-poster, .vjs-loading-spinner, .vjs-text-track-display, .vjs-text-track-display-ttml, .vjs-modal-dialog, .tvProviderImage, #onetrust-consent-sdk {
                          opacity: 0 !important; pointer-events: none !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };

                  const findMainVideo = () =>
                    document.querySelector('video#epg-player_html5_api') ||
                    document.querySelector('video.vjs-tech') ||
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  const getVjsPlayer = (v) => {
                    try {
                      if (window.videojs) {
                        const players = window.videojs.getPlayers && window.videojs.getPlayers();
                        if (players && players['epg-player']) return players['epg-player'];
                      }
                      const wrapper = v && v.closest('.video-js');
                      if (wrapper && wrapper.player) return wrapper.player;
                    } catch {}
                    return null;
                  };

                  // Replicates clicking Pause then Play in the browser using Video.js's own API + DOM button
                  const kickPausePlay = (v) => {
                    if (window.__charonUsaKicking) return;
                    window.__charonUsaKicking = true;

                    const vjs = getVjsPlayer(v);
                    const playBtn = document.querySelector('.vjs-play-control');

                    try {
                      if (vjs && typeof vjs.pause === 'function') vjs.pause();
                      else if (playBtn) playBtn.click();
                      else v.pause();
                    } catch {}

                    setTimeout(() => {
                      try {
                        if (vjs && typeof vjs.play === 'function') vjs.play();
                        else if (playBtn && v.paused) playBtn.click();
                        v.play().catch(() => {});
                      } catch {}
                      setTimeout(() => { window.__charonUsaKicking = false; }, 1000);
                    }, 400);
                  };

                  window.__charonIsPaused = () => {
                    if (window.__charonUsaKicking) return false;
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();

                    const v = findMainVideo();
                    if (!v || window.__charonUsaKicking) return;

                    // 1. Play FIRST while muted so Chrome autoplay never blocks it
                    if (v.paused) {
                      const bigPlay = document.querySelector('button.vjs-big-play-button, .vjs-play-control.vjs-paused');
                      if (bigPlay) bigPlay.click();
                      v.play().catch(() => {});
                      return;
                    }

                    // 2. Unmute once playing
                    if (v.muted || v.volume === 0) {
                      const vjs = getVjsPlayer(v);
                      if (vjs && typeof vjs.muted === 'function') {
                        try { vjs.muted(false); vjs.volume(1); } catch {}
                      }
                      const muteBtn = document.querySelector('.vjs-mute-control.vjs-vol-0, button[title*="Unmute" i], button[aria-label*="Unmute" i]');
                      if (muteBtn) muteBtn.click();
                      v.muted = false;
                      v.volume = 1.0;
                    } else if (v.volume < 1.0) {
                      v.volume = 1.0;
                    }

                    // 3. Proactive Startup Kick: Once video has played for ~2 seconds, do a single
                    // Pause -> Unpause cycle automatically so the initial buffer never freezes
                    if (!window.__charonDidInitialKick && v.currentTime > 0 && v.videoWidth > 0) {
                      if (!window.__charonPlayStartTime) {
                        window.__charonPlayStartTime = Date.now();
                      } else if (Date.now() - window.__charonPlayStartTime > 2000) {
                        window.__charonDidInitialKick = true;
                        kickPausePlay(v);
                        return;
                      }
                    }

                    // 4. Ongoing Decoded-Frame & Time Stall Watchdog:
                    // Checks both currentTime AND totalVideoFrames (catches freezes where audio continues or time ticks)
                    const frames = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality().totalVideoFrames : v.webkitDecodedFrameCount || 0;
                    const time = v.currentTime;

                    if (time > 0 && window.__charonDidInitialKick) {
                      const timeStuck = window.__charonLastTime === time;
                      const framesStuck = frames > 0 && window.__charonLastFrames === frames;

                      if (timeStuck || framesStuck) {
                        window.__charonStallTicks = (window.__charonStallTicks || 0) + 1;
                        if (window.__charonStallTicks >= 6) { // ~1.8 seconds frozen
                          window.__charonStallTicks = 0;
                          kickPausePlay(v);
                          return;
                        }
                      } else {
                        window.__charonLastTime = time;
                        window.__charonLastFrames = frames;
                        window.__charonStallTicks = 0;
                      }
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