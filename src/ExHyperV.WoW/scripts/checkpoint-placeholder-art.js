// Procedural 2x HiDPI unified checkpoint art generator focused on Directions A & C:
// A: Original non-copyrighted VM screen captures (Desktop Telemetry / Kernel Console)
// C: Restrained ExHyperV-branded no-capture standby / differencing chain states
const ART_W = 352;
const ART_H = 136;

export const CHECKPOINT_ART_MODES = [
  'c-brand-matte',
  'c-iso-chain',
  'a-vm-desktop',
  'a-console-shell'
];

export const DEFAULT_CHECKPOINT_ART_MODE = 'c-brand-matte';

const BRAND_ICON_MODES = new Set([
  'a-vm-desktop',
  'a-console-shell',
  'c-brand-matte'
]);

const artUrlCache = new Map();

function clamp01(x) {
  return x < 0 ? 0 : x > 1 ? 1 : x;
}

function clamp255(x) {
  return x < 0 ? 0 : x > 255 ? 255 : (x + 0.5) | 0;
}

function smoothstep(e0, e1, x) {
  const t = clamp01((x - e0) / (e1 - e0));
  return t * t * (3 - 2 * t);
}

function mix(a, b, t) {
  return a + (b - a) * t;
}

function hash2(x, y) {
  const n = Math.sin(x * 127.1 + y * 311.7) * 43758.5453123;
  return n - Math.floor(n);
}

function sdRoundRect(px, py, cx, cy, hw, hh, rad) {
  const qx = Math.abs(px - cx) - hw + rad;
  const qy = Math.abs(py - cy) - hh + rad;
  return Math.sqrt(Math.max(qx, 0) ** 2 + Math.max(qy, 0) ** 2) + Math.min(Math.max(qx, qy), 0) - rad;
}

function sdIsometricDiamond(px, py, cx, cy, rx, ry, cornerR) {
  const dx = Math.abs(px - cx);
  const dy = Math.abs(py - cy);
  const dot = dx / rx + dy / ry - 1.0;
  const scale = 1 / Math.sqrt((1 / (rx * rx)) + (1 / (ry * ry)));
  return dot * scale - cornerR;
}

function drawAACapsule(buf, W, H, x0, y0, x1, y1, rad, col, alpha) {
  const cx = (x0 + x1) * 0.5;
  const cy = (y0 + y1) * 0.5;
  const hw = (x1 - x0) * 0.5;
  const hh = (y1 - y0) * 0.5;
  const xs = Math.max(0, Math.floor(x0 - 2));
  const xe = Math.min(W - 1, Math.ceil(x1 + 2));
  const ys = Math.max(0, Math.floor(y0 - 2));
  const ye = Math.min(H - 1, Math.ceil(y1 + 2));
  for (let y = ys; y <= ye; y++) {
    for (let x = xs; x <= xe; x++) {
      const m = clamp01(0.5 - sdRoundRect(x, y, cx, cy, hw, hh, rad));
      if (m > 0.004) {
        const idx = (y * W + x) * 4;
        buf[idx] = clamp255(mix(buf[idx], col[0], m * alpha));
        buf[idx + 1] = clamp255(mix(buf[idx + 1], col[1], m * alpha));
        buf[idx + 2] = clamp255(mix(buf[idx + 2], col[2], m * alpha));
      }
    }
  }
}

// Direction A1: Original VM Desktop Screen Capture (Deep cobalt desktop + Fluent telemetry card + dock)
function renderAVmDesktop(W, H, buf) {
  for (let y = 0; y < H; y++) {
    const v = y / (H - 1);
    for (let x = 0; x < W; x++) {
      const u = x / (W - 1);
      const g1 = Math.exp(-(((u - 0.25) ** 2) * 5.2 + ((v - 0.58) ** 2) * 4.0));
      const g2 = Math.exp(-(((u - 0.78) ** 2) * 3.8 + ((v - 0.36) ** 2) * 4.5));
      const r = 10 + g1 * 18 + g2 * 10 + (1 - v) * 4;
      const g = 17 + g1 * 48 + g2 * 38 + (1 - v) * 8;
      const b = 31 + g1 * 104 + g2 * 76 + (1 - v) * 14;
      const grain = (hash2(x + 7, y + 13) - 0.5) * 1.4;
      const idx = (y * W + x) * 4;
      buf[idx] = clamp255(r + grain);
      buf[idx + 1] = clamp255(g + grain);
      buf[idx + 2] = clamp255(b + grain);
      buf[idx + 3] = 255;
    }
  }

  // Right-weighted frosted acrylic VM workstation telemetry window
  const winCx = 234;
  const winCy = 66;
  const winHw = 94;
  const winHh = 42;
  const winRad = 7;

  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const d = sdRoundRect(x, y, winCx, winCy, winHw, winHh, winRad);
      const idx = (y * W + x) * 4;

      if (d > 0 && d < 20) {
        const sh = Math.exp(-(d * d) / 48) * 0.55;
        buf[idx] = clamp255(buf[idx] * (1 - sh));
        buf[idx + 1] = clamp255(buf[idx + 1] * (1 - sh));
        buf[idx + 2] = clamp255(buf[idx + 2] * (1 - sh));
      }

      const mask = clamp01(0.5 - d);
      if (mask > 0.005) {
        const isTitle = y < (winCy - winHh + 14);
        buf[idx] = clamp255(mix(buf[idx], isTitle ? 28 : 21, mask * 0.92));
        buf[idx + 1] = clamp255(mix(buf[idx + 1], isTitle ? 34 : 26, mask * 0.92));
        buf[idx + 2] = clamp255(mix(buf[idx + 2], isTitle ? 44 : 34, mask * 0.92));

        const border = Math.exp(-((d + 0.55) ** 2) / 0.50) * 0.36;
        buf[idx] = clamp255(buf[idx] + border * 190);
        buf[idx + 1] = clamp255(buf[idx + 1] + border * 212);
        buf[idx + 2] = clamp255(buf[idx + 2] + border * 242);
      }

      if (Math.abs(y - (winCy - winHh + 14)) < 0.6 && Math.abs(x - winCx) < winHw - 2) {
        buf[idx] = clamp255(buf[idx] + 10);
        buf[idx + 1] = clamp255(buf[idx + 1] + 12);
        buf[idx + 2] = clamp255(buf[idx + 2] + 16);
      }

      if (x >= 152 && x <= 316 && y >= 58 && y <= 98) {
        const t = (x - 152) / 164;
        const curveY = 84 - Math.sin(t * 7.2 + 0.3) * 8 - Math.sin(t * 16.0) * 3 - t * 6;
        if (y > curveY) {
          const fillA = smoothstep(98, curveY, y) * 0.25;
          buf[idx] = clamp255(mix(buf[idx], 56, fillA));
          buf[idx + 1] = clamp255(mix(buf[idx + 1], 189, fillA));
          buf[idx + 2] = clamp255(mix(buf[idx + 2], 248, fillA));
        }
        const stroke = Math.exp(-((y - curveY) ** 2) / 0.72);
        if (stroke > 0.02) {
          buf[idx] = clamp255(mix(buf[idx], 96, stroke));
          buf[idx + 1] = clamp255(mix(buf[idx + 1], 220, stroke));
          buf[idx + 2] = clamp255(mix(buf[idx + 2], 255, stroke));
        }
      }
    }
  }

  drawAACapsule(buf, W, H, 150, 29, 214, 33, 2, [190, 212, 236], 0.55);
  drawAACapsule(buf, W, H, 312, 28, 318, 34, 3, [52, 211, 153], 0.95);
  drawAACapsule(buf, W, H, 152, 46, 204, 51, 2.5, [96, 210, 255], 0.85);
  drawAACapsule(buf, W, H, 212, 46, 256, 51, 2.5, [160, 185, 215], 0.45);
  drawAACapsule(buf, W, H, 152, 56, 230, 60, 2, [148, 163, 184], 0.42);

  for (let y = 122; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const idx = (y * W + x) * 4;
      buf[idx] = clamp255(mix(buf[idx], 10, 0.78));
      buf[idx + 1] = clamp255(mix(buf[idx + 1], 14, 0.78));
      buf[idx + 2] = clamp255(mix(buf[idx + 2], 22, 0.78));
      if (y === 122) {
        buf[idx] = clamp255(buf[idx] + 24);
        buf[idx + 1] = clamp255(buf[idx + 1] + 30);
        buf[idx + 2] = clamp255(buf[idx + 2] + 40);
      }
    }
  }

  drawAACapsule(buf, W, H, 155, 126, 161, 132, 1.8, [76, 194, 255], 0.95);
  drawAACapsule(buf, W, H, 167, 126, 173, 132, 1.8, [164, 178, 198], 0.72);
  drawAACapsule(buf, W, H, 179, 126, 185, 132, 1.8, [52, 211, 153], 0.88);
  drawAACapsule(buf, W, H, 191, 126, 197, 132, 1.8, [168, 158, 240], 0.78);
  drawAACapsule(buf, W, H, 316, 127, 340, 131, 1.8, [175, 192, 214], 0.48);
}

// Direction A2: Hypervisor Kernel / TTY Console Diagnostic Capture
function renderAConsoleShell(W, H, buf) {
  for (let y = 0; y < H; y++) {
    const v = y / (H - 1);
    for (let x = 0; x < W; x++) {
      const u = x / (W - 1);
      const glow = Math.exp(-(((u - 0.62) ** 2) * 3.8 + ((v - 0.56) ** 2) * 4.2));
      const r = 9 + glow * 8;
      const g = 13 + glow * 22;
      const b = 18 + glow * 32;
      const grain = (hash2(x + 3, y + 29) - 0.5) * 1.2;
      const idx = (y * W + x) * 4;
      buf[idx] = clamp255(r + grain);
      buf[idx + 1] = clamp255(g + grain);
      buf[idx + 2] = clamp255(b + grain);
      buf[idx + 3] = 255;
    }
  }

  for (let y = 14; y < 124; y++) {
    for (let x = 14; x < 338; x++) {
      const d = sdRoundRect(x, y, 176, 69, 160, 52, 6);
      if (Math.abs(d) < 0.75) {
        const idx = (y * W + x) * 4;
        buf[idx] = clamp255(buf[idx] + 22);
        buf[idx + 1] = clamp255(buf[idx + 1] + 32);
        buf[idx + 2] = clamp255(buf[idx + 2] + 44);
      }
    }
  }

  drawAACapsule(buf, W, H, 114, 24, 232, 28, 2, [138, 160, 184], 0.55);

  const rows = [
    [52, [52, 211, 153], 158, [203, 213, 225]],
    [66, [52, 211, 153], 184, [203, 213, 225]],
    [80, [56, 189, 248], 136, [186, 230, 253]],
    [94, [56, 189, 248], 96, [148, 163, 184]]
  ];
  for (const [ry, tagCol, len, lineCol] of rows) {
    drawAACapsule(buf, W, H, 28, ry, 46, ry + 5, 2, tagCol, 0.90);
    drawAACapsule(buf, W, H, 54, ry, 54 + len, ry + 5, 2.2, lineCol, 0.65);
  }
  drawAACapsule(buf, W, H, 158, 93, 164, 100, 1.2, [56, 189, 248], 0.95);
}

// Direction C1: ExHyperV Brand Standby Plate (Calm No-Capture Empty State Backdrop + Dot Matrix)
function renderCBrandMatte(W, H, buf) {
  const cx = 186;
  const cy = 67;

  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const dx = (x - cx) / 94;
      const dy = (y - cy) / 52;
      const d2 = dx * dx + dy * dy;
      const halo = Math.exp(-d2 * 1.25);

      let r = 17 + halo * 9;
      let g = 20 + halo * 22;
      let b = 26 + halo * 44;

      const sdx = (x - cx) / 28;
      const sdy = (y - (cy + 31)) / 9;
      const drop = Math.exp(-(sdx * sdx + sdy * sdy) * 1.4) * 0.42;
      r *= (1 - drop);
      g *= (1 - drop);
      b *= (1 - drop);

      if ((x % 14 === 6) && (y % 14 === 6)) {
        const dotFade = Math.exp(-d2 * 0.60) * 0.34;
        r += dotFade * 70;
        g += dotFade * 116;
        b += dotFade * 168;
      }

      const grain = (hash2(x + 19, y + 7) - 0.5) * 1.4;
      const idx = (y * W + x) * 4;
      buf[idx] = clamp255(r + grain);
      buf[idx + 1] = clamp255(g + grain);
      buf[idx + 2] = clamp255(b + grain);
      buf[idx + 3] = 255;
    }
  }
}

// Direction C2: Isometric Snapshot Differencing Chain (3 translucent stacked slabs)
function renderCIsoChain(W, H, buf) {
  const cx = 196;

  for (let y = 0; y < H; y++) {
    const v = y / (H - 1);
    for (let x = 0; x < W; x++) {
      const u = x / (W - 1);
      const glow = Math.exp(-(((u - 0.56) ** 2) * 4.6 + ((v - 0.54) ** 2) * 5.0));
      const r = 15 + glow * 8;
      const g = 18 + glow * 26;
      const b = 25 + glow * 48;
      const grain = (hash2(x + 23, y + 41) - 0.5) * 1.4;
      const idx = (y * W + x) * 4;
      buf[idx] = clamp255(r + grain);
      buf[idx + 1] = clamp255(g + grain);
      buf[idx + 2] = clamp255(b + grain);
      buf[idx + 3] = 255;
    }
  }

  const layers = [
    { cy: 92, rx: 52, ry: 23, fill: [24, 34, 52], rim: [68, 128, 198], alpha: 0.72, glow: 0.14 },
    { cy: 73, rx: 52, ry: 23, fill: [26, 48, 78], rim: [56, 178, 240], alpha: 0.78, glow: 0.24 },
    { cy: 54, rx: 52, ry: 23, fill: [30, 74, 128], rim: [130, 222, 255], alpha: 0.86, glow: 0.56 }
  ];

  for (let l = 0; l < layers.length; l++) {
    const L = layers[l];
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const dTop = sdIsometricDiamond(x, y, cx, L.cy, L.rx, L.ry, 2.5);
        const dBot = sdIsometricDiamond(x, y, cx, L.cy + 5, L.rx, L.ry, 2.5);
        const idx = (y * W + x) * 4;

        if (dBot > 0 && dBot < 12) {
          const sh = Math.exp(-(dBot * dBot) / 28) * 0.48;
          buf[idx] = clamp255(buf[idx] * (1 - sh));
          buf[idx + 1] = clamp255(buf[idx + 1] * (1 - sh));
          buf[idx + 2] = clamp255(buf[idx + 2] * (1 - sh));
        }

        const sideMask = clamp01(0.5 - dBot) * (1 - clamp01(0.5 - dTop)) * (y > L.cy ? 1 : 0);
        if (sideMask > 0.005) {
          buf[idx] = clamp255(mix(buf[idx], L.fill[0] * 0.62, sideMask * 0.92));
          buf[idx + 1] = clamp255(mix(buf[idx + 1], L.fill[1] * 0.62, sideMask * 0.92));
          buf[idx + 2] = clamp255(mix(buf[idx + 2], L.fill[2] * 0.72, sideMask * 0.92));
        }

        const topMask = clamp01(0.5 - dTop);
        if (topMask > 0.005) {
          const sheen = smoothstep(-50, 50, (x - cx) * 0.7 - (y - L.cy) * 0.9) * 0.25;
          buf[idx] = clamp255(mix(buf[idx], L.fill[0] + L.rim[0] * sheen, topMask * L.alpha));
          buf[idx + 1] = clamp255(mix(buf[idx + 1], L.fill[1] + L.rim[1] * sheen, topMask * L.alpha));
          buf[idx + 2] = clamp255(mix(buf[idx + 2], L.fill[2] + L.rim[2] * sheen, topMask * L.alpha));

          if (l === 2) {
            const dx = x - cx;
            const dy = y - L.cy;
            const d1 = Math.abs(dy / L.ry - dx / L.rx) / Math.sqrt(1 / (L.rx * L.rx) + 1 / (L.ry * L.ry));
            const d2 = Math.abs(dy / L.ry + dx / L.rx) / Math.sqrt(1 / (L.rx * L.rx) + 1 / (L.ry * L.ry));
            const seam = Math.max(Math.exp(-(d1 * d1) / 0.7), Math.exp(-(d2 * d2) / 0.7)) * 0.26 * clamp01(0.5 - dTop - 2.0);
            buf[idx] = clamp255(buf[idx] + seam * 125);
            buf[idx + 1] = clamp255(buf[idx + 1] + seam * 210);
            buf[idx + 2] = clamp255(buf[idx + 2] + seam * 255);
          }

          const edge = Math.exp(-((dTop + 0.6) ** 2) / 0.55) * (0.45 + 0.55 * L.glow);
          buf[idx] = clamp255(buf[idx] + edge * L.rim[0]);
          buf[idx + 1] = clamp255(buf[idx + 1] + edge * L.rim[1]);
          buf[idx + 2] = clamp255(buf[idx + 2] + edge * L.rim[2]);
        }
      }
    }
  }
}

const RENDERERS = {
  'a-vm-desktop': renderAVmDesktop,
  'a-console-shell': renderAConsoleShell,
  'c-brand-matte': renderCBrandMatte,
  'c-iso-chain': renderCIsoChain
};

export function normalizeCheckpointArtMode(mode) {
  return Object.prototype.hasOwnProperty.call(RENDERERS, mode)
    ? mode
    : DEFAULT_CHECKPOINT_ART_MODE;
}

export function checkpointArtUsesBrandIcon(mode) {
  return BRAND_ICON_MODES.has(normalizeCheckpointArtMode(mode));
}

export function renderCheckpointArtRgba(width, height, mode) {
  const key = normalizeCheckpointArtMode(mode);
  const buf = new Uint8ClampedArray(width * height * 4);
  RENDERERS[key](width, height, buf);
  return buf;
}

export function getCheckpointArtDataUrl(mode) {
  const key = normalizeCheckpointArtMode(mode);
  if (artUrlCache.has(key)) {
    return artUrlCache.get(key);
  }
  if (typeof document === 'undefined') {
    return '';
  }

  const canvas = document.createElement('canvas');
  canvas.width = ART_W;
  canvas.height = ART_H;
  const ctx = canvas.getContext('2d');
  if (!ctx) return '';

  const imgData = ctx.createImageData(ART_W, ART_H);
  RENDERERS[key](ART_W, ART_H, imgData.data);
  ctx.putImageData(imgData, 0, 0);

  const dataUrl = canvas.toDataURL('image/png');
  artUrlCache.set(key, dataUrl);
  return dataUrl;
}
