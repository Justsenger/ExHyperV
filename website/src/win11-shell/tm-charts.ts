import type { VirtualHardwareSnapshot } from './virtual-hardware';

export interface PerfSeriesColor {
  stroke: string;
  fill: string;
  kernelFill?: string;
  secondaryStroke?: string;
  dot: string;
}

export const TM_SERIES_COLORS: Record<'cpu' | 'mem' | 'disk' | 'net' | 'gpu' | 'npu', PerfSeriesColor> = {
  cpu: {
    stroke: '#2e9ae0',
    fill: 'rgba(46, 154, 224, 0.22)',
    kernelFill: 'rgba(46, 154, 224, 0.42)',
    dot: '#2e9ae0',
  },
  mem: {
    stroke: '#70a5fa',
    fill: 'rgba(92, 158, 250, 0.22)',
    dot: '#5c9efa',
  },
  disk: {
    stroke: '#4db854',
    fill: 'rgba(77, 184, 84, 0.16)',
    secondaryStroke: '#66d66d',
    dot: '#4db854',
  },
  net: {
    stroke: '#c85a98',
    fill: 'rgba(200, 90, 152, 0.16)',
    secondaryStroke: '#e27cb6',
    dot: '#c85a98',
  },
  gpu: {
    stroke: '#3a8dde',
    fill: 'rgba(58, 141, 222, 0.2)',
    dot: '#3a8dde',
  },
  npu: {
    stroke: '#22c55e',
    fill: 'rgba(34, 197, 94, 0.18)',
    dot: '#22c55e',
  },
};

export function sampleToSvgPoints(pts: readonly number[], w: number, h: number, maxY = 100): string {
  const safeMax = Math.max(1e-6, maxY);
  const count = pts.length;
  if (count === 0) return `0,${h} ${w},${h}`;
  if (count === 1) {
    const y = h - Math.max(0, Math.min(1, pts[0] / safeMax)) * h;
    return `0,${y.toFixed(1)} ${w},${y.toFixed(1)}`;
  }
  return pts
    .map((val, i) => {
      const x = (i / (count - 1)) * w;
      const ratio = Math.max(0, Math.min(1, val / safeMax));
      const y = h - ratio * h;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');
}

export function renderPerfChartSvg(opts: {
  primaryPts: readonly number[];
  secondaryPts?: readonly number[];
  secondaryMode?: 'dashed-series' | 'kernel-overlay';
  maxY?: number;
  width?: number;
  height?: number;
  cols?: number;
  rows?: number;
  color: PerfSeriesColor;
  peakValue?: number;
  thumb?: boolean;
}): string {
  const w = opts.width ?? (opts.thumb ? 120 : 400);
  const h = opts.height ?? (opts.thumb ? 80 : 200);
  const maxY = Math.max(1e-6, opts.maxY ?? 100);
  const cols = opts.cols ?? (opts.thumb ? 5 : 10);
  const rows = opts.rows ?? (opts.thumb ? 3 : 8);

  const gridLines: string[] = [];
  if (!opts.thumb) {
    for (let i = 1; i < cols; i++) {
      const x = ((i / cols) * w).toFixed(1);
      gridLines.push(`<line x1="${x}" y1="0" x2="${x}" y2="${h}" />`);
    }
    for (let j = 1; j < rows; j++) {
      const y = ((j / rows) * h).toFixed(1);
      gridLines.push(`<line x1="0" y1="${y}" x2="${w}" y2="${y}" />`);
    }
  }

  const linePts = sampleToSvgPoints(opts.primaryPts, w, h, maxY);
  const areaPts = `0,${h} ${linePts} ${w},${h}`;

  let extraSvg = '';
  if (opts.secondaryPts && opts.secondaryPts.length > 0) {
    const secLine = sampleToSvgPoints(opts.secondaryPts, w, h, maxY);
    if (opts.secondaryMode === 'kernel-overlay') {
      const secArea = `0,${h} ${secLine} ${w},${h}`;
      extraSvg = `
        <polygon points="${secArea}" fill="${opts.color.kernelFill ?? opts.color.fill}" />
        <polyline points="${secLine}" fill="none" stroke="${opts.color.stroke}" stroke-width="1.35" stroke-dasharray="3 2" vector-effect="non-scaling-stroke" />
      `;
    } else {
      extraSvg = `
        <polyline points="${secLine}" fill="none" stroke="${opts.color.secondaryStroke ?? opts.color.stroke}" stroke-width="1.5" stroke-dasharray="4 3" vector-effect="non-scaling-stroke" />
      `;
    }
  }

  let peakLineSvg = '';
  if (typeof opts.peakValue === 'number' && opts.peakValue > 0 && !opts.thumb) {
    const ratio = Math.max(0.02, Math.min(0.98, opts.peakValue / maxY));
    const py = (h - ratio * h).toFixed(1);
    peakLineSvg = `<line x1="0" y1="${py}" x2="${w}" y2="${py}" stroke="${opts.color.stroke}" stroke-opacity="0.55" stroke-width="1" stroke-dasharray="2 3" vector-effect="non-scaling-stroke" />`;
  }

  return `
    <svg class="w11-tm-svg" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true">
      <g class="w11-tm-grid-lines">${gridLines.join('')}</g>
      ${peakLineSvg}
      <polygon points="${areaPts}" fill="${opts.color.fill}" />
      <polyline points="${linePts}" fill="none" stroke="${opts.color.stroke}" stroke-width="1" vector-effect="non-scaling-stroke" />
      ${extraSvg}
    </svg>
  `;
}

export function computeDynamicScaleMax(peakVal: number, steps: readonly number[]): number {
  for (const s of steps) {
    if (peakVal <= s * 0.88) return s;
  }
  return steps[steps.length - 1];
}

export function formatThroughputKb(kbPerSec: number): string {
  if (kbPerSec >= 1024) {
    return `${(kbPerSec / 1024).toFixed(1)} MB/秒`;
  }
  return `${Math.round(kbPerSec)} KB/秒`;
}

export function formatNetRateKbps(kbps: number): string {
  if (kbps >= 1000) {
    return `${(kbps / 1000).toFixed(1)} Mbps`;
  }
  return `${Math.round(kbps)} Kbps`;
}

export function renderMemoryCompositionBar(hw: VirtualHardwareSnapshot): string {
  const usableMb = Math.max(1024, Math.round(hw.memory.usableGb * 1024));
  const usedMb = Math.max(512, Math.round(hw.memory.usedGb * 1024));
  const compMb = Math.max(32, Math.round(hw.memory.compressedMb));
  const inUsePureMb = Math.max(256, usedMb - compMb);
  const modMb = Math.max(16, Math.round(usableMb * 0.012));
  const availMb = Math.max(64, Math.round(hw.memory.availGb * 1024));
  const standbyMb = Math.max(64, Math.round(availMb * 0.62));
  const freeMb = Math.max(32, availMb - standbyMb);

  const usedPct = Math.max(6, Math.min(78, hw.memory.usagePct));
  const modPct = Math.max(1.5, Math.min(4, (modMb / usableMb) * 100));
  const standbyPct = Math.max(6, Math.min(82 - usedPct, (standbyMb / usableMb) * 100));

  const savedEst = Math.round(compMb * 1.48);
  const rawEst = Math.round(compMb * 2.48);

  const usedBody =
    `使用中(${inUsePureMb.toLocaleString('en-US')} MB)\n由进程、驱动程序或操作系统使用的内存\n\n` +
    `使用中，已压缩(${compMb} MB)\n压缩内存存储了估计 ${rawEst} MB 的数据，为系统节省了 ${savedEst} MB 的内存`;
  const modBody = `已修改(${modMb} MB)\n必须先将其内容写入磁盘，然后才可用于其他用途的内存`;
  const standbyBody = `备用(${standbyMb.toLocaleString('en-US')} MB)\n包含未频繁使用的缓存数据和代码的内存`;
  const freeBody = `可用(${freeMb.toLocaleString('en-US')} MB)\n包含当前未由进程、驱动程序或操作系统使用的内存，或包含无需先写入磁盘即可利用的已归零内存`;

  return `
    <div class="w11-tm-mem-bar" role="img" aria-label="内存组合">
      <div class="w11-tm-mem-used" style="width:${usedPct.toFixed(1)}%" data-tm-tip="内存组合" data-tm-tip-body="${usedBody.replace(/"/g, '&quot;')}"></div>
      <div class="w11-tm-mem-compressed" data-tm-tip="已压缩内存" data-tm-tip-body="${usedBody.replace(/"/g, '&quot;')}"></div>
      <div class="w11-tm-mem-modified" style="width:${modPct.toFixed(1)}%" data-tm-tip="已修改" data-tm-tip-body="${modBody.replace(/"/g, '&quot;')}"></div>
      <div class="w11-tm-mem-standby" style="width:${standbyPct.toFixed(1)}%" data-tm-tip="备用" data-tm-tip-body="${standbyBody.replace(/"/g, '&quot;')}"></div>
      <div class="w11-tm-mem-free" style="flex:1" data-tm-tip="可用" data-tm-tip-body="${freeBody.replace(/"/g, '&quot;')}"></div>
    </div>
  `;
}
