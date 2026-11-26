import { CanvasTexture, SRGBColorSpace } from 'three';

function loadImage(src, onReady) {
  const img = new Image();
  img.decoding = 'async';
  img.onload = () => onReady(img);
  img.src = src;
  return img;
}

/**
 * Rasterize an SVG at explicit targetSize×targetSize so small intrinsic
 * viewBoxes (e.g. 24×24 or 32×32) render razor-sharp on high-res textures.
 */
function loadSvgVector(src, targetSize, onReady) {
  if (typeof fetch !== 'function' || typeof DOMParser !== 'function') {
    loadImage(src, onReady);
    return;
  }
  fetch(src)
    .then((res) => (res.ok ? res.text() : Promise.reject(new Error(`HTTP ${res.status}`))))
    .then((svgText) => {
      const doc = new DOMParser().parseFromString(svgText, 'image/svg+xml');
      const svg = doc.documentElement;
      if (!svg || svg.nodeName.toLowerCase() !== 'svg') {
        loadImage(src, onReady);
        return;
      }
      if (!svg.getAttribute('viewBox')) {
        const w = parseFloat(svg.getAttribute('width') || '128') || 128;
        const h = parseFloat(svg.getAttribute('height') || '128') || 128;
        svg.setAttribute('viewBox', `0 0 ${w} ${h}`);
      }
      svg.setAttribute('width', String(targetSize));
      svg.setAttribute('height', String(targetSize));
      const serialized = new XMLSerializer().serializeToString(svg);
      const blob = new Blob([serialized], { type: 'image/svg+xml;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const img = new Image();
      img.decoding = 'async';
      img.onload = () => {
        URL.revokeObjectURL(url);
        onReady(img);
      };
      img.onerror = () => {
        URL.revokeObjectURL(url);
        loadImage(src, onReady);
      };
      img.src = url;
    })
    .catch(() => loadImage(src, onReady));
}

function createCanvasTex(size) {
  const canvas = document.createElement('canvas');
  canvas.width = size;
  canvas.height = size;
  const texture = new CanvasTexture(canvas);
  texture.colorSpace = SRGBColorSpace;
  texture.anisotropy = 8;
  return { canvas, ctx: canvas.getContext('2d'), texture };
}

/**
 * Load the 6 safe-compromise OS SVG icons into crisp 1024px CanvasTextures
 * for Concept 2's exploded corridor nodes.
 */
export function createOsIconPack() {
  const presets = [
    { id: 'Windows', scale: 0.68, shadow: 'rgba(6, 26, 62, 0.42)', glow: 'rgba(185, 238, 255, 0.45)' },
    { id: 'Ubuntu', scale: 0.70, shadow: 'rgba(8, 18, 42, 0.40)', glow: null },
    { id: 'ArchLinux', scale: 0.70, shadow: 'rgba(8, 18, 42, 0.40)', glow: 'rgba(205, 240, 255, 0.28)' },
    { id: 'Debian', scale: 0.72, shadow: 'rgba(255, 240, 246, 0.62)', glow: 'rgba(255, 255, 255, 0.55)' },
    { id: 'openSUSE', scale: 0.70, shadow: 'rgba(6, 26, 42, 0.40)', glow: 'rgba(135, 235, 165, 0.35)' },
    { id: 'FnOS', scale: 0.68, shadow: 'rgba(6, 26, 62, 0.42)', glow: 'rgba(185, 224, 255, 0.35)' },
  ];

  const S = 1024;
  const items = presets.map((cfg) => {
    const { ctx, texture } = createCanvasTex(S);
    const maxDim = Math.round(S * cfg.scale);

    loadSvgVector(`/ui-design/os/${cfg.id}.svg`, maxDim, (img) => {
      ctx.clearRect(0, 0, S, S);

      const ratio = img.width / Math.max(1, img.height);
      const drawW = ratio >= 1 ? maxDim : maxDim * ratio;
      const drawH = ratio >= 1 ? maxDim / ratio : maxDim;
      const dx = (S - drawW) / 2;
      const dy = (S - drawH) / 2;

      if (cfg.glow) {
        ctx.save();
        ctx.shadowColor = cfg.glow;
        ctx.shadowBlur = 28;
        ctx.shadowOffsetY = 0;
        ctx.drawImage(img, dx, dy, drawW, drawH);
        ctx.restore();
      }

      ctx.save();
      ctx.shadowColor = cfg.shadow;
      ctx.shadowBlur = 18;
      ctx.shadowOffsetY = 8;
      ctx.drawImage(img, dx, dy, drawW, drawH);
      ctx.restore();

      texture.needsUpdate = true;
    });

    return texture;
  });

  return {
    textures: items,
    dispose() {
      for (const tex of items) tex.dispose();
    },
  };
}

/**
 * Soft radial gradient CanvasTexture used for volumetric core aura & energy packets.
 */
export function createRadialGlowTexture(size = 128) {
  const { ctx, texture } = createCanvasTex(size);
  const half = size / 2;
  const grad = ctx.createRadialGradient(half, half, 0, half, half, half);
  grad.addColorStop(0, 'rgba(255, 255, 255, 1)');
  grad.addColorStop(0.22, 'rgba(130, 224, 255, 0.82)');
  grad.addColorStop(0.54, 'rgba(42, 168, 255, 0.28)');
  grad.addColorStop(1, 'rgba(14, 110, 232, 0)');
  ctx.fillStyle = grad;
  ctx.fillRect(0, 0, size, size);
  texture.needsUpdate = true;
  return texture;
}

/**
 * Delicate 4-point optical starlight / crystal flare texture for luminous core focus.
 */
export function createOpticalFlareTexture(size = 256) {
  const { ctx, texture } = createCanvasTex(size);
  const c = size / 2;
  ctx.clearRect(0, 0, size, size);

  // Inner luminous mist
  const halo = ctx.createRadialGradient(c, c, 0, c, c, c * 0.65);
  halo.addColorStop(0, 'rgba(255, 255, 255, 0.96)');
  halo.addColorStop(0.16, 'rgba(170, 240, 255, 0.68)');
  halo.addColorStop(0.45, 'rgba(56, 189, 248, 0.16)');
  halo.addColorStop(1, 'rgba(56, 189, 248, 0)');
  ctx.fillStyle = halo;
  ctx.fillRect(0, 0, size, size);

  // Horizontal & Vertical crystalline flare needles
  const drawRay = (rx, ry, alpha) => {
    ctx.save();
    ctx.translate(c, c);
    ctx.scale(rx, ry);
    const g = ctx.createRadialGradient(0, 0, 0, 0, 0, 1);
    g.addColorStop(0, `rgba(255, 255, 255, ${alpha})`);
    g.addColorStop(0.35, `rgba(175, 242, 255, ${alpha * 0.55})`);
    g.addColorStop(1, 'rgba(56, 189, 248, 0)');
    ctx.fillStyle = g;
    ctx.beginPath();
    ctx.arc(0, 0, 1, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
  };

  drawRay(c * 0.94, size * 0.022, 0.92);
  drawRay(size * 0.022, c * 0.94, 0.92);

  // Subtle 45-deg secondary shimmer cross
  ctx.save();
  ctx.translate(c, c);
  ctx.rotate(Math.PI / 4);
  ctx.translate(-c, -c);
  drawRay(c * 0.54, size * 0.013, 0.55);
  drawRay(size * 0.013, c * 0.54, 0.55);
  ctx.restore();

  texture.needsUpdate = true;
  return texture;
}

function traceRoundedRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, r);
}

/**
 * Multi-layered soft rounded-square ambient occlusion shadow matching a crystal column footprint.
 * Rendered in white + alpha so material.color cleanly tints it for Dark and Light themes.
 */
export function createContactShadowTexture(size = 256) {
  const { ctx, texture } = createCanvasTex(size);
  ctx.clearRect(0, 0, size, size);

  const layers = [
    { inset: size * 0.25, radius: size * 0.14, blur: size * 0.11, alpha: 0.42 },
    { inset: size * 0.29, radius: size * 0.11, blur: size * 0.065, alpha: 0.52 },
    { inset: size * 0.34, radius: size * 0.09, blur: size * 0.032, alpha: 0.48 },
  ];

  for (const l of layers) {
    ctx.save();
    ctx.shadowColor = `rgba(255, 255, 255, ${l.alpha})`;
    ctx.shadowBlur = l.blur;
    ctx.fillStyle = `rgba(255, 255, 255, ${l.alpha * 0.45})`;
    traceRoundedRect(ctx, l.inset, l.inset, size - l.inset * 2, size - l.inset * 2, l.radius);
    ctx.fill();
    ctx.restore();
  }

  // Mask perimeter smoothly so shadow edges never clip on the geometry quad
  ctx.save();
  ctx.globalCompositeOperation = 'destination-in';
  const mask = ctx.createRadialGradient(size / 2, size / 2, size * 0.14, size / 2, size / 2, size * 0.49);
  mask.addColorStop(0, 'rgba(255,255,255,1)');
  mask.addColorStop(0.68, 'rgba(255,255,255,0.85)');
  mask.addColorStop(1, 'rgba(255,255,255,0)');
  ctx.fillStyle = mask;
  ctx.fillRect(0, 0, size, size);
  ctx.restore();

  texture.needsUpdate = true;
  return texture;
}

/**
 * High-craft Fluent architectural frosted-glass plinth mat texture (no sci-fi clock ticks,
 * no dashed target rings). Provides clean specular directional bevel + faded 2x2 Hyper-V quadrant seam.
 */
export function createArchitecturalStageTexture(size = 1024) {
  const { ctx, texture } = createCanvasTex(size);
  const c = size / 2;
  ctx.clearRect(0, 0, size, size);

  // Seamless wide studio floor sheen fading organically into background (zero hard tray edge)
  const floorSheen = ctx.createRadialGradient(c, c, size * 0.02, c, c, size * 0.46);
  floorSheen.addColorStop(0, 'rgba(255, 255, 255, 0.24)');
  floorSheen.addColorStop(0.32, 'rgba(255, 255, 255, 0.11)');
  floorSheen.addColorStop(0.70, 'rgba(255, 255, 255, 0.02)');
  floorSheen.addColorStop(1, 'rgba(255, 255, 255, 0)');
  ctx.fillStyle = floorSheen;
  ctx.fillRect(0, 0, size, size);

  // Whisper-thin fading 2x2 Hyper-V quadrant axis hairlines along the central corridors
  const seamStart = size * 0.12;
  const seamEnd = size - seamStart;
  const hGrad = ctx.createLinearGradient(seamStart, c, seamEnd, c);
  hGrad.addColorStop(0, 'rgba(255, 255, 255, 0)');
  hGrad.addColorStop(0.24, 'rgba(255, 255, 255, 0.14)');
  hGrad.addColorStop(0.5, 'rgba(255, 255, 255, 0.34)');
  hGrad.addColorStop(0.76, 'rgba(255, 255, 255, 0.14)');
  hGrad.addColorStop(1, 'rgba(255, 255, 255, 0)');
  ctx.strokeStyle = hGrad;
  ctx.lineWidth = Math.max(1.2, size * 0.0015);
  ctx.beginPath();
  ctx.moveTo(seamStart, c);
  ctx.lineTo(seamEnd, c);
  ctx.stroke();

  const vGrad = ctx.createLinearGradient(c, seamStart, c, seamEnd);
  vGrad.addColorStop(0, 'rgba(255, 255, 255, 0)');
  vGrad.addColorStop(0.24, 'rgba(255, 255, 255, 0.14)');
  vGrad.addColorStop(0.5, 'rgba(255, 255, 255, 0.34)');
  vGrad.addColorStop(0.76, 'rgba(255, 255, 255, 0.14)');
  vGrad.addColorStop(1, 'rgba(255, 255, 255, 0)');
  ctx.strokeStyle = vGrad;
  ctx.beginPath();
  ctx.moveTo(c, seamStart);
  ctx.lineTo(c, seamEnd);
  ctx.stroke();

  // Compact Fluent chamfered central hub pad nested strictly inside the exploded core bay (~±1.04u)
  const hubSize = size * 0.28;
  const hubOffset = (size - hubSize) / 2;
  const hubRadius = size * 0.048;

  const hubFill = ctx.createLinearGradient(hubOffset, hubOffset, hubOffset + hubSize, hubOffset + hubSize);
  hubFill.addColorStop(0, 'rgba(255, 255, 255, 0.26)');
  hubFill.addColorStop(0.5, 'rgba(255, 255, 255, 0.10)');
  hubFill.addColorStop(1, 'rgba(255, 255, 255, 0.04)');
  ctx.fillStyle = hubFill;
  traceRoundedRect(ctx, hubOffset, hubOffset, hubSize, hubSize, hubRadius);
  ctx.fill();

  const rimGrad = ctx.createLinearGradient(hubOffset, hubOffset, hubOffset + hubSize, hubOffset + hubSize);
  rimGrad.addColorStop(0, 'rgba(255, 255, 255, 0.68)');
  rimGrad.addColorStop(0.5, 'rgba(255, 255, 255, 0.22)');
  rimGrad.addColorStop(1, 'rgba(255, 255, 255, 0.38)');
  ctx.strokeStyle = rimGrad;
  ctx.lineWidth = Math.max(1.5, size * 0.0020);
  traceRoundedRect(ctx, hubOffset, hubOffset, hubSize, hubSize, hubRadius);
  ctx.stroke();

  texture.needsUpdate = true;
  return texture;
}

/**
 * Clean 3D horizontal ripple wave texture for charge-pulse feedback on the stage floor.
 */
export function createPulseRippleTexture(size = 256) {
  const { ctx, texture } = createCanvasTex(size);
  const c = size / 2;
  ctx.clearRect(0, 0, size, size);

  const grad = ctx.createRadialGradient(c, c, size * 0.24, c, c, size * 0.49);
  grad.addColorStop(0, 'rgba(255, 255, 255, 0)');
  grad.addColorStop(0.56, 'rgba(255, 255, 255, 0.08)');
  grad.addColorStop(0.78, 'rgba(255, 255, 255, 0.72)');
  grad.addColorStop(0.90, 'rgba(255, 255, 255, 0.22)');
  grad.addColorStop(1, 'rgba(255, 255, 255, 0)');
  ctx.fillStyle = grad;
  ctx.fillRect(0, 0, size, size);

  texture.needsUpdate = true;
  return texture;
}

/**
 * High-precision 3D hardware virtualization horizon matrix texture with radial feathering.
 * White/alpha monochrome so Three.js material color tints seamlessly across Light/Dark themes.
 */
export function createHorizonMatrixTexture(size = 1024) {
  const { ctx, texture } = createCanvasTex(size);
  const c = size / 2;
  ctx.clearRect(0, 0, size, size);

  const pad = size * 0.05;
  const span = size - pad * 2;
  const cells = 12;
  const step = span / cells;

  // Fine sub-grid lines
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.14)';
  ctx.lineWidth = Math.max(1, size * 0.0012);
  ctx.beginPath();
  for (let i = 0; i <= cells * 2; i++) {
    const p = pad + (i * step) / 2;
    ctx.moveTo(p, pad);
    ctx.lineTo(p, size - pad);
    ctx.moveTo(pad, p);
    ctx.lineTo(size - pad, p);
  }
  ctx.stroke();

  // Primary major grid lines + center axes
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.34)';
  ctx.lineWidth = Math.max(1.2, size * 0.0016);
  ctx.beginPath();
  for (let i = 0; i <= cells; i++) {
    const p = pad + i * step;
    ctx.moveTo(p, pad);
    ctx.lineTo(p, size - pad);
    ctx.moveTo(pad, p);
    ctx.lineTo(size - pad, p);
  }
  ctx.stroke();

  // Micro reticle "+" intersection markers on major grid nodes
  const cross = size * 0.0065;
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.72)';
  ctx.lineWidth = Math.max(1.4, size * 0.0018);
  ctx.beginPath();
  for (let ix = 1; ix < cells; ix++) {
    for (let iy = 1; iy < cells; iy++) {
      const px = pad + ix * step;
      const py = pad + iy * step;
      ctx.moveTo(px - cross, py);
      ctx.lineTo(px + cross, py);
      ctx.moveTo(px, py - cross);
      ctx.lineTo(px, py + cross);
    }
  }
  ctx.stroke();

  // Inner chamber pad frame matching the 4-cell ExHyperV cube footprint
  const chamberSize = step * 4.4;
  const chamberOffset = c - chamberSize / 2;
  const chamberRadius = chamberSize * 0.11;
  ctx.fillStyle = 'rgba(255, 255, 255, 0.10)';
  traceRoundedRect(ctx, chamberOffset, chamberOffset, chamberSize, chamberSize, chamberRadius);
  ctx.fill();

  ctx.strokeStyle = 'rgba(255, 255, 255, 0.62)';
  ctx.lineWidth = Math.max(1.8, size * 0.0022);
  traceRoundedRect(ctx, chamberOffset, chamberOffset, chamberSize, chamberSize, chamberRadius);
  ctx.stroke();

  // Smooth circular alpha feathering so the plane vanishes cleanly into the page
  ctx.globalCompositeOperation = 'destination-in';
  const fade = ctx.createRadialGradient(c, c, size * 0.14, c, c, size * 0.49);
  fade.addColorStop(0, 'rgba(255, 255, 255, 1)');
  fade.addColorStop(0.48, 'rgba(255, 255, 255, 0.88)');
  fade.addColorStop(0.76, 'rgba(255, 255, 255, 0.34)');
  fade.addColorStop(1, 'rgba(255, 255, 255, 0)');
  ctx.fillStyle = fade;
  ctx.fillRect(0, 0, size, size);
  ctx.globalCompositeOperation = 'source-over';

  texture.needsUpdate = true;
  return texture;
}

/**
 * Clean architectural holographic orbital calibration gauge texture for reveal-holofield.
 * Rendered monochrome white/alpha so Three.js material color tints seamlessly for Dark & Light themes.
 */
export function createHoloReticleTexture(size = 1024) {
  const { ctx, texture } = createCanvasTex(size);
  const c = size / 2;
  ctx.clearRect(0, 0, size, size);

  const innerPool = ctx.createRadialGradient(c, c, size * 0.02, c, c, size * 0.28);
  innerPool.addColorStop(0, 'rgba(255, 255, 255, 0.34)');
  innerPool.addColorStop(0.55, 'rgba(255, 255, 255, 0.10)');
  innerPool.addColorStop(1, 'rgba(255, 255, 255, 0)');
  ctx.fillStyle = innerPool;
  ctx.fillRect(0, 0, size, size);

  ctx.strokeStyle = 'rgba(255, 255, 255, 0.44)';
  ctx.lineWidth = Math.max(1.5, size * 0.0020);
  ctx.beginPath();
  ctx.arc(c, c, size * 0.42, 0, Math.PI * 2);
  ctx.stroke();

  ctx.save();
  ctx.strokeStyle = 'rgba(255, 255, 255, 0.68)';
  ctx.lineWidth = Math.max(1.8, size * 0.0024);
  ctx.setLineDash([size * 0.028, size * 0.016]);
  ctx.beginPath();
  ctx.arc(c, c, size * 0.31, 0, Math.PI * 2);
  ctx.stroke();
  ctx.restore();

  ctx.strokeStyle = 'rgba(255, 255, 255, 0.56)';
  ctx.lineWidth = Math.max(1.5, size * 0.0018);
  ctx.beginPath();
  ctx.arc(c, c, size * 0.19, 0, Math.PI * 2);
  ctx.stroke();

  for (let deg = 0; deg < 360; deg += 10) {
    const a = (deg * Math.PI) / 180;
    const isCardinal = deg % 90 === 0;
    const isDiag = deg % 45 === 0;
    const r0 = size * (isCardinal ? 0.365 : isDiag ? 0.382 : 0.398);
    const r1 = size * 0.42;
    ctx.strokeStyle = isCardinal
      ? 'rgba(255, 255, 255, 0.92)'
      : isDiag
        ? 'rgba(255, 255, 255, 0.64)'
        : 'rgba(255, 255, 255, 0.32)';
    ctx.lineWidth = Math.max(1.2, size * (isCardinal ? 0.0032 : 0.0016));
    ctx.beginPath();
    ctx.moveTo(c + Math.cos(a) * r0, c + Math.sin(a) * r0);
    ctx.lineTo(c + Math.cos(a) * r1, c + Math.sin(a) * r1);
    ctx.stroke();
  }

  for (let i = 0; i < 4; i += 1) {
    const a = (i * Math.PI) / 2;
    const dx = Math.cos(a);
    const dy = Math.sin(a);
    const g = ctx.createLinearGradient(
      c + dx * size * 0.19,
      c + dy * size * 0.19,
      c + dx * size * 0.46,
      c + dy * size * 0.46,
    );
    g.addColorStop(0, 'rgba(255, 255, 255, 0.55)');
    g.addColorStop(1, 'rgba(255, 255, 255, 0)');
    ctx.strokeStyle = g;
    ctx.lineWidth = Math.max(1.4, size * 0.0018);
    ctx.beginPath();
    ctx.moveTo(c + dx * size * 0.19, c + dy * size * 0.19);
    ctx.lineTo(c + dx * size * 0.46, c + dy * size * 0.46);
    ctx.stroke();
  }

  texture.needsUpdate = true;
  return texture;
}
