import {
  parseConsoleBinaryPacket,
  createConsoleRenderer,
  mapKeyEventToScanCode,
  resolveRemoteCursorStyle,
  resolveEnhancedDesktopScale,
  selectInitialConsoleEncoding,
  selectNegotiatedConsoleEncoding,
  computeScaledSurfaceCssDimensions,
  isH264WebCodecsSupported,
  encodeClipboardTextMessage,
  decodeClipboardTextMessage,
  encodeConsoleAudioInputPacket,
  CTRL_ALT_DEL_SEQUENCE
} from './console-wire.js';

export function createConsoleController({
  getVm,
  getAllVms,
  getAdvSettings,
  getTheme,
  getAccentHex,
  getShowExtensionEntries,
  getOpenVmIds,
  getDefaultPowerAction,
  getServerConnection,
  executeVmPowerAction,
  isHostedInWinOnWeb = false,
  onVmPowerChanged,
  onActiveVmChanged,
  onFocusExistingWindow,
  onOpenNewWindow,
  onDragSpacerRect,
  onRequestClose
}) {
  const win = document.getElementById('vmConsoleWindow');
  if (!win) return { open: () => {}, openTab: () => {}, syncTheme: () => {}, syncVmState: () => {} };

  const titleBar = document.getElementById('consoleTitleBar');
  const dragSpacer = document.getElementById('consoleDragSpacer');

  const switcherBtn = document.getElementById('consoleSwitcherBtn');
  const switcherOsIcon = document.getElementById('consoleSwitcherOsIcon');
  const switcherName = document.getElementById('consoleSwitcherName');
  const switcherFlyout = document.getElementById('consoleSwitcherFlyout');
  const switcherList = document.getElementById('consoleSwitcherList');

  const minBtn = document.getElementById('consoleMinimizeBtn');
  const maxBtn = document.getElementById('consoleMaximizeBtn');
  const maxIcon = document.getElementById('consoleMaximizeIcon');
  const closeBtn = document.getElementById('consoleCloseBtn');

  const powerSplit = document.getElementById('consolePowerSplit');
  const powerMainBtn = document.getElementById('consolePowerMainBtn');
  const powerMainIcon = document.getElementById('consolePowerMainIcon');
  const powerDropBtn = document.getElementById('consolePowerDropBtn');
  const powerFlyout = document.getElementById('consolePowerFlyout');

  const resAnchor = document.getElementById('consoleResAnchor');
  const resBtn = document.getElementById('consoleResBtn');
  const resLabel = document.getElementById('consoleResLabel');
  const resFlyout = document.getElementById('consoleResFlyout');

  const scaleBtn = document.getElementById('consoleScaleBtn');
  const scaleLabel = document.getElementById('consoleScaleLabel');
  const scaleFlyout = document.getElementById('consoleScaleFlyout');

  const modeNames = { basic: '基本会话', enhanced: '增强会话', external: '超强会话' };
  const accessStatus = document.getElementById('consoleAccessStatus');
  const takeControl = document.getElementById('consoleTakeControl');
  const displayModes = document.getElementById('consoleDisplayModes');
  const watchOnly = document.getElementById('consoleWatchOnly');
  const remotePointer = document.getElementById('consoleRemotePointer');
  const fallbackEnhanced = document.getElementById('consoleFallbackEnhanced');
  const modeBtn = document.getElementById('consoleModeBtn');
  const modeLabel = document.getElementById('consoleModeLabel');
  const modeFlyout = document.getElementById('consoleModeFlyout');

  const qualityAnchor = document.getElementById('consoleQualityAnchor');
  const qualityBtn = document.getElementById('consoleQualityBtn');
  const qualityLabel = document.getElementById('consoleQualityLabel');
  const qualityFlyout = document.getElementById('consoleQualityFlyout');

  const cadBtn = document.getElementById('consoleCadBtn');
  const shotBtn = document.getElementById('consoleShotBtn');
  const audioBtn = document.getElementById('consoleAudioBtn');
  const audioIcon = document.getElementById('consoleAudioIcon');
  const micBtn = document.getElementById('consoleMicBtn');
  const micIcon = document.getElementById('consoleMicIcon');
  const clipAnchor = document.getElementById('consoleClipAnchor');
  const clipBtn = document.getElementById('consoleClipBtn');
  const clipFlyout = document.getElementById('consoleClipFlyout');
  const clipSendBtn = document.getElementById('consoleClipSendBtn');
  const clipCopyBtn = document.getElementById('consoleClipCopyBtn');
  const clipCopyLabel = document.getElementById('consoleClipCopyLabel');
  const clipInput = document.getElementById('consoleClipInput');
  const clipManualSendBtn = document.getElementById('consoleClipManualSendBtn');
  const fullscreenBtn = document.getElementById('consoleFullscreenBtn');
  const fullscreenIcon = document.getElementById('consoleFullscreenIcon');

  const fsBar = document.getElementById('consoleFsBar');
  const fsPinBtn = document.getElementById('consoleFsPinBtn');
  const fsVmName = document.getElementById('consoleFsVmName');
  const fsCadBtn = document.getElementById('consoleFsCadBtn');
  const fsModeBtn = document.getElementById('consoleFsModeBtn');
  const fsModeLabel = document.getElementById('consoleFsModeLabel');
  const fsExitBtn = document.getElementById('consoleFsExitBtn');

  const viewport = document.getElementById('consoleViewport');
  const toastEl = document.getElementById('consoleToast');
  const toastIconEl = document.getElementById('consoleToastIcon');
  const toastTextEl = document.getElementById('consoleToastText');
  const screenSurface = document.getElementById('consoleScreenSurface');
  const liveCanvas = document.getElementById('consoleLiveCanvas');
  const guestDesktop = document.getElementById('consoleGuestDesktop');
  const guestSessionTag = document.getElementById('consoleGuestSessionTag');
  const guestHostname = document.getElementById('consoleGuestHostname');
  const guestResLabel = document.getElementById('consoleGuestResLabel');

  const emptyState = document.getElementById('consoleEmptyState');
  const emptyCard = emptyState?.querySelector('.console-empty-card');
  const emptySpinner = document.getElementById('consoleEmptySpinner');
  const emptyOs = document.getElementById('consoleEmptyOs');
  const emptyTitle = document.getElementById('consoleEmptyTitle');
  const emptyDesc = document.getElementById('consoleEmptyDesc');
  const primaryActionBtn = document.getElementById('consolePrimaryActionBtn');
  const emptyActions = primaryActionBtn?.parentElement;

  const sessions = new Map();
  const serverSupportedEncodingsCache = new Map();
  const consoleState = {
    isOpen: false,
    isFullscreen: false,
    fsPinned: true,
    activeVmId: null
  };

  let toastTimer = null;
  let fsHideTimer = null;
  let renderer = null;
  let pendingPointerMove = null;
  let pointerMoveRafId = 0;
  let webCodecsH264Supported = typeof VideoDecoder !== 'undefined' && typeof EncodedVideoChunk !== 'undefined';
  const showExtensionEntriesEnabled = () => {
    if (typeof getShowExtensionEntries === 'function') return Boolean(getShowExtensionEntries());
    try { return localStorage.getItem('exhyperv.extensions.showEntries') !== '0'; } catch { return true; }
  };
  void isH264WebCodecsSupported().then((supported) => {
    webCodecsH264Supported = supported;
  });

  if (screenSurface && !screenSurface.hasAttribute('tabindex')) {
    screenSurface.setAttribute('tabindex', '-1');
  }

  function clearPendingPointerMove() {
    pendingPointerMove = null;
    if (pointerMoveRafId) {
      if (typeof cancelAnimationFrame === 'function') cancelAnimationFrame(pointerMoveRafId);
      else clearTimeout(pointerMoveRafId);
      pointerMoveRafId = 0;
    }
  }

  function flushPendingPointerMove() {
    pointerMoveRafId = 0;
    const move = pendingPointerMove;
    pendingPointerMove = null;
    if (!move) return;
    const { session, clientX, clientY } = move;
    if (!session || session !== activeSession() || session.connState !== 'connected' || !session.liveStreamActive || !session.inputCaptured) return;
    const coords = mapPointerCoords(session, clientX, clientY);
    if (coords) {
      sendSocketJson(session, {
        type: 'pointer',
        x: coords.x,
        y: coords.y,
        button: 'None',
        down: false,
        wheel: 0
      });
    }
  }

  function ensureRenderer() {
    if (!renderer && liveCanvas) {
      renderer = createConsoleRenderer(liveCanvas, {
        onDisplayed(sequence) {
          const session = activeSession();
          if (session?.protocol === 2 && Number.isSafeInteger(sequence)) sendSocketJson(session, { type: 'displayed', sequence });
        },
        onCodecError: (_codec, _err) => {
          const session = activeSession();
          if (!session || !session.ws || session.ws.readyState !== WebSocket.OPEN) return;
          if (session.activeEncoding === 'h264') {
            session.h264RuntimeFailed = true;
            const fallback = typeof DecompressionStream !== 'undefined' ? 'deflate' : 'png';
            session.activeEncoding = fallback;
            if (session.encodingPref === 'h264') session.encodingPref = 'auto';
            sendSocketJson(session, { type: 'setEncoding', encoding: fallback });
            render();
            showToast('视频硬解不可用，已自动切换至压缩流', 'warning');
          }
        },
        onNeedKeyFrame: () => {
          const session = activeSession();
          if (session?.liveStreamActive) {
            sendSocketJson(session, { type: 'captureFrame' });
          }
        }
      });
    }
    return renderer;
  }

  function resolveSessionConsoleFeatures(session) {
    const adv = session?.adv || {};
    return {
      clipboard: adv.consoleClipboard !== false,
      audioOutput: adv.consoleAudioOutput !== false,
      audioInput: adv.consoleAudioInput !== false
    };
  }

  function stopSessionMicrophone(session) {
    if (!session) return;
    session.micEnabled = false;
    session.micStarting = false;
    if (session._micProcessor) {
      try {
        session._micProcessor.onaudioprocess = null;
        session._micProcessor.disconnect();
      } catch {}
      session._micProcessor = null;
    }
    if (session._micGain) {
      try {
        session._micGain.disconnect();
      } catch {}
      session._micGain = null;
    }
    if (session._micSource) {
      try {
        session._micSource.disconnect();
      } catch {}
      session._micSource = null;
    }
    if (session._micStream) {
      try {
        session._micStream.getTracks().forEach((t) => t.stop());
      } catch {}
      session._micStream = null;
    }
    if (session._micContext) {
      try {
        void session._micContext.close();
      } catch {}
      session._micContext = null;
    }
  }

  async function startSessionMicrophone(session) {
    if (!session || session.micStarting) return;
    const features = resolveSessionConsoleFeatures(session);
    const format = session.capabilities?.audioInputFormat;
    if (
      session.connState !== 'connected' ||
      session.mode !== 'enhanced' ||
      !features.audioInput ||
      !session.capabilities?.audioInputAvailable ||
      !format
    ) {
      showToast('当前会话尚未就绪或未启用麦克风输入通道', 'warning');
      return;
    }
    if (!navigator.mediaDevices?.getUserMedia) {
      showToast('当前浏览器环境不支持麦克风采集（需 localhost 或 HTTPS 上下文）', 'error');
      return;
    }
    const ws = session.ws;
    const token = session.connectToken;
    session.micStarting = true;
    session.micEnabled = true;
    render();
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true
        }
      });
      if (!session.micEnabled || session.ws !== ws || session.connectToken !== token || session.connState !== 'connected') {
        stream.getTracks().forEach((t) => t.stop());
        session.micStarting = false;
        render();
        return;
      }
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) {
        stream.getTracks().forEach((t) => t.stop());
        throw new Error('WebAudio 不可用');
      }
      const audioCtx = new AudioCtx();
      if (audioCtx.state === 'suspended') {
        await audioCtx.resume();
      }
      const source = audioCtx.createMediaStreamSource(stream);
      const inputChannels = Math.max(1, Math.min(2, source.channelCount || 1));
      const processor = audioCtx.createScriptProcessor(1024, inputChannels, 1);
      const silentGain = audioCtx.createGain();
      silentGain.gain.value = 0;
      const startTimeMs = performance.now();

      processor.onaudioprocess = (ev) => {
        if (!session.micEnabled || session.ws !== ws || ws.readyState !== WebSocket.OPEN) return;
        const activeFormat = session.capabilities?.audioInputFormat;
        if (!session.capabilities?.audioInputAvailable || !activeFormat) return;
        const chData = [];
        for (let ch = 0; ch < ev.inputBuffer.numberOfChannels; ch++) {
          chData.push(ev.inputBuffer.getChannelData(ch));
        }
        const elapsedTicks = BigInt(Math.max(0, Math.round((performance.now() - startTimeMs) * 10000)));
        const packet = encodeConsoleAudioInputPacket(activeFormat, chData, audioCtx.sampleRate, elapsedTicks);
        if (packet && ws.bufferedAmount < 65536 && ws.readyState === WebSocket.OPEN) {
          try {
            if (session.protocol === 2) {
              if (!session.control?.hasControl) return;
              const prefixed = new Uint8Array(packet.length + 16);
              const header = new DataView(prefixed.buffer);
              header.setBigInt64(0, BigInt(session.control.generation), true);
              header.setBigInt64(8, BigInt(++session.inputSequence), true);
              prefixed.set(packet, 16);
              ws.send(prefixed);
            } else ws.send(packet);
          } catch {}
        }
      };

      source.connect(processor);
      processor.connect(silentGain);
      silentGain.connect(audioCtx.destination);
      stream.getAudioTracks()[0]?.addEventListener('ended', () => {
        if (session._micStream === stream) {
          stopSessionMicrophone(session);
          render();
          showToast('麦克风设备已断开', 'warning');
        }
      });

      session._micStream = stream;
      session._micContext = audioCtx;
      session._micSource = source;
      session._micProcessor = processor;
      session._micGain = silentGain;
      session.micStarting = false;
      render();
      showToast('麦克风输入已开启');
    } catch (err) {
      stopSessionMicrophone(session);
      render();
      showToast(err?.message ? `无法开启麦克风：${err.message}` : '无法开启麦克风', 'error');
    }
  }

  function closeSessionSocket(session) {
    if (!session) return;
    stopSessionMicrophone(session);
    clearPendingPointerMove();
    session.connectToken = (session.connectToken || 0) + 1;
    if (session.connectTimer) {
      clearTimeout(session.connectTimer);
      session.connectTimer = null;
    }
    const ws = session.ws;
    session.ws = null;
    session.liveStreamActive = false;
    session.cursorStyle = 'default';
    session.cursorReceived = false;
    session.cursorToken = (session.cursorToken || 0) + 1;
    if (ws) {
      if (ws.readyState !== WebSocket.CLOSED) {
        session.closing = new Promise(resolve => ws.addEventListener('close', resolve, { once: true }));
      }
      try {
        if (ws.readyState === WebSocket.OPEN && session.connState === 'connected' && session.inputCaptured) {
          ws.send(JSON.stringify({ type: 'releaseAllInputs', generation: session.control?.generation, inputSequence: ++session.inputSequence }));
        }
        ws.close(1000, 'client closed');
      } catch {}
    }
    session.inputCaptured = false;
  }

  function sendSocketJson(session, payload) {
    const ws = session?.ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) return false;
    if (ws.bufferedAmount > 65536 && payload.type === 'pointer' && !payload.button && !payload.wheel) return false;
    if (payload.type === 'clipboard' && ws.bufferedAmount > 65536) return false;
    try {
      if (session.protocol === 2) {
        const local = ['captureFrame', 'displayed', 'setEncoding', 'ping', 'takeControl'].includes(payload.type);
        if (!local && !session.control?.hasControl) return false;
        payload = { ...payload, generation: session.control?.generation || 0, inputSequence: ++session.inputSequence };
      }
      ws.send(JSON.stringify(payload));
      return true;
    } catch {
      return false;
    }
  }

  function effectiveSessionScale(session, width) {
    if (!session || session.mode !== 'enhanced') return 100;
    return resolveEnhancedDesktopScale(
      session.desktopScale,
      width,
      typeof window !== 'undefined' ? window.devicePixelRatio : 1
    );
  }

  function parseResolutionTuple(resKey) {
    const [wStr, hStr] = String(resKey || '1280x720').split('x');
    return {
      width: Math.max(320, Number.parseInt(wStr, 10) || 1280),
      height: Math.max(240, Number.parseInt(hStr, 10) || 720)
    };
  }

  async function resolveLiveConnection(session) {
    if (typeof getServerConnection === 'function') {
      return await getServerConnection(session?.vm, session.mode, Boolean(session.watchOnly), session.ceiling);
    }
    if (session?.vm?.serverMode === 'live' && session?.vm?.serverUrl) {
      return {
        mode: 'live',
        serverUrl: session.vm.serverUrl,
        ticket: session.vm.wsTicket || ''
      };
    }
    return null;
  }

  function isSessionLive(session) {
    return Boolean(session && (session.liveMode || session.vm?.serverMode === 'live'));
  }

  function activeSession() {
    return consoleState.activeVmId ? sessions.get(consoleState.activeVmId) || null : null;
  }

  function showToast(text, severity = 'success') {
    if (!toastEl || !toastTextEl) return;
    const tone = severity === 'error' || severity === 'warning' ? severity : 'success';
    toastEl.classList.remove('is-success', 'is-warning', 'is-error');
    toastEl.classList.add(`is-${tone}`);
    if (toastIconEl) {
      if (tone === 'error') {
        toastIconEl.className = 'fi console-toast-icon';
        toastIconEl.textContent = '\uE711';
      } else if (tone === 'warning') {
        toastIconEl.className = 'fi fi-fluent console-toast-icon';
        toastIconEl.textContent = '\uF13C';
      } else {
        toastIconEl.className = 'fi console-toast-icon';
        toastIconEl.textContent = '\uE73E';
      }
    }
    toastTextEl.textContent = text;
    toastEl.hidden = false;
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
      toastEl.hidden = true;
    }, tone === 'error' ? 3200 : 2000);
  }

  function formatResText(resKey) {
    return String(resKey || '1280x720').replace('x', ' × ');
  }

  function formatHeaderResText(resKey) {
    return String(resKey || '1280x720').replace(/\s*x\s*/i, '×');
  }

  function escapeHtml(text) {
    return String(text ?? '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function closeAllFlyouts() {
    if (powerFlyout) powerFlyout.hidden = true;
    if (switcherFlyout) switcherFlyout.hidden = true;
    if (resFlyout) resFlyout.hidden = true;
    if (scaleFlyout) scaleFlyout.hidden = true;
    if (modeFlyout) modeFlyout.hidden = true;
    if (qualityFlyout) qualityFlyout.hidden = true;
    if (clipFlyout) clipFlyout.hidden = true;
  }

  function syncTheme() {
    const theme = getTheme ? getTheme() : 'dark';
    const accent = getAccentHex ? getAccentHex() : '#4CC2FF';
    win.setAttribute('data-theme', theme === 'light' ? 'light' : 'dark');
    win.style.setProperty('--VmHeaderCheckedBrush', accent);
  }

  function notifyDragSpacerRect() {
    if (!dragSpacer || typeof onDragSpacerRect !== 'function') return;
    requestAnimationFrame(() => {
      const r = dragSpacer.getBoundingClientRect();
      onDragSpacerRect({ left: Math.round(r.left), width: Math.max(0, Math.round(r.width)) });
    });
  }

  if (dragSpacer && typeof ResizeObserver !== 'undefined') {
    const ro = new ResizeObserver(() => notifyDragSpacerRect());
    ro.observe(dragSpacer);
  }

  function startSessionConnecting(session, targetMode) {
    closeSessionSocket(session);
    session.mode = targetMode;
    session.connState = 'connecting';
    session.inputCaptured = false;
    session.liveError = '';
    session.capabilities = null;
    session.remotePosition = null; session.remoteShape = null; session.control = null; session.extension = null;
    const token = session.connectToken;
    render();

    Promise.resolve(session.closing)
      .then(() => token === session.connectToken ? resolveLiveConnection(session) : null)
      .then((conn) => {
        if (token !== session.connectToken || !consoleState.isOpen || !sessions.has(session.id)) return;
        const isGen1 = session.vm.generation === 1 || session.vm.gen === 1;
        const consoleOff = !isGen1 && (Boolean(session.adv.shielding) || session.adv.consoleSupport === false);
        if (!session.vm.running) {
          session.connState = 'stopped';
          render();
          return;
        }
        if (consoleOff) {
          session.connState = 'disabled';
          render();
          return;
        }

        if ((targetMode === 'external' || session.watchOnly) && conn?.protocol !== 2)
          throw new Error(targetMode === 'external' ? '宿主扩展不可达（EXT-001），请在宿主桌面端查看扩展状态' : '此连接无法授权仅观看会话');
        if (!conn || conn.mode !== 'live' || !conn.serverUrl) {
          session.liveMode = false;
          session.liveStreamActive = false;
          session.connectTimer = setTimeout(() => {
            if (token !== session.connectToken) return;
            session.connState = 'connected';
            render();
          }, 440);
          return;
        }

        session.liveMode = true;
        session.liveStreamActive = false;
        session.serverUrl = conn.serverUrl;
        const cachedSupported =
          conn.supportedEncodings ||
          conn.serverInfo?.supportedConsoleEncodings ||
          serverSupportedEncodingsCache.get(conn.serverUrl) ||
          null;
        const initialEncoding = selectInitialConsoleEncoding({
          serverUrl: conn.serverUrl,
          preference: session.encodingPref || 'auto',
          serverSupportedEncodings: cachedSupported,
          hasWebCodecs: webCodecsH264Supported && !session.h264RuntimeFailed,
          hasDecompressionStream: typeof DecompressionStream !== 'undefined'
        });
        session.activeEncoding = initialEncoding;
        const { width, height } = parseResolutionTuple(session.resolution);
        const scale = targetMode === 'enhanced'
          ? resolveEnhancedDesktopScale(session.desktopScale, width, typeof window !== 'undefined' ? window.devicePixelRatio : 1)
          : 100;
        const features = resolveSessionConsoleFeatures(session);
        session.rights = conn.rights;
        session.protocol = conn.protocol === 2 ? 2 : 1;
        session.control = null;
        session.inputSequence = 0;
        session.extension = conn.extension;
        const wsUrl = new URL(`/ws/v${session.protocol}/console/${encodeURIComponent(session.vm.id)}`, conn.serverUrl);
        wsUrl.protocol = wsUrl.protocol === 'https:' ? 'wss:' : 'ws:';
        wsUrl.searchParams.set('backend', conn.backendId || (targetMode === 'enhanced' ? 'RdpEnhanced' : 'RdpBasic'));
        wsUrl.searchParams.set('frameEncoding', initialEncoding);
        if (targetMode !== 'external') {
          wsUrl.searchParams.set('width', String(width));
          wsUrl.searchParams.set('height', String(height));
          wsUrl.searchParams.set('desktopScale', String(scale));
        }
        wsUrl.searchParams.set('hostCursor', 'true');
        wsUrl.searchParams.set('clipboard', features.clipboard ? 'true' : 'false');
        wsUrl.searchParams.set('audioOutput', features.audioOutput ? 'true' : 'false');
        wsUrl.searchParams.set('audioInput', features.audioInput ? 'true' : 'false');
        if (conn.ticket) {
          wsUrl.searchParams.set('ticket', conn.ticket);
        }

        const ws = new WebSocket(wsUrl.toString());
        ws.binaryType = 'arraybuffer';
        session.ws = ws;

        function handleBinaryBuffer(buffer) {
          if (!buffer || token !== session.connectToken || session.ws !== ws) return;
          const packet = parseConsoleBinaryPacket(buffer, session.protocol);
          if (!packet) { ws.close(1002, '画面协议无效'); return; }
          if (packet.kind === 'video' || packet.kind === 'h264') {
            const sizeChanged = session.frameWidth !== packet.width || session.frameHeight !== packet.height;
            session.frameWidth = packet.width;
            session.frameHeight = packet.height;
            session.liveError = '';
            if (packet.kind === 'h264') {
              session.activeEncoding = 'h264';
              session.h264Hardware = Boolean(packet.isHardware);
            } else if (packet.encoding !== 'regions' && !(session.activeEncoding === 'h264' && packet.encoding === 'bgra')) {
              session.activeEncoding = packet.encoding;
            }
            if (session.connState === 'connecting') {
              session.connState = 'connected';
              session.liveStreamActive = true;
              render();
            } else if (sizeChanged && session.id === consoleState.activeVmId) {
              render();
            }
            if (session.id === consoleState.activeVmId) {
              ensureRenderer()?.enqueueVideo(packet);
            }
          } else if (packet.kind === 'audio') {
            if (session.id === consoleState.activeVmId) {
              ensureRenderer()?.playAudio(packet, Boolean(session.muted));
            }
          }
        }

        ws.onmessage = (event) => {
          if (token !== session.connectToken || session.ws !== ws) return;
          if (typeof event.data === 'string') {
            let msg = null;
            try {
              msg = JSON.parse(event.data);
            } catch {
              return;
            }
            if (!msg || typeof msg !== 'object') return;
            if (msg.type === 'state') {
              if (msg.state === 'Connected' || msg.state === 'LoginCompleted') {
                session.connState = 'connected';
                session.liveStreamActive = true;
                session.liveError = '';
                if (session.id === consoleState.activeVmId) {
                  ensureRenderer()?.reset();
                }
                render();
              } else if (msg.state === 'Connecting') {
                session.connState = 'connecting';
                render();
              } else if (msg.state === 'Disconnected') {
                session.liveError = msg.error || (msg.errorCode ? `连接中断（错误码 ${msg.errorCode}）` : '');
                closeSessionSocket(session);
                session.connState = session.vm.running ? 'disconnected' : 'stopped';
                session.inputCaptured = false;
                render();
              }
            } else if (msg.type === 'control') {
              session.control = msg;
              if (!msg.hasControl) { stopSessionMicrophone(session); session.inputCaptured = false; }
              render();
            } else if (msg.type === 'transport') {
              session.extension = { ...session.extension, transportKind: msg.transportKind === 'GpuShared' ? 2 : 1, surfaceEpoch: msg.surfaceEpoch };
              render();
            } else if (msg.type === 'cursorState') {
              session.remotePosition = msg.state;
              render();
            } else if (msg.type === 'displayMode') {
              if (msg.result?.activeMode?.id) displayModes.value = msg.result.activeMode.id;
            } else if (msg.type === 'capabilities') {
              session.capabilities = msg;
              if (!msg.audioInputAvailable && (session.micEnabled || session.micStarting)) {
                stopSessionMicrophone(session);
              }
              if (Array.isArray(msg.supportedEncodings) && session.serverUrl) {
                serverSupportedEncodingsCache.set(session.serverUrl, msg.supportedEncodings);
              }
              if (typeof msg.activeEncoding === 'string') {
                session.activeEncoding = msg.activeEncoding;
              }
              const desiredEncoding = selectNegotiatedConsoleEncoding({
                serverUrl: session.serverUrl,
                preference: session.encodingPref || 'auto',
                capabilities: msg,
                hasWebCodecs: webCodecsH264Supported && !session.h264RuntimeFailed,
                hasDecompressionStream: typeof DecompressionStream !== 'undefined'
              });
              if (
                desiredEncoding !== session.activeEncoding &&
                Array.isArray(msg.supportedEncodings) &&
                msg.supportedEncodings.includes(desiredEncoding)
              ) {
                if (sendSocketJson(session, { type: 'setEncoding', encoding: desiredEncoding })) {
                  session.activeEncoding = desiredEncoding;
                }
              }
              render();
            } else if (msg.type === 'encodingChanged') {
              if (typeof msg.encoding === 'string') {
                session.activeEncoding = msg.encoding;
                render();
              }
            } else if (msg.type === 'cursor') {
              session.remoteShape = msg.visible ? msg : null;
              render();
              if (session.remotePosition) return;
              const cursorToken = (session.cursorToken = (session.cursorToken || 0) + 1);
              if (!msg.visible) {
                session.cursorStyle = session.cursorReceived ? 'none' : 'default';
                if (session.id === consoleState.activeVmId && screenSurface) {
                  screenSurface.style.cursor = session.liveStreamActive ? session.cursorStyle : '';
                }
              } else {
                session.cursorReceived = true;
                const resolved = resolveRemoteCursorStyle(msg);
                if (typeof resolved === 'string') {
                  session.cursorStyle = resolved;
                  if (session.id === consoleState.activeVmId && screenSurface) {
                    screenSurface.style.cursor = session.liveStreamActive ? resolved : '';
                  }
                } else if (resolved && typeof resolved.then === 'function') {
                  void resolved.then((cssValue) => {
                    if (token !== session.connectToken || session.ws !== ws || cursorToken !== session.cursorToken) return;
                    session.cursorStyle = cssValue || 'default';
                    if (session.id === consoleState.activeVmId && screenSurface) {
                      screenSurface.style.cursor = session.liveStreamActive ? session.cursorStyle : '';
                    }
                  });
                }
              }
            } else if (msg.type === 'clipboard') {
              const text = decodeClipboardTextMessage(msg);
              if (text !== null) {
                session.lastGuestClipboardText = text;
                if (typeof document !== 'undefined' && document.hasFocus?.() && navigator.clipboard?.writeText) {
                  void navigator.clipboard.writeText(text).catch(() => {});
                }
                render();
              }
            } else if (msg.type === 'resizeRejected') {
              showToast(
                msg.errorCode ? `虚拟机未接受当前分辨率变更（${msg.errorCode}）` : '当前会话暂不支持动态调整分辨率',
                'warning'
              );
            } else if (msg.type === 'error') {
              const errMsg = msg.error || '控制台会话发生错误';
              showToast(errMsg, 'error');
              if (session.connState === 'connecting') {
                session.liveError = errMsg;
                closeSessionSocket(session);
                session.connState = session.vm.running ? 'disconnected' : 'stopped';
                render();
              }
            }
            return;
          }

          if (event.data instanceof ArrayBuffer) {
            handleBinaryBuffer(event.data);
          } else if (event.data instanceof Blob) {
            void event.data.arrayBuffer().then(handleBinaryBuffer);
          }
        };

        ws.onerror = () => {
          if (token !== session.connectToken || session.ws !== ws) return;
          if (!session.liveError) {
            session.liveError = 'WebSocket 控制台通道连接失败，请检查服务端运行状态或网络可达性';
          }
        };

        ws.onclose = (ev) => {
          if (token !== session.connectToken || session.ws !== ws) return;
          session.ws = null;
          session.liveStreamActive = false;
          session.inputCaptured = false;
          if (session.connState === 'connecting' || session.connState === 'connected') {
            session.connState = session.vm.running ? 'disconnected' : 'stopped';
            if (!session.liveError && ev?.reason) {
              session.liveError = ev.reason;
            }
            render();
          }
        };
      })
      .catch((err) => {
        if (token !== session.connectToken || !consoleState.isOpen || !sessions.has(session.id)) return;
        session.liveMode = true;
        session.liveStreamActive = false;
        session.connState = session.vm.running ? 'disconnected' : 'stopped';
        session.liveError = err?.message || '无法获取控制台连接票据';
        render();
        showToast(session.liveError, 'error');
      });
  }

  function render() {
    const session = activeSession();
    if (!session) return;

    const vm = session.vm;
    const adv = session.adv;
    const isGen1 = vm.generation === 1 || vm.gen === 1;
    const consoleOff = !isGen1 && (Boolean(adv.shielding) || adv.consoleSupport === false);
    const isRunning = Boolean(vm.running);

    if (!isRunning && session.connState !== 'connecting') {
      closeSessionSocket(session);
      session.connState = 'stopped';
      session.inputCaptured = false;
    } else if (consoleOff && session.connState !== 'connecting') {
      closeSessionSocket(session);
      session.connState = 'disabled';
      session.inputCaptured = false;
    }

    const vmName = vm.name || '虚拟机';
    const osPath = vm.osIcon ? `/ui-design/os/${vm.osIcon}` : '/ui-design/os/Windows.svg';

    if (switcherOsIcon) switcherOsIcon.src = osPath;
    if (switcherName) switcherName.textContent = vmName;
    if (switcherBtn) switcherBtn.title = `${vmName}（切换虚拟机或在新窗口打开）`;

    if (emptyOs) emptyOs.src = osPath;
    if (fsVmName) fsVmName.textContent = vmName;
    if (guestHostname) guestHostname.textContent = vmName.toUpperCase().replace(/[^A-Z0-9-]/g, '-');

    const external = session.mode === 'external';
    const isEnhanced = session.mode !== 'basic';
    const control = session.control;
    const path = session.extension?.transportKind;
    const labels = [control && !control.hasControl ? '仅观看' : '', path === 1 ? 'CPU 共享' : path === 2 ? 'GPU 共享' : ''].filter(Boolean);
    if (accessStatus) { accessStatus.textContent = labels.join(' / '); accessStatus.hidden = !labels.length; }
    if (watchOnly) watchOnly.setAttribute('aria-pressed', String(Boolean(session.watchOnly)));
    if (takeControl) takeControl.hidden = !control?.canTake || control.hasControl;
    if (fallbackEnhanced) fallbackEnhanced.hidden = !external || !session.liveError;
    if (displayModes) {
      const modes = session.capabilities?.modes || [];
      displayModes.hidden = !modes.length;
      displayModes.disabled = !control?.hasControl;
      if (displayModes.dataset.modes !== JSON.stringify(modes)) {
        displayModes.dataset.modes = JSON.stringify(modes);
        displayModes.replaceChildren(...modes.map(m => new Option(m.width + ' × ' + m.height, m.id)));
      }
    }
    if (remotePointer) {
      const pos = session.remotePosition, shape = session.remoteShape;
      remotePointer.hidden = !(session.liveStreamActive && pos?.visible && !pos.composited && pos.coordinateSpace === 'DesktopPixels' && shape?.png && session.frameWidth);
      if (!remotePointer.hidden) {
        remotePointer.src = 'data:image/png;base64,' + shape.png;
        remotePointer.style.left = ((pos.x - shape.hotX) * 100 / session.frameWidth) + '%';
        remotePointer.style.top = ((pos.y - shape.hotY) * 100 / session.frameHeight) + '%';
        remotePointer.style.width = (shape.width * 100 / session.frameWidth) + '%';
      }
    }
    const connected = session.connState === 'connected';
    const showLiveCanvas = Boolean(connected && session.liveStreamActive);
    const canChangeRes =
      isRunning && !external && control?.hasControl !== false &&
      isEnhanced &&
      !isGen1 &&
      !(showLiveCanvas && session.capabilities && session.capabilities.supportsResize === false);
    const enhancedScaleVal = session.desktopScale >= 100 ? `${session.desktopScale}%` : 'fit';
    const zoomVal = isEnhanced
      ? enhancedScaleVal
      : (session.scaleMode === 'actual' ? '100%' : (session.scaleMode || 'fit'));
    const isMuted = Boolean(session.muted);

    const primaryPowerAction = String(getDefaultPowerAction?.() || session.vm.defaultPowerAction || 'Shutdown');
    const isSavePrimary = primaryPowerAction.toLowerCase() === 'save';

    if (powerSplit) powerSplit.hidden = false;
    if (powerMainBtn) {
      powerMainBtn.title = !isRunning ? '启动虚拟机' : (isSavePrimary ? '保存状态' : '关机');
      const iconEl = powerMainIcon || powerMainBtn.querySelector('.fi');
      if (iconEl) {
        if (!isRunning) {
          iconEl.className = 'fi fi-fluent-filled';
          iconEl.innerHTML = '&#xF610;';
        } else {
          iconEl.className = 'fi fi-fluent';
          iconEl.innerHTML = isSavePrimary ? '&#xF680;' : '&#xF60F;';
        }
      }
    }
    if (powerDropBtn) {
      powerDropBtn.disabled = !isRunning;
      powerDropBtn.title = isRunning ? '更多电源操作' : '虚拟机未运行时无其他电源操作';
    }
    if (!isRunning && powerFlyout) powerFlyout.hidden = true;
    const powerShutdownItem = powerFlyout?.querySelector('[data-console-power="shutdown"]');
    const powerSaveItem = powerFlyout?.querySelector('[data-console-power="save"]');
    if (powerShutdownItem) powerShutdownItem.hidden = !isSavePrimary;
    if (powerSaveItem) powerSaveItem.hidden = isSavePrimary;

    if (resAnchor) resAnchor.hidden = false;
    if (resBtn) resBtn.disabled = !canChangeRes;
    const displayedRes = showLiveCanvas && session.frameWidth > 0 && session.frameHeight > 0
      ? `${session.frameWidth}x${session.frameHeight}`
      : session.resolution;
    if (resLabel) resLabel.textContent = (isEnhanced && !isGen1) ? formatHeaderResText(displayedRes) : '-';
    resFlyout?.querySelectorAll('[data-console-res]').forEach((item) => {
      item.classList.toggle('is-selected', item.getAttribute('data-console-res') === session.resolution);
    });

    if (scaleBtn) scaleBtn.title = isEnhanced ? '调整增强会话桌面缩放' : '调整画面缩放比例';
    if (scaleLabel) {
      scaleLabel.textContent = zoomVal === 'physical' ? '1:1' : (zoomVal === 'fit' || zoomVal === 'auto') ? '自动' : zoomVal;
    }
    scaleFlyout?.querySelectorAll('[data-console-zoom]').forEach((item) => {
      const z = item.getAttribute('data-console-zoom');
      const scope = item.getAttribute('data-console-zoom-scope');
      if (scope === 'enhanced') item.hidden = !isEnhanced;
      else if (scope === 'basic') item.hidden = isEnhanced;
      item.classList.toggle('is-selected', z === zoomVal || (z === 'fit' && zoomVal === 'auto'));
    });

    if (qualityAnchor) qualityAnchor.hidden = !isSessionLive(session);
    if (qualityBtn) {
      qualityBtn.disabled = !isRunning || consoleOff;
      const encNames = { bgra: '无损 BGRA', h264: session.h264Hardware ? 'H.264 硬解' : 'H.264', deflate: 'Deflate 压缩', png: 'PNG 兼容' };
      const curName = encNames[session.activeEncoding] || '自动';
      qualityBtn.title = `画面编码：${session.encodingPref === 'auto' ? `自动协商（当前 ${curName}）` : curName}`;
    }
    if (qualityLabel) {
      const shortNames = { auto: '自动', bgra: '无损', h264: 'H.264', deflate: '压缩', png: 'PNG' };
      qualityLabel.textContent = shortNames[session.encodingPref || 'auto'] || '自动';
    }
    qualityFlyout?.querySelectorAll('[data-console-quality]').forEach((item) => {
      const q = item.getAttribute('data-console-quality');
      item.classList.toggle('is-selected', q === (session.encodingPref || 'auto'));
      if (q === 'h264') {
        const serverSupportsH264 = !session.capabilities || !Array.isArray(session.capabilities.supportedEncodings) || session.capabilities.supportedEncodings.includes('h264');
        item.disabled = !webCodecsH264Supported || !serverSupportsH264;
      }
    });

    if (modeBtn) modeBtn.disabled = !isRunning || consoleOff;
    if (modeLabel) modeLabel.textContent = modeNames[session.mode];
    const showExtEntry = showExtensionEntriesEnabled();
    modeFlyout?.querySelectorAll('[data-console-session]').forEach((item) => {
      const m = item.getAttribute('data-console-session');
      item.classList.toggle('is-selected', m === session.mode);
      if (m === 'enhanced') item.disabled = !isRunning || consoleOff || isGen1;
      if (m === 'external') item.hidden = !showExtEntry && !external;
    });
    if (fsModeBtn) {
      fsModeBtn.classList.toggle('is-active', isEnhanced);
      fsModeBtn.disabled = !isRunning || consoleOff || isGen1;
    }
    if (fsModeLabel) fsModeLabel.textContent = modeNames[session.mode];

    if (cadBtn) {
      cadBtn.hidden = false;
      cadBtn.disabled = !connected || control?.hasControl === false;
    }
    if (fsCadBtn) {
      fsCadBtn.hidden = false;
      fsCadBtn.disabled = !connected || control?.hasControl === false;
    }
    if (shotBtn) shotBtn.disabled = !connected;

    const features = resolveSessionConsoleFeatures(session);
    const caps = session.capabilities;
    const audioOutBlocked = !connected || !isEnhanced || !features.audioOutput || (caps && caps.audioOutputAvailable === false);
    if (audioBtn) {
      audioBtn.disabled = audioOutBlocked;
      audioBtn.classList.toggle('is-checked', !audioOutBlocked && isMuted);
      audioBtn.title = !isEnhanced
        ? '仅增强会话支持音频输出'
        : !features.audioOutput
          ? '已在「高级 -> 控制台」关闭声音输出'
          : (caps && caps.audioOutputAvailable === false)
            ? '来宾音频输出通道未就绪'
            : isMuted
              ? '已静音（点击开启音频）'
              : '音频开启（点击静音）';
    }
    if (audioIcon) audioIcon.innerHTML = isMuted ? '&#xEB4B;' : '&#xEB43;';

    const micReady = connected && (!control || control.hasControl) && isEnhanced && features.audioInput && Boolean(caps?.audioInputAvailable && caps?.audioInputFormat);
    const micActive = Boolean(session.micEnabled);
    if (micBtn) {
      micBtn.hidden = !isEnhanced;
      micBtn.disabled = !micReady;
      micBtn.classList.toggle('is-recording', micReady && micActive);
      micBtn.title = !isEnhanced
        ? '仅增强会话支持麦克风输入'
        : !features.audioInput
          ? '已在「高级 -> 控制台」关闭麦克风输入'
          : !micReady
            ? '等待连接或来宾音频输入通道就绪'
            : micActive
              ? '麦克风输入开启中（点击静音）'
              : '麦克风已静音（点击开启并发送到虚拟机）';
    }
    if (micIcon) micIcon.innerHTML = micActive ? '&#xE720;' : '&#xF781;';

    const clipReady = connected && isEnhanced && features.clipboard && (!caps || caps.clipboardAvailable !== false);
    const guestClipText = typeof session.lastGuestClipboardText === 'string' ? session.lastGuestClipboardText : '';
    if (clipAnchor) clipAnchor.hidden = !isEnhanced;
    if (clipBtn) {
      clipBtn.disabled = !clipReady;
      clipBtn.title = !isEnhanced
        ? '仅增强会话支持剪贴板同步'
        : !features.clipboard
          ? '已在「高级 -> 控制台」关闭剪贴板共享'
          : !clipReady
            ? '等待连接或来宾剪贴板通道就绪'
            : '剪贴板文本同步（点击发送或复制）';
    }
    if (!clipReady && clipFlyout && !clipFlyout.hidden) {
      clipFlyout.hidden = true;
    }
    if (clipSendBtn) clipSendBtn.disabled = !clipReady || control?.hasControl === false;
    if (clipManualSendBtn) clipManualSendBtn.disabled = !clipReady || control?.hasControl === false;
    if (clipCopyBtn) clipCopyBtn.disabled = !clipReady || !guestClipText;
    if (clipCopyLabel) {
      if (guestClipText) {
        const preview = guestClipText.replace(/\s+/g, ' ').trim().slice(0, 18);
        clipCopyLabel.textContent = `复制来宾文本（${preview}${guestClipText.length > 18 ? '…' : ''}）`;
      } else {
        clipCopyLabel.textContent = '复制来自虚拟机的文本';
      }
    }

    if (fullscreenIcon) fullscreenIcon.innerHTML = consoleState.isFullscreen ? '&#xE688;' : '&#xE685;';
    if (fullscreenBtn) fullscreenBtn.title = consoleState.isFullscreen ? '退出全屏 (Esc)' : '全屏显示';

    const displayRes = isEnhanced ? formatResText(session.resolution) : '1024 × 768';
    if (guestResLabel) guestResLabel.textContent = displayRes;
    if (guestSessionTag) guestSessionTag.textContent = modeNames[session.mode];

    const useViewportZoom = !isEnhanced && zoomVal !== 'fit';
    if (viewport) viewport.classList.toggle('is-actual-scale', useViewportZoom);
    if (liveCanvas) liveCanvas.hidden = !showLiveCanvas;
    if (guestDesktop) guestDesktop.hidden = showLiveCanvas;
    let crispCanvas = false;
    if (screenSurface) {
      screenSurface.classList.toggle('is-captured', connected && session.inputCaptured);
      screenSurface.style.cursor = showLiveCanvas ? (session.remotePosition ? 'none' : session.cursorStyle || 'default') : '';
      if (useViewportZoom) {
        const rw = showLiveCanvas && session.frameWidth ? session.frameWidth : (parseResolutionTuple(isEnhanced ? session.resolution : '1024x768').width);
        const rh = showLiveCanvas && session.frameHeight ? session.frameHeight : (parseResolutionTuple(isEnhanced ? session.resolution : '1024x768').height);
        const dpr = typeof window !== 'undefined' ? window.devicePixelRatio : 1;
        const scaled = computeScaledSurfaceCssDimensions(rw, rh, zoomVal, dpr);
        screenSurface.style.width = `${scaled.width}px`;
        screenSurface.style.height = `${scaled.height}px`;
        crispCanvas = scaled.crisp;
      } else {
        screenSurface.style.width = '';
        screenSurface.style.height = '';
      }
    }
    if (liveCanvas) liveCanvas.classList.toggle('is-crisp', crispCanvas);

    if (emptyState && emptySpinner && emptyOs && emptyTitle && emptyDesc && primaryActionBtn) {
      if (connected) {
        emptyState.hidden = true;
      } else {
        emptyState.hidden = false;
        const isConnecting = session.connState === 'connecting';
        emptyCard?.classList.toggle('is-minimal', isConnecting);
        if (isConnecting) {
          emptySpinner.hidden = false;
          emptyOs.hidden = true;
          emptyTitle.textContent = '正在连接...';
          emptyDesc.hidden = true;
          emptyDesc.textContent = '';
          primaryActionBtn.hidden = true;
        } else if (session.connState === 'disabled') {
          emptySpinner.hidden = true;
          emptyOs.hidden = false;
          emptyTitle.textContent = '控制台已被禁用';
          emptyDesc.hidden = false;
          emptyDesc.textContent = '可在虚拟机高级设置中重新开启';
          primaryActionBtn.hidden = true;
        } else if (session.connState === 'disconnected') {
          emptySpinner.hidden = true;
          emptyOs.hidden = false;
          emptyTitle.textContent = '连接已断开';
          emptyDesc.hidden = !session.liveError;
          emptyDesc.textContent = session.liveError || '';
          primaryActionBtn.hidden = false;
          primaryActionBtn.textContent = '重新连接';
        } else {
          emptySpinner.hidden = true;
          emptyOs.hidden = false;
          emptyTitle.textContent = '虚拟机未运行';
          emptyDesc.hidden = true;
          emptyDesc.textContent = '';
          primaryActionBtn.hidden = false;
          primaryActionBtn.textContent = '启动并连接';
        }
        if (emptyActions) emptyActions.hidden = primaryActionBtn.hidden;
      }
    }

    win.classList.toggle('is-fullscreen', consoleState.isFullscreen);
    if (fsBar) fsBar.hidden = !consoleState.isFullscreen;
    if (fsPinBtn) fsPinBtn.classList.toggle('is-pinned', consoleState.fsPinned);

    notifyDragSpacerRect();
  }

  function buildSessionPayload(session) {
    return {
      id: session.vm.id,
      name: session.vm.name,
      running: Boolean(session.vm.running),
      osIcon: session.vm.osIcon || 'Windows.svg',
      generation: session.vm.generation ?? session.vm.gen ?? 2,
      consoleSupport: session.adv.consoleSupport !== false,
      shielding: Boolean(session.adv.shielding),
      consoleClipboard: typeof session.adv.consoleClipboard === 'boolean' ? session.adv.consoleClipboard : undefined,
      consoleAudioOutput: typeof session.adv.consoleAudioOutput === 'boolean' ? session.adv.consoleAudioOutput : undefined,
      consoleAudioInput: typeof session.adv.consoleAudioInput === 'boolean' ? session.adv.consoleAudioInput : undefined,
      mode: session.mode,
      scaleMode: session.scaleMode,
      desktopScale: session.desktopScale,
      resolution: session.resolution,
      serverMode: session.liveMode ? 'live' : (session.vm.serverMode || 'demo'),
      serverUrl: session.vm.serverUrl || '',
      accentHex: getAccentHex ? getAccentHex() : '#4CC2FF'
    };
  }

  function openTab(vmPayload, advPayload) {
    syncTheme();
    consoleState.isOpen = true;
    win.hidden = false;

    const vmData = vmPayload || getVm?.() || { id: 'vm-1', name: 'Windows 11', running: true, osIcon: 'Windows.svg', generation: 2 };
    const advData = advPayload || getAdvSettings?.() || { consoleSupport: true, shielding: false };
    const id = vmData.id || `vm-${Date.now()}`;
    sessions.forEach((s) => {
      if (s.id !== id) stopSessionMicrophone(s);
    });

    let session = sessions.get(id);
    if (session) {
      const wasRunning = Boolean(session.vm.running);
      const wasLive = Boolean(session.liveMode);
      session.vm = { ...session.vm, ...vmData };
      session.adv = { ...session.adv, ...advData };
      if (vmData.serverMode === 'demo' && wasLive) {
        session.liveMode = false;
        closeSessionSocket(session);
      } else if (vmData.serverMode === 'live') {
        session.liveMode = true;
      }
      const wasDifferentTab = consoleState.activeVmId !== id;
      consoleState.activeVmId = id;
      onActiveVmChanged?.(id);
      if (wasDifferentTab && session.id === id) {
        ensureRenderer()?.reset();
        if (session.liveStreamActive && session.frameWidth > 0 && session.frameHeight > 0) {
          sendSocketJson(session, { type: 'captureFrame' });
        }
      }
      if (!wasRunning && session.vm.running && session.connState === 'stopped' && !session.ws) {
        startSessionConnecting(session, session.mode);
        return;
      }
      render();
      return;
    }

    session = {
      id,
      vm: { ...vmData },
      adv: { ...advData },
      mode: vmData.mode || 'enhanced',
      connState: 'stopped',
      scaleMode: vmData.scaleMode || 'fit',
      desktopScale: Number(vmData.desktopScale) >= 100 ? Math.min(500, Math.trunc(Number(vmData.desktopScale))) : 0,
      resolution: vmData.resolution || '1280x720',
      muted: Boolean(vmData.muted),
      inputCaptured: false,
      connectTimer: null,
      connectToken: 0,
      ws: null,
      liveMode: vmData.serverMode === 'live',
      liveStreamActive: false,
      liveError: '',
      capabilities: null,
      encodingPref: vmData.encodingPref || 'auto',
      activeEncoding: '',
      serverUrl: vmData.serverUrl || '',
      h264RuntimeFailed: false,
      h264Hardware: false,
      frameWidth: 0,
      frameHeight: 0,
      cursorStyle: 'default',
      cursorReceived: false,
      cursorToken: 0,
      micEnabled: false,
      micStarting: false,
      lastGuestClipboardText: ''
    };
    sessions.set(id, session);
    consoleState.activeVmId = id;
    onActiveVmChanged?.(id);

    const isGen1 = session.vm.generation === 1 || session.vm.gen === 1;
    const consoleOff = !isGen1 && (Boolean(session.adv.shielding) || session.adv.consoleSupport === false);
    if (!session.vm.running) {
      session.connState = 'stopped';
      render();
    } else if (consoleOff) {
      session.connState = 'disabled';
      render();
    } else {
      startSessionConnecting(session, session.mode);
    }
  }

  function open() {
    const targetVm = getVm?.();
    openTab(targetVm, getAdvSettings?.(targetVm));
  }

  function close() {
    closeAllFlyouts();
    clearPendingPointerMove();
    sessions.forEach((s) => {
      closeSessionSocket(s);
    });
    sessions.clear();
    if (renderer) {
      renderer.destroy();
      renderer = null;
    }
    consoleState.activeVmId = null;
    consoleState.isOpen = false;
    consoleState.isFullscreen = false;
    win.classList.remove('is-maximized', 'is-fullscreen');
    if (isHostedInWinOnWeb && typeof onRequestClose === 'function') {
      onRequestClose();
      return;
    }
    win.hidden = true;
  }

  // Left-top VM Switcher menu (switch in-place or pop out a separate window)
  switcherBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    const next = switcherFlyout ? switcherFlyout.hidden : false;
    closeAllFlyouts();
    if (!switcherFlyout || !switcherList) return;
    if (!next) {
      switcherFlyout.hidden = true;
      return;
    }
    const allVms = getAllVms ? getAllVms() : [];
    const openVmIds = getOpenVmIds ? getOpenVmIds() : new Set();
    switcherList.innerHTML = allVms.length
      ? allVms
          .map((item) => {
            const isActive = item.id === consoleState.activeVmId;
            const isOpenElsewhere = !isActive && openVmIds.has(item.id);
            const cached = sessions.get(item.id);
            const running = cached ? cached.vm.running : item.running;
            const osPath = item.osIcon ? `/ui-design/os/${escapeHtml(item.osIcon)}` : '/ui-design/os/Windows.svg';
            const statusText = isActive ? '当前' : isOpenElsewhere ? '已打开' : running ? '运行中' : '已关机';
            const popBtnHtml = !isActive && !isOpenElsewhere
              ? `<button type="button" class="console-switcher-pop" data-pop-vm-id="${escapeHtml(item.id)}" title="在新独立窗口中打开">
                   <span class="fi">&#xE8A7;</span>
                 </button>`
              : '';
            return `
              <div class="console-switcher-row">
                <button type="button" class="console-switcher-item ${isActive ? 'is-selected' : ''}" data-switch-vm-id="${escapeHtml(item.id)}" data-open-elsewhere="${isOpenElsewhere ? 'true' : 'false'}">
                  <img class="console-os-icon" src="${osPath}" alt="" draggable="false" />
                  <span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeHtml(item.name)}</span>
                  <span style="font-size:11px;opacity:0.6;">${statusText}</span>
                </button>
                ${popBtnHtml}
              </div>
            `;
          })
          .join('')
      : '<div style="padding:8px 10px;font-size:12px;opacity:0.65;">暂无其他可用虚拟机</div>';

    switcherList.querySelectorAll('[data-switch-vm-id]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const vid = btn.getAttribute('data-switch-vm-id');
        const isElsewhere = btn.getAttribute('data-open-elsewhere') === 'true';
        const target = allVms.find((v) => v.id === vid);
        closeAllFlyouts();
        if (!target) return;
        if (isElsewhere) {
          onFocusExistingWindow?.(target.id);
          return;
        }
        openTab(target, getAdvSettings?.(target));
      });
    });

    switcherList.querySelectorAll('[data-pop-vm-id]').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const vid = btn.getAttribute('data-pop-vm-id');
        const target = allVms.find((v) => v.id === vid);
        closeAllFlyouts();
        if (!target) return;
        if (typeof onOpenNewWindow === 'function') {
          const cached = sessions.get(target.id);
          onOpenNewWindow(cached ? buildSessionPayload(cached) : { ...target, accentHex: getAccentHex?.() || '#4CC2FF' });
        } else {
          showToast('请在 WinOnWeb 桌面模式中打开独立窗口', 'warning');
        }
      });
    });

    switcherFlyout.hidden = false;
  });

  closeBtn?.addEventListener('click', close);
  minBtn?.addEventListener('click', close);
  maxBtn?.addEventListener('click', () => {
    if (consoleState.isFullscreen || isHostedInWinOnWeb) return;
    const isMax = win.classList.toggle('is-maximized');
    if (maxIcon) maxIcon.innerHTML = isMax ? '&#xE923;' : '&#xE922;';
  });

  function toggleFlyoutMenu(flyout, e) {
    e?.stopPropagation();
    const open = flyout ? flyout.hidden : false;
    closeAllFlyouts();
    if (flyout) flyout.hidden = !open;
  }

  resBtn?.addEventListener('click', (e) => {
    if (resBtn.disabled) return;
    toggleFlyoutMenu(resFlyout, e);
  });
  resFlyout?.querySelectorAll('[data-console-res]').forEach((item) => {
    item.addEventListener('click', () => {
      const nextRes = item.getAttribute('data-console-res');
      closeAllFlyouts();
      const session = activeSession();
      if (!session || !nextRes) return;
      session.resolution = nextRes;
      if (session.liveStreamActive) {
        const { width, height } = parseResolutionTuple(nextRes);
        sendSocketJson(session, { type: 'resize', width, height, desktopScale: effectiveSessionScale(session, width) });
      }
      render();
      showToast(`已切换分辨率为 ${formatResText(nextRes)}`);
    });
  });

  scaleBtn?.addEventListener('click', (e) => toggleFlyoutMenu(scaleFlyout, e));
  scaleFlyout?.querySelectorAll('[data-console-zoom]').forEach((item) => {
    item.addEventListener('click', () => {
      const nextZoom = item.getAttribute('data-console-zoom');
      closeAllFlyouts();
      const session = activeSession();
      if (!session || !nextZoom) return;
      if (session.mode === 'enhanced') {
        session.desktopScale = nextZoom === 'fit' ? 0 : (Number.parseInt(nextZoom, 10) || 100);
        if (session.liveStreamActive) {
          const { width, height } = session.frameWidth > 0 && session.frameHeight > 0
            ? { width: session.frameWidth, height: session.frameHeight }
            : parseResolutionTuple(session.resolution);
          sendSocketJson(session, {
            type: 'resize',
            width,
            height,
            desktopScale: effectiveSessionScale(session, width)
          });
        }
        render();
        return;
      }
      session.scaleMode = nextZoom;
      render();
    });
  });

  function applySessionMode(nextMode) {
    const session = activeSession();
    if (!session || session.mode === nextMode) return;
    closeAllFlyouts();
    if (session.vm.running) {
      startSessionConnecting(session, nextMode);
      showToast('正在切换到' + modeNames[nextMode]);
    } else {
      session.mode = nextMode;
      render();
    }
  }

  takeControl?.addEventListener('click', () => sendSocketJson(activeSession(), { type: 'takeControl' }));
  displayModes?.addEventListener('change', () => sendSocketJson(activeSession(), { type: 'setMode', id: displayModes.value }));
  fallbackEnhanced?.addEventListener('click', () => {
    const session = activeSession();
    if (session) session.ceiling = session.rights ?? null;
    applySessionMode('enhanced');
  });
  document.getElementById('consoleWatchOnly')?.addEventListener('click', () => {
    const session = activeSession();
    if (!session) return;
    closeAllFlyouts();
    session.watchOnly = !session.watchOnly;
    startSessionConnecting(session, session.mode);
  });
  modeBtn?.addEventListener('click', (e) => {
    if (modeBtn.disabled) return;
    toggleFlyoutMenu(modeFlyout, e);
  });
  modeFlyout?.querySelectorAll('[data-console-session]').forEach((item) => {
    item.addEventListener('click', () => {
      const targetMode = item.getAttribute('data-console-session');
      closeAllFlyouts();
      if (targetMode) { const session = activeSession(); if (session) session.ceiling = null; applySessionMode(targetMode); }
    });
  });
  fsModeBtn?.addEventListener('click', () => {
    const session = activeSession();
    if (!session || fsModeBtn.disabled) return;
    const allowExternal = showExtensionEntriesEnabled() || session.mode === 'external';
    const next = allowExternal
      ? { basic: 'enhanced', enhanced: 'external', external: 'basic' }
      : { basic: 'enhanced', enhanced: 'basic', external: 'basic' };
    applySessionMode(next[session.mode]);
  });

  qualityBtn?.addEventListener('click', (e) => {
    if (qualityBtn.disabled) return;
    toggleFlyoutMenu(qualityFlyout, e);
  });
  qualityFlyout?.querySelectorAll('[data-console-quality]').forEach((item) => {
    item.addEventListener('click', () => {
      const pref = item.getAttribute('data-console-quality');
      closeAllFlyouts();
      const session = activeSession();
      if (!session || !pref) return;
      session.encodingPref = pref;
      session.h264RuntimeFailed = false;
      if (session.ws && session.ws.readyState === WebSocket.OPEN) {
        const hasModernCaps = Array.isArray(session.capabilities?.supportedEncodings);
        const target = selectNegotiatedConsoleEncoding({
          serverUrl: session.serverUrl,
          preference: pref,
          capabilities: session.capabilities,
          hasWebCodecs: webCodecsH264Supported,
          hasDecompressionStream: typeof DecompressionStream !== 'undefined'
        });
        if (hasModernCaps) {
          if (session.capabilities.supportedEncodings.includes(target)) {
            if (target !== session.activeEncoding && sendSocketJson(session, { type: 'setEncoding', encoding: target })) {
              session.activeEncoding = target;
            }
          } else {
            showToast('宿主机服务端不支持所选编码，保持当前传输格式', 'warning');
          }
        } else if (target !== session.activeEncoding) {
          startSessionConnecting(session, session.mode);
        }
      }
      render();
    });
  });

  async function handlePowerAction(session, act) {
    if (!session || !act) return;
    if (act === 'connect-only') {
      startSessionConnecting(session, session.mode);
      return;
    }
    if (act === 'start') {
      if (session.vm.running) {
        startSessionConnecting(session, session.mode);
        return;
      }
      closeSessionSocket(session);
      session.connState = 'connecting';
      session.liveError = '';
      render();
      if (typeof executeVmPowerAction === 'function') {
        try {
          await executeVmPowerAction(session.vm.id, 'start');
        } catch (err) {
          session.connState = 'stopped';
          session.liveError = err?.message || '启动虚拟机失败';
          render();
          showToast(session.liveError, 'error');
          return;
        }
      }
      session.vm.running = true;
      onVmPowerChanged?.({ ...session.vm, action: 'start' });
      startSessionConnecting(session, session.mode);
      return;
    }

    if (act === 'restart') {
      closeSessionSocket(session);
      session.connState = 'connecting';
      session.liveError = '';
      render();
      showToast('正在重启虚拟机');
      if (typeof executeVmPowerAction === 'function') {
        try {
          await executeVmPowerAction(session.vm.id, 'restart');
        } catch (err) {
          session.connState = session.vm.running ? 'disconnected' : 'stopped';
          session.liveError = err?.message || '重启虚拟机失败';
          render();
          showToast(session.liveError, 'error');
          return;
        }
      }
      startSessionConnecting(session, session.mode);
      return;
    }

    const toastMap = {
      shutdown: '已发送关机指令',
      save: '正在保存虚拟机状态',
      pause: '已暂停虚拟机',
      turnoff: '已关闭虚拟机电源'
    };
    closeSessionSocket(session);
    showToast(toastMap[act] || '正在执行电源操作');
    if (typeof executeVmPowerAction === 'function') {
      try {
        const handled = await executeVmPowerAction(session.vm.id, act);
        if (handled !== true) {
          session.vm.running = false;
          onVmPowerChanged?.(session.vm);
        }
      } catch (err) {
        showToast(err?.message || '电源操作失败', 'error');
        return;
      }
    }
    session.vm.running = false;
    session.connState = 'stopped';
    session.inputCaptured = false;
    render();
  }

  function startActiveVm() {
    const session = activeSession();
    if (!session) return;
    if (session.vm.running && session.connState === 'disconnected') {
      void handlePowerAction(session, 'connect-only');
      return;
    }
    void handlePowerAction(session, 'start');
  }

  powerMainBtn?.addEventListener('click', () => {
    const session = activeSession();
    if (!session) return;
    closeAllFlyouts();
    if (!session.vm.running) {
      startActiveVm();
      return;
    }
    const isSavePrimary = String(getDefaultPowerAction?.() || session?.vm?.defaultPowerAction || 'Shutdown').toLowerCase() === 'save';
    void handlePowerAction(session, isSavePrimary ? 'save' : 'shutdown');
  });

  powerDropBtn?.addEventListener('click', (e) => {
    if (powerDropBtn.disabled) return;
    toggleFlyoutMenu(powerFlyout, e);
  });

  powerFlyout?.querySelectorAll('[data-console-power]').forEach((item) => {
    item.addEventListener('click', () => {
      const act = item.getAttribute('data-console-power');
      closeAllFlyouts();
      const session = activeSession();
      if (!session || !act) return;
      void handlePowerAction(session, act);
    });
  });

  primaryActionBtn?.addEventListener('click', startActiveVm);

  function sendCad() {
    closeAllFlyouts();
    const session = activeSession();
    if (session?.liveStreamActive) {
      sendSocketJson(session, { type: 'keys', events: CTRL_ALT_DEL_SEQUENCE });
    }
    showToast('已发送 Ctrl + Alt + Del');
  }
  cadBtn?.addEventListener('click', sendCad);
  fsCadBtn?.addEventListener('click', sendCad);

  function takeScreenshot() {
    const session = activeSession();
    if (!session || session.connState !== 'connected') return;
    closeAllFlyouts();
    if (session.liveStreamActive && liveCanvas && liveCanvas.width > 0 && liveCanvas.height > 0) {
      liveCanvas.toBlob((blob) => {
        if (!blob) return;
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        const stamp = new Date().toISOString().replace(/[:.]/g, '-');
        link.href = url;
        link.download = `${session.vm.name || session.vm.id}-${stamp}.png`;
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1500);
      }, 'image/png');
      showToast('已导出当前控制台截图');
      return;
    }
    showToast('已截取当前控制台画面');
  }
  shotBtn?.addEventListener('click', takeScreenshot);

  audioBtn?.addEventListener('click', () => {
    const session = activeSession();
    if (!session) return;
    closeAllFlyouts();
    session.muted = !session.muted;
    render();
    showToast(session.muted ? '控制台音频已静音' : '控制台音频已开启');
  });

  micBtn?.addEventListener('click', () => {
    const session = activeSession();
    if (!session || micBtn.disabled) return;
    closeAllFlyouts();
    if (session.micEnabled || session.micStarting) {
      stopSessionMicrophone(session);
      render();
      showToast('麦克风已静音');
    } else {
      void startSessionMicrophone(session);
    }
  });

  function sendTextToGuestClipboard(session, text) {
    if (!session || session.connState !== 'connected' || session.mode !== 'enhanced') {
      showToast('仅增强会话连接状态下可同步剪贴板', 'warning');
      return false;
    }
    const payload = encodeClipboardTextMessage(text);
    const sent = sendSocketJson(session, payload);
    if (!sent) {
      showToast('发送剪贴板文本失败，请确认增强会话已连接', 'error');
      return false;
    }
    return true;
  }

  clipBtn?.addEventListener('click', (e) => {
    if (clipBtn.disabled) return;
    toggleFlyoutMenu(clipFlyout, e);
  });

  clipSendBtn?.addEventListener('click', async () => {
    const session = activeSession();
    if (!session || clipSendBtn.disabled) return;
    if (!navigator.clipboard?.readText) {
      showToast('当前环境不支持直接读取本机剪贴板，请在下方输入框粘贴后发送', 'warning');
      clipInput?.focus();
      return;
    }
    try {
      const text = await navigator.clipboard.readText();
      if (!text) {
        showToast('本机剪贴板暂无纯文本，可在下方输入框直接输入后发送', 'warning');
        clipInput?.focus();
        return;
      }
      if (sendTextToGuestClipboard(session, text)) {
        closeAllFlyouts();
        showToast('已发送本机剪贴板文本到虚拟机');
      }
    } catch {
      showToast('未能自动读取本机剪贴板权限，请在下方输入框按 Ctrl + V 后发送', 'warning');
      clipInput?.focus();
    }
  });

  clipCopyBtn?.addEventListener('click', async () => {
    const session = activeSession();
    const text = session?.lastGuestClipboardText || '';
    if (!text) return;
    if (!navigator.clipboard?.writeText) {
      if (clipInput) {
        clipInput.value = text;
        clipInput.focus();
        clipInput.select();
      }
      showToast('请按 Ctrl + C 复制输入框中的虚拟机文本', 'warning');
      return;
    }
    try {
      await navigator.clipboard.writeText(text);
      closeAllFlyouts();
      showToast('已复制虚拟机剪贴板文本到本机');
    } catch {
      if (clipInput) {
        clipInput.value = text;
        clipInput.focus();
        clipInput.select();
      }
      showToast('浏览器阻止了写入剪贴板，请按 Ctrl + C 复制输入框文本', 'warning');
    }
  });

  clipManualSendBtn?.addEventListener('click', () => {
    const session = activeSession();
    if (!session || clipManualSendBtn.disabled) return;
    const text = clipInput?.value ?? '';
    if (!text) {
      showToast('请输入或粘贴要发送的文本', 'warning');
      clipInput?.focus();
      return;
    }
    if (sendTextToGuestClipboard(session, text)) {
      if (clipInput) clipInput.value = '';
      closeAllFlyouts();
      showToast('已发送文本到虚拟机剪贴板');
    }
  });

  clipInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      clipManualSendBtn?.click();
    }
  });

  function setFullscreen(flag) {
    consoleState.isFullscreen = Boolean(flag);
    closeAllFlyouts();
    render();
  }
  fullscreenBtn?.addEventListener('click', () => setFullscreen(true));
  fsExitBtn?.addEventListener('click', () => setFullscreen(false));
  fsPinBtn?.addEventListener('click', () => {
    consoleState.fsPinned = !consoleState.fsPinned;
    render();
  });

  win.addEventListener('mousemove', (e) => {
    if (!consoleState.isFullscreen || consoleState.fsPinned || !fsBar) return;
    const rect = win.getBoundingClientRect();
    if (e.clientY - rect.top <= (fsBar.hidden ? 8 : 44)) {
      fsBar.hidden = false;
      if (fsHideTimer) clearTimeout(fsHideTimer);
    } else if (!fsBar.hidden) {
      if (fsHideTimer) clearTimeout(fsHideTimer);
      fsHideTimer = setTimeout(() => {
        if (consoleState.isFullscreen && !consoleState.fsPinned) {
          fsBar.hidden = true;
        }
      }, 1200);
    }
  });

  function mapPointerCoords(session, clientX, clientY) {
    const el = liveCanvas && !liveCanvas.hidden ? liveCanvas : screenSurface;
    if (!el) return null;
    if (isSessionLive(session) && (!session.frameWidth || !session.frameHeight)) return null;
    const rect = el.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return null;
    const guestW = session.frameWidth || liveCanvas?.width || 1280;
    const guestH = session.frameHeight || liveCanvas?.height || 720;
    if (guestW <= 0 || guestH <= 0) return null;
    const scale = Math.min(rect.width / guestW, rect.height / guestH);
    const drawnW = guestW * scale;
    const drawnH = guestH * scale;
    const offsetX = rect.left + (rect.width - drawnW) / 2;
    const offsetY = rect.top + (rect.height - drawnH) / 2;
    const normX = Math.max(0, Math.min(1, (clientX - offsetX) / drawnW));
    const normY = Math.max(0, Math.min(1, (clientY - offsetY) / drawnH));
    return {
      x: Math.max(0, Math.min(guestW - 1, Math.round(normX * Math.max(1, guestW - 1)))),
      y: Math.max(0, Math.min(guestH - 1, Math.round(normY * Math.max(1, guestH - 1))))
    };
  }

  function mapPointerButton(buttonIdx) {
    if (buttonIdx === 0) return 'Left';
    if (buttonIdx === 2) return 'Right';
    if (buttonIdx === 1) return 'Middle';
    return 'None';
  }

  screenSurface?.addEventListener('click', () => {
    const session = activeSession();
    if (!session || session.connState !== 'connected') return;
    if (session.control?.hasControl === false) return;
    session.inputCaptured = true;
    screenSurface.focus({ preventScroll: true });
    render();
  });

  screenSurface?.addEventListener('mousedown', (e) => {
    const session = activeSession();
    if (!session || session.connState !== 'connected') return;
    clearPendingPointerMove();
    const button = mapPointerButton(e.button);
    session.inputCaptured = true;
    screenSurface.focus({ preventScroll: true });
    if (button === 'None') return;
    if (session.liveStreamActive) {
      const coords = mapPointerCoords(session, e.clientX, e.clientY);
      if (coords) {
        sendSocketJson(session, {
          type: 'pointer',
          x: coords.x,
          y: coords.y,
          button,
          down: true,
          wheel: 0
        });
      }
      if (e.button === 2) e.preventDefault();
    }
  });

  screenSurface?.addEventListener('mouseup', (e) => {
    const session = activeSession();
    if (!session || session.connState !== 'connected' || !session.liveStreamActive) return;
    clearPendingPointerMove();
    const button = mapPointerButton(e.button);
    if (button === 'None') return;
    const coords = mapPointerCoords(session, e.clientX, e.clientY);
    if (coords) {
      sendSocketJson(session, {
        type: 'pointer',
        x: coords.x,
        y: coords.y,
        button,
        down: false,
        wheel: 0
      });
    }
  });

  screenSurface?.addEventListener('mousemove', (e) => {
    const session = activeSession();
    if (!session || session.connState !== 'connected' || !session.liveStreamActive || !session.inputCaptured) return;
    pendingPointerMove = { session, clientX: e.clientX, clientY: e.clientY };
    if (!pointerMoveRafId) {
      pointerMoveRafId = typeof requestAnimationFrame === 'function'
        ? requestAnimationFrame(flushPendingPointerMove)
        : setTimeout(flushPendingPointerMove, 16);
    }
  });

  screenSurface?.addEventListener('contextmenu', (e) => {
    const session = activeSession();
    if (session?.liveStreamActive && session.connState === 'connected') {
      e.preventDefault();
    }
  });

  screenSurface?.addEventListener(
    'wheel',
    (e) => {
      const session = activeSession();
      if (!session || session.connState !== 'connected' || !session.liveStreamActive) return;
      clearPendingPointerMove();
      e.preventDefault();
      if (e.deltaY === 0 || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
      const coords = mapPointerCoords(session, e.clientX, e.clientY);
      if (!coords) return;
      const wheelDelta = Math.max(-120, Math.min(120, Math.round((e.deltaY < 0 ? 1 : -1) * 120)));
      sendSocketJson(session, {
        type: 'pointer',
        x: coords.x,
        y: coords.y,
        button: 'None',
        down: false,
        wheel: wheelDelta
      });
    },
    { passive: false }
  );

  function isEditableKeyboardTarget(target) {
    return Boolean(
      target &&
      (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)
    );
  }

  document.addEventListener('keydown', (e) => {
    if (!consoleState.isOpen) return;
    if (isEditableKeyboardTarget(e.target)) {
      if (e.key === 'Escape') closeAllFlyouts();
      return;
    }
    const session = activeSession();
    if (e.key === 'Escape') {
      if (session && session.inputCaptured) {
        clearPendingPointerMove();
        if (session.liveStreamActive && session.connState === 'connected') {
          sendSocketJson(session, { type: 'releaseAllInputs' });
        }
        session.inputCaptured = false;
        render();
        return;
      }
      if (consoleState.isFullscreen) {
        setFullscreen(false);
        return;
      }
    }
    if (session && session.connState === 'connected' && session.inputCaptured && session.liveStreamActive) {
      if (e.code === 'Pause') {
        e.preventDefault();
        if (!e.repeat) {
          sendSocketJson(session, { type: 'pause' });
        }
        return;
      }
      const scan = mapKeyEventToScanCode(e);
      if (scan) {
        e.preventDefault();
        sendSocketJson(session, {
          type: 'key',
          scanCode: scan.scanCode,
          extended: scan.extended,
          down: true
        });
      }
    }
  });

  document.addEventListener('keyup', (e) => {
    if (!consoleState.isOpen || isEditableKeyboardTarget(e.target)) return;
    const session = activeSession();
    if (session && session.connState === 'connected' && session.inputCaptured && session.liveStreamActive) {
      if (e.code === 'Pause') {
        e.preventDefault();
        return;
      }
      const scan = mapKeyEventToScanCode(e);
      if (scan) {
        e.preventDefault();
        sendSocketJson(session, {
          type: 'key',
          scanCode: scan.scanCode,
          extended: scan.extended,
          down: false
        });
      }
    }
  });

  window.addEventListener('blur', () => {
    const session = activeSession();
    if (!consoleState.isOpen || !session || !session.inputCaptured) return;
    clearPendingPointerMove();
    if (session.liveStreamActive && session.connState === 'connected') {
      sendSocketJson(session, { type: 'releaseAllInputs' });
    }
    session.inputCaptured = false;
    render();
  });

  win.addEventListener('click', (e) => {
    if (!e.target.closest('.console-anchor')) {
      closeAllFlyouts();
    }
  });

  // Fallback window dragging (when opened outside WinOnWeb desktop)
  if (!isHostedInWinOnWeb) {
    let dragging = false;
    let dragStartX = 0;
    let dragStartY = 0;
    let startLeft = 0;
    let startTop = 0;

    titleBar?.addEventListener('mousedown', (e) => {
      if (
        e.target.closest('.caption-buttons') ||
        e.target.closest('.console-anchor') ||
        e.target.closest('.console-icon-btn') ||
        e.target.closest('.console-hdr-btn') ||
        e.target.closest('.console-split-group') ||
        win.classList.contains('is-maximized') ||
        consoleState.isFullscreen
      ) {
        return;
      }
      dragging = true;
      const rect = win.getBoundingClientRect();
      win.style.left = `${rect.left}px`;
      win.style.top = `${rect.top}px`;
      win.style.transform = 'none';
      dragStartX = e.clientX;
      dragStartY = e.clientY;
      startLeft = rect.left;
      startTop = rect.top;
    });

    window.addEventListener('mousemove', (e) => {
      if (!dragging) return;
      const nextLeft = Math.max(8, Math.min(window.innerWidth - 180, startLeft + (e.clientX - dragStartX)));
      const nextTop = Math.max(8, Math.min(window.innerHeight - 80, startTop + (e.clientY - dragStartY)));
      win.style.left = `${nextLeft}px`;
      win.style.top = `${nextTop}px`;
    });

    window.addEventListener('mouseup', () => {
      dragging = false;
    });
  }

  return {
    open,
    openTab,
    close,
    syncTheme,
    syncVmState: () => {
      if (consoleState.isOpen) render();
    }
  };
}
