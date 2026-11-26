/**
 * Binary protocol codec, fast Canvas/WebCodecs renderer, and input/display helpers
 * for ExHyperV.Server /ws/v1/console/{vmId}.
 */

export const CONSOLE_WIRE_MAGIC_0 = 0x45; // 'E'
export const CONSOLE_WIRE_MAGIC_1 = 0x58; // 'X'

/**
 * Decodes ConsoleWire.Video (type=1, 34-byte header), ConsoleWire.Audio (type=2, 18-byte header),
 * or ConsoleWire.H264 (type=3, 34-byte header).
 */
export function parseConsoleBinaryPacket(buffer, version = 1) {
  const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
  if (bytes.byteLength < 18 || bytes[0] !== CONSOLE_WIRE_MAGIC_0 || bytes[1] !== CONSOLE_WIRE_MAGIC_1) {
    return null;
  }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const packetType = bytes[2];
  const flags = bytes[3];
  const format = bytes[4];
  const channels = bytes[5];

  if (packetType === 4 && version === 2) {
    if (bytes.length < 40 || bytes.length > 65 * 1024 * 1024 || flags > 1 || format !== 0 || channels !== 2
      || view.getUint32(6, true) !== bytes.length || view.getUint16(38, true) !== 0) return null;
    const u16 = (offset) => view.getUint16(offset, true);
    const width = u16(26), height = u16(28), dirtyCount = u16(30), moveCount = u16(32);
    const header = 40 + dirtyCount * 8 + moveCount * 12;
    if (!width || !height || width * height * 4 > 64 * 1024 * 1024 || dirtyCount + moveCount > 4096
      || view.getUint32(34, true) !== header || header > bytes.length) return null;
    const rect = (i) => ({ x: u16(i), y: u16(i + 2), width: u16(i + 4), height: u16(i + 6) });
    const valid = (r) => r.width && r.height && r.x + r.width <= width && r.y + r.height <= height;
    const dirty = [], moves = [];
    let offset = 40, payload = header;
    for (let i = 0; i < dirtyCount; i++, offset += 8) {
      const r = rect(offset);
      const end = payload + r.width * r.height * 4;
      if (!valid(r) || end > bytes.length) return null;
      dirty.push({ ...r, pixels: bytes.subarray(payload, end) });
      payload = end;
    }
    for (let i = 0; i < moveCount; i++, offset += 12) {
      const r = rect(offset + 4), sx = u16(offset), sy = u16(offset + 2);
      if (!valid(r) || sx + r.width > width || sy + r.height > height) return null;
      moves.push({ ...r, sx, sy });
    }
    if (payload !== bytes.length || flags && (moveCount || dirtyCount !== 1 || dirty[0].width !== width || dirty[0].height !== height)) return null;
    const sequence = Number(view.getBigInt64(10, true)), baseSequence = Number(view.getBigInt64(18, true));
    if (!Number.isSafeInteger(sequence) || !Number.isSafeInteger(baseSequence)) return null;
    return { kind: 'video', encoding: 'regions', width, height, fullFrame: Boolean(flags), sequence, baseSequence, dirty, moves };
  }
  if (packetType === 1) {
    if (bytes.byteLength < 34) return null;
    const sequence = Number(view.getBigInt64(6, true));
    const baseSequence = Number(view.getBigInt64(14, true));
    const width = view.getUint16(22, true);
    const height = view.getUint16(24, true);
    if (format > 2 || version === 2 && (flags > 1 || channels || !width || !height || width * height * 4 > 64 * 1024 * 1024)) return null;
    const rectX = view.getUint16(26, true);
    const rectY = view.getUint16(28, true);
    const rectWidth = view.getUint16(30, true);
    const rectHeight = view.getUint16(32, true);
    if (version === 2 && (!rectWidth || !rectHeight || rectX + rectWidth > width || rectY + rectHeight > height || (!format && bytes.length !== 34 + rectWidth * rectHeight * 4))) return null;
    const payload = bytes.subarray(34);
    return {
      kind: 'video',
      fullFrame: (flags & 1) !== 0,
      encoding: format === 0 ? 'bgra' : format === 1 ? 'png' : 'deflate',
      sequence,
      baseSequence,
      width,
      height,
      rect: { x: rectX, y: rectY, width: rectWidth, height: rectHeight },
      payload
    };
  }

  if (packetType === 2) {
    const sampleRate = view.getInt32(6, true);
    const timestampTicks = Number(view.getBigInt64(10, true));
    const samples = bytes.subarray(18);
    if (![1, 2].includes(format) || version === 2 && (flags || !channels || channels > 8 || !samples.length || samples.length % (channels * (format === 1 ? 2 : 4)))) return null;
    return {
      kind: 'audio',
      sampleFormat: format === 1 ? 's16le' : 'f32le',
      channels,
      sampleRate,
      timestampTicks,
      samples
    };
  }

  if (packetType === 3) {
    if (bytes.byteLength < 34) return null;
    const sequence = Number(view.getBigInt64(6, true));
    const timestampUs = Number(view.getBigInt64(14, true));
    const width = view.getUint16(22, true);
    const height = view.getUint16(24, true);
    if (version === 2 && (flags > 3 || format || channels || view.getUint32(30, true) || !width || !height || bytes.length === 34)) return null;
    const durationUs = view.getUint32(26, true);
    const payload = bytes.subarray(34);
    return {
      kind: 'h264',
      isKeyFrame: (flags & 1) !== 0,
      isHardware: (flags & 2) !== 0,
      codec: format === 0 ? 'h264-annexb' : 'h264',
      sequence,
      timestampUs,
      durationUs,
      width,
      height,
      payload
    };
  }

  return null;
}

export function isLoopbackHost(serverUrl) {
  if (!serverUrl) return false;
  try {
    const parsed = new URL(String(serverUrl));
    const host = parsed.hostname.replace(/^\[|\]$/g, '').toLowerCase();
    return host === 'localhost' || host === '127.0.0.1' || host === '::1' || /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(host);
  } catch {
    return false;
  }
}

export function selectInitialConsoleEncoding({
  serverUrl,
  preference = 'auto',
  serverSupportedEncodings = null,
  hasWebCodecs = typeof VideoDecoder !== 'undefined' && typeof EncodedVideoChunk !== 'undefined',
  hasDecompressionStream = typeof DecompressionStream !== 'undefined'
} = {}) {
  const known = Array.isArray(serverSupportedEncodings) ? serverSupportedEncodings : null;
  if (preference === 'bgra') return 'bgra';
  if (preference === 'png') return 'png';
  if (preference === 'deflate') return hasDecompressionStream ? 'deflate' : 'png';
  if (preference === 'h264') {
    if (known && known.includes('h264') && hasWebCodecs) return 'h264';
    return hasDecompressionStream ? 'deflate' : 'png';
  }
  if (isLoopbackHost(serverUrl)) {
    return 'bgra';
  }
  if (known && known.includes('h264') && hasWebCodecs) {
    return 'h264';
  }
  return hasDecompressionStream ? 'deflate' : 'png';
}

export function selectNegotiatedConsoleEncoding({
  serverUrl,
  preference = 'auto',
  capabilities = null,
  hasWebCodecs = typeof VideoDecoder !== 'undefined' && typeof EncodedVideoChunk !== 'undefined',
  hasDecompressionStream = typeof DecompressionStream !== 'undefined'
} = {}) {
  const supported = Array.isArray(capabilities?.supportedEncodings)
    ? capabilities.supportedEncodings
    : ['bgra', 'deflate', 'png'];

  if (preference === 'bgra' && supported.includes('bgra')) return 'bgra';
  if (preference === 'png') return 'png';
  if (preference === 'deflate') {
    return hasDecompressionStream && supported.includes('deflate') ? 'deflate' : 'png';
  }
  if (preference === 'h264') {
    if (supported.includes('h264') && hasWebCodecs) return 'h264';
    return hasDecompressionStream && supported.includes('deflate') ? 'deflate' : 'png';
  }
  if (isLoopbackHost(serverUrl) && supported.includes('bgra')) {
    return 'bgra';
  }
  if (supported.includes('h264') && hasWebCodecs) {
    return 'h264';
  }
  if (supported.includes('deflate') && hasDecompressionStream) {
    return 'deflate';
  }
  return 'png';
}

export function computeScaledSurfaceCssDimensions(guestWidth, guestHeight, zoomVal, devicePixelRatio = 1) {
  const w = Math.max(1, Math.round(Number(guestWidth) || 1));
  const h = Math.max(1, Math.round(Number(guestHeight) || 1));
  const dprRaw = Number(devicePixelRatio);
  const dpr = Number.isFinite(dprRaw) && dprRaw > 0 ? dprRaw : 1;
  if (zoomVal === 'physical') {
    return {
      width: Math.max(1, Math.round(w / dpr)),
      height: Math.max(1, Math.round(h / dpr)),
      crisp: true
    };
  }
  const pct = Number.parseFloat(zoomVal) || 100;
  return {
    width: Math.max(1, Math.round(w * (pct / 100))),
    height: Math.max(1, Math.round(h * (pct / 100))),
    crisp: pct % 100 === 0 && Number.isInteger(dpr)
  };
}

export function extractH264CodecFromAnnexB(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.byteLength < 8) return null;
  const len = bytes.byteLength;
  for (let i = 0; i <= len - 4; i++) {
    let scLen = 0;
    if (bytes[i] === 0 && bytes[i + 1] === 0) {
      if (bytes[i + 2] === 1) scLen = 3;
      else if (i + 3 < len && bytes[i + 2] === 0 && bytes[i + 3] === 1) scLen = 4;
    }
    if (!scLen) continue;
    const naluStart = i + scLen;
    if (naluStart + 3 >= len) break;
    if ((bytes[naluStart] & 0x1f) === 7) {
      const profile = bytes[naluStart + 1].toString(16).padStart(2, '0');
      const constraints = bytes[naluStart + 2].toString(16).padStart(2, '0');
      const level = bytes[naluStart + 3].toString(16).padStart(2, '0');
      return `avc1.${profile}${constraints}${level}`;
    }
    i = naluStart;
  }
  return null;
}

export async function isH264WebCodecsSupported() {
  if (typeof VideoDecoder === 'undefined' || typeof EncodedVideoChunk === 'undefined') return false;
  if (typeof VideoDecoder.isConfigSupported !== 'function') return true;
  for (const codec of ['avc1.4D401F', 'avc1.42E01E']) {
    try {
      const check = await VideoDecoder.isConfigSupported({ codec, optimizeForLatency: true });
      if (check?.supported) return true;
    } catch {}
  }
  return false;
}

async function decodeDeflateRaw(payload, length) {
  if (typeof DecompressionStream === 'undefined') {
    throw new Error('当前浏览器不支持 DecompressionStream 解码，请使用 PNG 帧编码');
  }
  const ds = new DecompressionStream('deflate-raw');
  const stream = new Blob([payload]).stream().pipeThrough(ds);
  if (!Number.isSafeInteger(length) || length <= 0 || length > 64 * 1024 * 1024) throw new Error('画面大小无效');
  const result = new Uint8Array(length);
  const reader = stream.getReader();
  let offset = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      if (value.length > length - offset) throw new Error('画面数据超出声明长度');
      result.set(value, offset);
      offset += value.length;
    }
    if (offset !== length) throw new Error('画面数据不完整');
    return result;
  } finally { await reader.cancel(); reader.releaseLock(); }
}

/**
 * Fast 32-bit BGRA -> RGBA swap with alpha forced to 0xFF.
 * Handles unaligned source views (such as WebSocket binary payload at offset 34).
 */
export function swapBgraToRgba32(bgraBytes, dstRgba32, pixelCount, dstBytes = null) {
  const count = Math.min(pixelCount, Math.floor(bgraBytes.byteLength / 4), dstRgba32.length);
  if (count <= 0) return;

  let src32;
  if ((bgraBytes.byteOffset & 3) === 0) {
    src32 = new Uint32Array(bgraBytes.buffer, bgraBytes.byteOffset, count);
  } else {
    const targetBytes = dstBytes || new Uint8Array(dstRgba32.buffer, dstRgba32.byteOffset, count * 4);
    targetBytes.set(bgraBytes.subarray(0, count * 4));
    src32 = dstRgba32;
  }

  for (let i = 0; i < count; i++) {
    const px = src32[i];
    dstRgba32[i] = (px & 0xFF00FF00) | ((px & 0xFF) << 16) | ((px >>> 16) & 0xFF) | 0xFF000000;
  }
}

export function bgraToRgbaImageData(bgraBytes, width, height) {
  const pixelCount = width * height;
  const rgba = new Uint8ClampedArray(pixelCount * 4);
  const rgba32 = new Uint32Array(rgba.buffer);
  const rgba8 = new Uint8Array(rgba.buffer);
  swapBgraToRgba32(bgraBytes, rgba32, pixelCount, rgba8);
  return new ImageData(rgba, width, height);
}

function rectContains(outer, inner) {
  return (
    outer.x <= inner.x &&
    outer.y <= inner.y &&
    outer.x + outer.width >= inner.x + inner.width &&
    outer.y + outer.height >= inner.y + inner.height
  );
}

function disposeDrawItem(item) {
  if (item?.bitmap && typeof item.bitmap.close === 'function') {
    try {
      item.bitmap.close();
    } catch {}
  }
}

export function createConsoleRenderer(canvas, options = {}) {
  const onCodecError = typeof options?.onCodecError === 'function' ? options.onCodecError : null;
  const onNeedKeyFrame = typeof options?.onNeedKeyFrame === 'function' ? options.onNeedKeyFrame : null;
  const scheduleFrame =
    typeof options?.scheduleFrame === 'function'
      ? options.scheduleFrame
      : typeof requestAnimationFrame === 'function'
        ? (cb) => requestAnimationFrame(cb)
        : (cb) => setTimeout(cb, 0);
  const cancelFrame =
    typeof options?.cancelFrame === 'function'
      ? options.cancelFrame
      : typeof cancelAnimationFrame === 'function'
        ? (id) => cancelAnimationFrame(id)
        : (id) => clearTimeout(id);

  let ctx = null;
  let lastWidth = 0;
  let lastHeight = 0;
  let audioCtx = null;
  let nextAudioTime = 0;
  let destroyed = false;
  let displayed = null;
  function painted(sequence) { displayed = sequence; options.onDisplayed?.(sequence); }

  // Pre-allocated ImageData + scratch Uint32Array buffers for zero-churn BGRA/deflate painting.
  let fullImageData = null;
  let fullImageDataBytes = null;
  let fullImageData32 = null;
  let scratchBuffer = null;
  let scratchBytes = null;
  let scratch32 = null;

  // Non-blocking decode & coalesced render queues.
  let nextOrderId = 0;
  let minValidOrderId = 0;
  const decodeQueue = [];
  let decodeRunning = false;
  const pendingDraws = [];
  let pendingH264Frame = null;
  let rafId = 0;

  // WebCodecs H.264 decoder state.
  let h264Decoder = null;
  let h264Configured = false;
  let h264WaitingKeyFrame = false;
  let h264Width = 0;
  let h264Height = 0;
  let h264DroppedDeltas = 0;
  const h264OrderQueue = [];

  function ensureCanvasSize(width, height) {
    if (!canvas) return null;
    if (!ctx) ctx = canvas.getContext('2d', { alpha: false });
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
      lastWidth = width;
      lastHeight = height;
      fullImageData = null;
      fullImageDataBytes = null;
      fullImageData32 = null;
    }
    return ctx;
  }

  function convertToReusableImageData(rawBgra, width, height) {
    const pixelCount = width * height;
    const byteLen = pixelCount * 4;
    if (typeof ImageData === 'undefined') {
      const rgba = new Uint8ClampedArray(byteLen);
      swapBgraToRgba32(rawBgra, new Uint32Array(rgba.buffer), pixelCount, new Uint8Array(rgba.buffer));
      return { width, height, data: rgba };
    }

    if (width === lastWidth && height === lastHeight) {
      if (!fullImageData || fullImageData.width !== width || fullImageData.height !== height) {
        fullImageData = new ImageData(width, height);
        fullImageDataBytes = new Uint8Array(fullImageData.data.buffer, fullImageData.data.byteOffset, byteLen);
        fullImageData32 = new Uint32Array(fullImageData.data.buffer, fullImageData.data.byteOffset, pixelCount);
      }
      swapBgraToRgba32(rawBgra, fullImageData32, pixelCount, fullImageDataBytes);
      return fullImageData;
    }

    if (!scratchBuffer || scratchBuffer.byteLength < byteLen) {
      scratchBuffer = new ArrayBuffer(byteLen);
      scratchBytes = new Uint8Array(scratchBuffer);
      scratch32 = new Uint32Array(scratchBuffer);
    }
    swapBgraToRgba32(rawBgra, scratch32, pixelCount, scratchBytes);
    return new ImageData(new Uint8ClampedArray(scratchBuffer, 0, byteLen), width, height);
  }

  function clearPendingDraws(maxOrderId = Infinity) {
    for (let i = pendingDraws.length - 1; i >= 0; i--) {
      if (pendingDraws[i].orderId < maxOrderId) {
        disposeDrawItem(pendingDraws[i]);
        pendingDraws.splice(i, 1);
      }
    }
    if (pendingH264Frame && pendingH264Frame.orderId < maxOrderId) {
      try {
        pendingH264Frame.frame.close();
      } catch {}
      pendingH264Frame = null;
    }
  }

  function pushDrawItem(item) {
    if (item.fullFrame) {
      clearPendingDraws(item.orderId);
    } else {
      for (let i = pendingDraws.length - 1; i >= 0; i--) {
        const prev = pendingDraws[i];
        if (prev.orderId < item.orderId && prev.canvasW === item.canvasW && prev.canvasH === item.canvasH && rectContains(item.rect, prev.rect)) {
          disposeDrawItem(prev);
          pendingDraws.splice(i, 1);
        }
      }
    }
    pendingDraws.push(item);
    scheduleRender();
  }

  function scheduleRender() {
    if (destroyed || rafId) return;
    rafId = scheduleFrame(flushRender);
  }

  function drawItem(item) {
    const context = ensureCanvasSize(item.canvasW, item.canvasH);
    if (!context) {
      disposeDrawItem(item);
      return;
    }
    const { x, y, width, height } = item.rect;
    if (width <= 0 || height <= 0) {
      disposeDrawItem(item);
      return;
    }
    if (item.bitmap) {
      try {
        context.drawImage(item.bitmap, x, y, width, height);
        painted(item.sequence);
      } finally {
        disposeDrawItem(item);
      }
      return;
    }
    const imgData = convertToReusableImageData(item.rawBgra, width, height);
    context.putImageData(imgData, x, y);
    painted(item.sequence);
  }

  function flushRender() {
    rafId = 0;
    if (destroyed) {
      clearPendingDraws();
      return;
    }

    const h264 = pendingH264Frame;
    pendingH264Frame = null;
    const draws = pendingDraws.splice(0, pendingDraws.length);

    if (h264) {
      // Any queued 2D draws older than the latest full-frame VideoFrame are superseded.
      const newerDraws = [];
      for (const item of draws) {
        if (item.orderId < h264.orderId) {
          disposeDrawItem(item);
        } else {
          newerDraws.push(item);
        }
      }
      const context = ensureCanvasSize(h264.width, h264.height);
      try {
        if (context) {
          context.drawImage(h264.frame, 0, 0, h264.width, h264.height);
          painted(h264.sequence);
        }
      } finally {
        try {
          h264.frame.close();
        } catch {}
      }
      for (const item of newerDraws) {
        drawItem(item);
      }
      return;
    }

    let startIdx = 0;
    for (let i = draws.length - 1; i >= 0; i--) {
      if (draws[i].fullFrame) {
        startIdx = i;
        break;
      }
    }
    for (let i = 0; i < startIdx; i++) {
      disposeDrawItem(draws[i]);
    }
    for (let i = startIdx; i < draws.length; i++) {
      drawItem(draws[i]);
    }
  }

  async function pumpDecodeQueue() {
    if (decodeRunning || destroyed) return;
    decodeRunning = true;
    try {
      while (decodeQueue.length > 0 && !destroyed) {
        const job = decodeQueue.shift();
        if (!job || job.orderId < minValidOrderId) continue;
        const { packet, orderId } = job;
        const { x, y, width, height } = packet.rect;
        if (width <= 0 || height <= 0) continue;

        try {
          if (packet.encoding === 'png') {
            const blob = new Blob([packet.payload], { type: 'image/png' });
            const bitmap = await createImageBitmap(blob);
            if (destroyed || orderId < minValidOrderId) {
              bitmap.close();
              continue;
            }
            pushDrawItem({
              orderId,
              sequence: packet.sequence, fullFrame: packet.fullFrame,
              canvasW: packet.width,
              canvasH: packet.height,
              rect: packet.rect,
              bitmap
            });
          } else {
            const rawBgra = await decodeDeflateRaw(packet.payload, width * height * 4);
            if (destroyed || orderId < minValidOrderId) continue;
            pushDrawItem({
              orderId,
              sequence: packet.sequence, fullFrame: packet.fullFrame,
              canvasW: packet.width,
              canvasH: packet.height,
              rect: packet.rect,
              rawBgra
            });
          }
        } catch (err) {
          if (!destroyed && job.onError) job.onError(err);
        }
      }
    } finally {
      decodeRunning = false;
      if (decodeQueue.length > 0 && !destroyed) {
        void pumpDecodeQueue();
      }
    }
  }

  function resetH264Decoder() {
    h264Configured = false;
    h264WaitingKeyFrame = false;
    h264Width = 0;
    h264Height = 0;
    h264DroppedDeltas = 0;
    h264OrderQueue.length = 0;
    if (pendingH264Frame) {
      try {
        pendingH264Frame.frame.close();
      } catch {}
      pendingH264Frame = null;
    }
    if (h264Decoder) {
      try {
        if (h264Decoder.state !== 'closed') h264Decoder.close();
      } catch {}
      h264Decoder = null;
    }
  }

  function handleH264CodecFailure(err) {
    resetH264Decoder();
    if (!destroyed) {
      onCodecError?.('h264', err);
    }
  }

  function ensureH264Decoder() {
    if (h264Decoder && h264Decoder.state !== 'closed') return h264Decoder;
    h264Configured = false;
    h264OrderQueue.length = 0;
    h264Decoder = new VideoDecoder({
      output(videoFrame) {
        const { orderId, sequence } = h264OrderQueue.shift() || { orderId: ++nextOrderId };
        if (destroyed || orderId < minValidOrderId) {
          try {
            videoFrame.close();
          } catch {}
          return;
        }
        const width = videoFrame.displayWidth || videoFrame.codedWidth || h264Width || canvas?.width || 1280;
        const height = videoFrame.displayHeight || videoFrame.codedHeight || h264Height || canvas?.height || 720;
        if (pendingH264Frame) {
          try {
            pendingH264Frame.frame.close();
          } catch {}
        }
        pendingH264Frame = { frame: videoFrame, orderId, sequence, width, height };
        scheduleRender();
      },
      error(err) {
        handleH264CodecFailure(err);
      }
    });
    return h264Decoder;
  }

  function enqueueH264(packet, orderId) {
    if (typeof VideoDecoder === 'undefined' || typeof EncodedVideoChunk === 'undefined') {
      onCodecError?.('h264', new Error('当前浏览器不支持 WebCodecs VideoDecoder'));
      return;
    }

    const sizeChanged = h264Width !== packet.width || h264Height !== packet.height;
    if (!h264Decoder || h264Decoder.state === 'closed' || !h264Configured || sizeChanged) {
      if (!packet.isKeyFrame) {
        h264WaitingKeyFrame = true;
        if (++h264DroppedDeltas === 15) {
          onNeedKeyFrame?.();
        }
        return;
      }
      const decoder = ensureH264Decoder();
      const codec = extractH264CodecFromAnnexB(packet.payload) || 'avc1.4D401F';
      try {
        decoder.configure({
          codec,
          codedWidth: packet.width,
          codedHeight: packet.height,
          optimizeForLatency: true
        });
        h264Configured = true;
        h264WaitingKeyFrame = false;
        h264DroppedDeltas = 0;
        h264Width = packet.width;
        h264Height = packet.height;
      } catch (err) {
        handleH264CodecFailure(err);
        return;
      }
    } else if (h264WaitingKeyFrame) {
      if (!packet.isKeyFrame) {
        if (++h264DroppedDeltas === 15) {
          onNeedKeyFrame?.();
        }
        return;
      }
      h264WaitingKeyFrame = false;
      h264DroppedDeltas = 0;
    }

    try {
      h264OrderQueue.push({ orderId, sequence: packet.sequence });
      const chunk = new EncodedVideoChunk({
        type: packet.isKeyFrame ? 'key' : 'delta',
        timestamp: Math.max(0, Math.trunc(packet.timestampUs || 0)),
        duration: packet.durationUs > 0 ? Math.trunc(packet.durationUs) : undefined,
        data: packet.payload
      });
      h264Decoder.decode(chunk);
    } catch (err) {
      if (h264OrderQueue.length > 0) h264OrderQueue.pop();
      handleH264CodecFailure(err);
    }
  }

  /**
   * Non-blocking entry point for WebSocket onmessage. Never stalls reception on async decoding.
   */
  function enqueueVideo(packet) {
    if (destroyed || !packet) return;
    if (packet.kind === 'h264') {
      const orderId = ++nextOrderId;
      enqueueH264(packet, orderId);
      return;
    }
    if (packet.kind !== 'video') return;
    if (packet.encoding === 'regions') {
      if (!packet.fullFrame && (displayed !== packet.baseSequence || canvas.width !== packet.width || canvas.height !== packet.height)) {
        displayed = null;
        onNeedKeyFrame?.();
        return;
      }
      minValidOrderId = ++nextOrderId;
      decodeQueue.length = 0;
      clearPendingDraws();
      const context = ensureCanvasSize(packet.width, packet.height);
      if (!context) return;
      const base = packet.moves.length ? context.getImageData(0, 0, packet.width, packet.height) : null;
      for (const m of packet.moves) context.putImageData(base, m.x - m.sx, m.y - m.sy, m.sx, m.sy, m.width, m.height);
      for (const r of packet.dirty) context.putImageData(convertToReusableImageData(r.pixels, r.width, r.height), r.x, r.y);
      painted(packet.sequence);
      return;
    }
    const { x, y, width, height } = packet.rect;
    if (width <= 0 || height <= 0) return;

    const orderId = ++nextOrderId;
    if (packet.encoding === 'bgra') {
      if (packet.fullFrame) {
        minValidOrderId = orderId;
        decodeQueue.length = 0;
      }
      pushDrawItem({
        orderId,
        sequence: packet.sequence, fullFrame: packet.fullFrame,
        canvasW: packet.width,
        canvasH: packet.height,
        rect: packet.rect,
        rawBgra: packet.payload
      });
      return;
    }

    if (packet.fullFrame) {
      minValidOrderId = orderId;
      decodeQueue.length = 0;
      clearPendingDraws(orderId);
    }
    decodeQueue.push({ orderId, packet });
    void pumpDecodeQueue();
  }

  /**
   * Backwards-compatible async draw method for direct/test callers.
   */
  async function drawVideo(packet) {
    if (destroyed || !packet) return;
    if (packet.kind === 'h264' || packet.encoding === 'regions') {
      enqueueVideo(packet);
      flushRender();
      return;
    }
    if (packet.kind !== 'video') return;
    const context = ensureCanvasSize(packet.width, packet.height);
    if (!context) return;
    const { x, y, width, height } = packet.rect;
    if (width <= 0 || height <= 0) return;

    if (packet.encoding === 'png') {
      const blob = new Blob([packet.payload], { type: 'image/png' });
      const bitmap = await createImageBitmap(blob);
      try {
        if (!destroyed) {
          context.drawImage(bitmap, x, y, width, height);
        }
      } finally {
        bitmap.close();
      }
      return;
    }

    const rawBgra = packet.encoding === 'deflate' ? await decodeDeflateRaw(packet.payload, width * height * 4) : packet.payload;
    if (destroyed) return;
    const imageData = convertToReusableImageData(rawBgra, width, height);
    context.putImageData(imageData, x, y);
  }

  function playAudio(packet, muted) {
    if (destroyed || muted || !packet || packet.kind !== 'audio') return;
    const AudioContextCtor = typeof window !== 'undefined' && (window.AudioContext || window.webkitAudioContext);
    if (!AudioContextCtor || !packet.sampleRate || !packet.channels) return;
    if (!audioCtx) {
      audioCtx = new AudioContextCtor();
      nextAudioTime = audioCtx.currentTime;
    }
    if (audioCtx.state === 'suspended') {
      void audioCtx.resume().catch(() => {});
    }

    const bytesPerSample = packet.sampleFormat === 's16le' ? 2 : 4;
    const frameCount = Math.floor(packet.samples.byteLength / (bytesPerSample * packet.channels));
    if (frameCount <= 0) return;

    const buffer = audioCtx.createBuffer(packet.channels, frameCount, packet.sampleRate);
    const view = new DataView(packet.samples.buffer, packet.samples.byteOffset, packet.samples.byteLength);
    for (let ch = 0; ch < packet.channels; ch++) {
      const channelData = buffer.getChannelData(ch);
      for (let i = 0; i < frameCount; i++) {
        const offset = (i * packet.channels + ch) * bytesPerSample;
        channelData[i] = packet.sampleFormat === 's16le' ? view.getInt16(offset, true) / 32768 : view.getFloat32(offset, true);
      }
    }

    const source = audioCtx.createBufferSource();
    source.buffer = buffer;
    source.connect(audioCtx.destination);
    const now = audioCtx.currentTime;
    if (nextAudioTime < now || nextAudioTime > now + 0.35) {
      nextAudioTime = now + 0.02;
    }
    source.start(nextAudioTime);
    nextAudioTime += buffer.duration;
  }

  function reset() {
    displayed = null;
    nextAudioTime = 0;
    minValidOrderId = ++nextOrderId;
    decodeQueue.length = 0;
    clearPendingDraws();
    resetH264Decoder();
    if (rafId) {
      cancelFrame(rafId);
      rafId = 0;
    }
    if (ctx && lastWidth > 0 && lastHeight > 0) {
      ctx.clearRect(0, 0, lastWidth, lastHeight);
    }
  }

  function destroy() {
    destroyed = true;
    minValidOrderId = ++nextOrderId;
    decodeQueue.length = 0;
    clearPendingDraws();
    resetH264Decoder();
    if (rafId) {
      cancelFrame(rafId);
      rafId = 0;
    }
    if (audioCtx) {
      void audioCtx.close().catch(() => {});
      audioCtx = null;
    }
  }

  return {
    enqueueVideo,
    drawVideo,
    flushRender,
    playAudio,
    reset,
    destroy,
    get pendingQueueLength() {
      return decodeQueue.length + pendingDraws.length + (pendingH264Frame ? 1 : 0);
    }
  };
}

// KeyboardEvent.code -> Windows PS/2 Set-1 scanCode + extended flag
const CODE_MAP = {
  Escape: [0x01, false],
  Digit1: [0x02, false],
  Digit2: [0x03, false],
  Digit3: [0x04, false],
  Digit4: [0x05, false],
  Digit5: [0x06, false],
  Digit6: [0x07, false],
  Digit7: [0x08, false],
  Digit8: [0x09, false],
  Digit9: [0x0a, false],
  Digit0: [0x0b, false],
  Minus: [0x0c, false],
  Equal: [0x0d, false],
  Backspace: [0x0e, false],
  Tab: [0x0f, false],
  KeyQ: [0x10, false],
  KeyW: [0x11, false],
  KeyE: [0x12, false],
  KeyR: [0x13, false],
  KeyT: [0x14, false],
  KeyY: [0x15, false],
  KeyU: [0x16, false],
  KeyI: [0x17, false],
  KeyO: [0x18, false],
  KeyP: [0x19, false],
  BracketLeft: [0x1a, false],
  BracketRight: [0x1b, false],
  Enter: [0x1c, false],
  ControlLeft: [0x1d, false],
  KeyA: [0x1e, false],
  KeyS: [0x1f, false],
  KeyD: [0x20, false],
  KeyF: [0x21, false],
  KeyG: [0x22, false],
  KeyH: [0x23, false],
  KeyJ: [0x24, false],
  KeyK: [0x25, false],
  KeyL: [0x26, false],
  Semicolon: [0x27, false],
  Quote: [0x28, false],
  Backquote: [0x29, false],
  ShiftLeft: [0x2a, false],
  Backslash: [0x2b, false],
  KeyZ: [0x2c, false],
  KeyX: [0x2d, false],
  KeyC: [0x2e, false],
  KeyV: [0x2f, false],
  KeyB: [0x30, false],
  KeyN: [0x31, false],
  KeyM: [0x32, false],
  Comma: [0x33, false],
  Period: [0x34, false],
  Slash: [0x35, false],
  ShiftRight: [0x36, false],
  NumpadMultiply: [0x37, false],
  AltLeft: [0x38, false],
  Space: [0x39, false],
  CapsLock: [0x3a, false],
  F1: [0x3b, false],
  F2: [0x3c, false],
  F3: [0x3d, false],
  F4: [0x3e, false],
  F5: [0x3f, false],
  F6: [0x40, false],
  F7: [0x41, false],
  F8: [0x42, false],
  F9: [0x43, false],
  F10: [0x44, false],
  NumLock: [0x45, false],
  ScrollLock: [0x46, false],
  Numpad7: [0x47, false],
  Numpad8: [0x48, false],
  Numpad9: [0x49, false],
  NumpadSubtract: [0x4a, false],
  Numpad4: [0x4b, false],
  Numpad5: [0x4c, false],
  Numpad6: [0x4d, false],
  NumpadAdd: [0x4e, false],
  Numpad1: [0x4f, false],
  Numpad2: [0x50, false],
  Numpad3: [0x51, false],
  Numpad0: [0x52, false],
  NumpadDecimal: [0x53, false],
  IntlBackslash: [0x56, false],
  F11: [0x57, false],
  F12: [0x58, false],
  NumpadEnter: [0x1c, true],
  ControlRight: [0x1d, true],
  NumpadDivide: [0x35, true],
  PrintScreen: [0x37, true],
  AltRight: [0x38, true],
  Home: [0x47, true],
  ArrowUp: [0x48, true],
  PageUp: [0x49, true],
  ArrowLeft: [0x4b, true],
  ArrowRight: [0x4d, true],
  End: [0x4f, true],
  ArrowDown: [0x50, true],
  PageDown: [0x51, true],
  Insert: [0x52, true],
  Delete: [0x53, true],
  MetaLeft: [0x5b, true],
  MetaRight: [0x5c, true],
  ContextMenu: [0x5d, true]
};

export function mapKeyEventToScanCode(keyboardEvent) {
  const mapped = CODE_MAP[keyboardEvent?.code];
  if (!mapped) return null;
  return { scanCode: mapped[0], extended: mapped[1] };
}

export const CTRL_ALT_DEL_SEQUENCE = [
  { scanCode: 0x1d, extended: false, down: true },
  { scanCode: 0x38, extended: false, down: true },
  { scanCode: 0x53, extended: true, down: true },
  { scanCode: 0x53, extended: true, down: false },
  { scanCode: 0x38, extended: false, down: false },
  { scanCode: 0x1d, extended: false, down: false }
];

const MAX_BROWSER_CURSOR_PX = 128;

function formatCssCursorUrl(dataUrl, hotX, hotY) {
  return `url("${dataUrl}") ${hotX} ${hotY}, default`;
}

async function downscaleCursorDataUrl(dataUrl, width, height, hotX, hotY) {
  if (typeof document === 'undefined' || typeof Image === 'undefined') {
    return 'default';
  }
  const scale = MAX_BROWSER_CURSOR_PX / Math.max(width, height);
  const scaledW = Math.max(1, Math.min(MAX_BROWSER_CURSOR_PX, Math.round(width * scale)));
  const scaledH = Math.max(1, Math.min(MAX_BROWSER_CURSOR_PX, Math.round(height * scale)));
  const scaledHotX = Math.max(0, Math.min(scaledW - 1, Math.round((hotX * scaledW) / width)));
  const scaledHotY = Math.max(0, Math.min(scaledH - 1, Math.round((hotY * scaledH) / height)));
  try {
    const img = new Image();
    await new Promise((resolve, reject) => {
      img.onload = resolve;
      img.onerror = reject;
      img.src = dataUrl;
    });
    const canvas = document.createElement('canvas');
    canvas.width = scaledW;
    canvas.height = scaledH;
    const context = canvas.getContext('2d');
    if (!context) return 'default';
    context.drawImage(img, 0, 0, scaledW, scaledH);
    return formatCssCursorUrl(canvas.toDataURL('image/png'), scaledHotX, scaledHotY);
  } catch {
    return 'default';
  }
}

export function resolveRemoteCursorStyle(msg) {
  if (!msg || !msg.visible || !msg.png) return null;
  const width = Math.trunc(Number(msg.width) || 0);
  const height = Math.trunc(Number(msg.height) || 0);
  if (width <= 0 || height <= 0) return 'default';
  const hotX = Math.max(0, Math.min(width - 1, Math.trunc(Number(msg.hotX) || 0)));
  const hotY = Math.max(0, Math.min(height - 1, Math.trunc(Number(msg.hotY) || 0)));
  const dataUrl = `data:image/png;base64,${msg.png}`;
  if (width <= MAX_BROWSER_CURSOR_PX && height <= MAX_BROWSER_CURSOR_PX) {
    return formatCssCursorUrl(dataUrl, hotX, hotY);
  }
  return downscaleCursorDataUrl(dataUrl, width, height, hotX, hotY);
}

export function hostDisplayScalePercent(devicePixelRatio) {
  const ratio = Number(devicePixelRatio);
  const safeRatio = Number.isFinite(ratio) && ratio > 0 ? ratio : 1;
  return Math.min(300, Math.max(100, Math.round(safeRatio * 4) * 25));
}

export function resolveEnhancedDesktopScale(desktopScale, width, devicePixelRatio = 1) {
  const requested = Math.trunc(Number(desktopScale) || 0);
  if (requested >= 100) {
    return Math.min(500, requested);
  }
  return Number(width) >= 1920 ? hostDisplayScalePercent(devicePixelRatio) : 100;
}

export const MAX_AUDIO_INPUT_PACKET_BYTES = 1024 * 1024;
const CONSOLE_WIRE_HEADER_BYTES = 18;

function encodeBytesBase64(bytes) {
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 8192) {
    const slice = bytes.subarray(offset, offset + 8192);
    binary += String.fromCharCode(...slice);
  }
  if (typeof btoa === 'function') return btoa(binary);
  return Buffer.from(bytes).toString('base64');
}

function decodeBase64Bytes(base64) {
  if (typeof base64 !== 'string' || !base64) return new Uint8Array(0);
  if (typeof atob === 'function') {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
  }
  return new Uint8Array(Buffer.from(base64, 'base64'));
}

export function encodeClipboardTextMessage(text) {
  const source = String(text ?? '').replace(/\r?\n/g, '\r\n');
  const bytes = new Uint8Array((source.length + 1) * 2);
  for (let i = 0; i < source.length; i++) {
    const code = source.charCodeAt(i);
    bytes[i * 2] = code & 0xff;
    bytes[i * 2 + 1] = (code >> 8) & 0xff;
  }
  return {
    type: 'clipboard',
    formats: [
      {
        id: 13,
        name: null,
        data: encodeBytesBase64(bytes)
      }
    ]
  };
}

export function decodeClipboardTextMessage(msg) {
  if (!msg || !Array.isArray(msg.formats)) return null;
  const unicodeFormat = msg.formats.find((f) => f && f.id === 13 && typeof f.data === 'string');
  if (!unicodeFormat) return null;
  let bytes;
  try {
    bytes = decodeBase64Bytes(unicodeFormat.data);
  } catch {
    return null;
  }
  if (bytes.length < 2 || bytes.length % 2 !== 0) return null;
  const chars = [];
  for (let i = 0; i + 1 < bytes.length; i += 2) {
    const code = bytes[i] | (bytes[i + 1] << 8);
    if (code === 0) break;
    chars.push(code);
  }
  let text = '';
  for (let offset = 0; offset < chars.length; offset += 8192) {
    text += String.fromCharCode(...chars.slice(offset, offset + 8192));
  }
  return text.replace(/\r\n/g, '\n');
}

export function resolveAudioSampleKind(sampleFormat) {
  if (sampleFormat === 0 || sampleFormat === 'Signed16LittleEndian') return 1;
  if (sampleFormat === 1 || sampleFormat === 'Float32LittleEndian') return 2;
  return 0;
}

export function encodeConsoleAudioInputPacket(format, channelSamples, inputSampleRate, timestampTicks = 0n) {
  const sampleRate = Math.trunc(Number(format?.sampleRate) || 0);
  const targetChannels = Math.trunc(Number(format?.channels) || 0);
  const sampleKind = resolveAudioSampleKind(format?.sampleFormat);
  if (sampleRate < 8000 || targetChannels < 1 || targetChannels > 8 || !sampleKind) return null;

  const sources = Array.isArray(channelSamples) ? channelSamples : [channelSamples];
  const srcRate = Math.trunc(Number(inputSampleRate) || 0) || sampleRate;
  const firstSrc = sources[0];
  if (!firstSrc || !firstSrc.length || srcRate < 8000) return null;

  const srcChannels = sources.length;
  const srcFrames = firstSrc.length;
  const ratio = sampleRate / srcRate;
  const targetFrames = srcRate === sampleRate
    ? srcFrames
    : Math.max(1, Math.round(srcFrames * ratio));
  const bytesPerSample = sampleKind === 2 ? 4 : 2;
  const payloadBytes = targetFrames * targetChannels * bytesPerSample;
  if (payloadBytes <= 0 || payloadBytes > MAX_AUDIO_INPUT_PACKET_BYTES) return null;

  const buffer = new ArrayBuffer(CONSOLE_WIRE_HEADER_BYTES + payloadBytes);
  const view = new DataView(buffer);
  const bytes = new Uint8Array(buffer);
  bytes[0] = CONSOLE_WIRE_MAGIC_0;
  bytes[1] = CONSOLE_WIRE_MAGIC_1;
  bytes[2] = 3;
  bytes[3] = 0;
  bytes[4] = sampleKind;
  bytes[5] = targetChannels;
  view.setInt32(6, sampleRate, true);
  view.setBigInt64(10, BigInt(timestampTicks >= 0n ? timestampTicks : 0n), true);

  let writeOffset = CONSOLE_WIRE_HEADER_BYTES;
  for (let f = 0; f < targetFrames; f++) {
    const srcPos = srcRate === sampleRate ? f : (f / ratio);
    const idx0 = Math.min(srcFrames - 1, Math.floor(srcPos));
    const idx1 = Math.min(srcFrames - 1, idx0 + 1);
    const frac = srcPos - idx0;
    for (let ch = 0; ch < targetChannels; ch++) {
      let sample;
      if (targetChannels === 1 && srcChannels > 1) {
        let sum = 0;
        for (let sc = 0; sc < srcChannels; sc++) {
          const arr = sources[sc] || firstSrc;
          const v0 = arr[idx0] || 0;
          const v1 = arr[idx1] || 0;
          sum += v0 + (v1 - v0) * frac;
        }
        sample = sum / srcChannels;
      } else {
        const arr = sources[ch] || firstSrc;
        const v0 = arr[idx0] || 0;
        const v1 = arr[idx1] || 0;
        sample = v0 + (v1 - v0) * frac;
      }
      const clamped = Math.max(-1, Math.min(1, Number.isFinite(sample) ? sample : 0));
      if (sampleKind === 2) {
        view.setFloat32(writeOffset, clamped, true);
        writeOffset += 4;
      } else {
        view.setInt16(writeOffset, clamped < 0 ? Math.round(clamped * 32768) : Math.round(clamped * 32767), true);
        writeOffset += 2;
      }
    }
  }
  return new Uint8Array(buffer);
}
