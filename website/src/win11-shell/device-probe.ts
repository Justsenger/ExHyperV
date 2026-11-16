import { probeHardware, type HardwareProfile } from 'hw-profiler';

export interface StorageProbeInfo {
  supported: boolean;
  isSecureContext: boolean;
  quotaBytes: number | null;
  usageBytes: number | null;
  quotaGiB: number | null;
  quotaGB: number | null;
  usageMB: number | null;
  usageDetails: Record<string, number> | null;
  persisted: boolean | null;
  opfsSupported: boolean;
  opfsReady: boolean | null;
  fsAccessSupported: boolean;
  error?: string;
}

export interface DeviceProbeReport {
  timestamp: string;
  context: {
    origin: string;
    isSecureContext: boolean;
    language: string;
    languages: readonly string[];
    timeZone: string;
    userAgent: string;
    platformLegacy: string;
    uaData?: Record<string, unknown> | string;
  };
  cpu: {
    hardwareConcurrency: number | null;
    computePressureSupport: boolean;
    computePressureState?: string;
  };
  memory: {
    deviceMemoryGiB: number | string;
    jsHeapUsedMB?: number;
    jsHeapTotalMB?: number;
    jsHeapLimitMB?: number;
  };
  gpu: {
    webglVendor?: string;
    webglRenderer?: string;
    webglVersion?: string;
    webglMaxTextureSize?: number;
    webgpuSupported: boolean;
    webgpuAdapterInfo?: Record<string, unknown> | string;
    webgpuLimits?: Record<string, unknown>;
  };
  display: {
    screenResolution: string;
    availableScreen: string;
    viewport: string;
    devicePixelRatio: number;
    colorDepth: number;
    orientation: string;
    hdrSupported: boolean;
    p3GamutSupported: boolean;
    maxTouchPoints: number;
    coarsePointer: boolean;
  };
  battery: Record<string, unknown> | string;
  network: {
    onLine: boolean;
    connection?: Record<string, unknown> | string;
    sessionResourcesCount: number;
    sessionTransferKB: number;
  };
  storage: StorageProbeInfo;
  peripheralsAndApis: Record<string, unknown>;
  hardwareProfile?: HardwareProfile;
}

/**
 * Probe browser storage quota, current origin usage, persistence status, and OPFS availability.
 */
export async function collectStorageProbeInfo(): Promise<StorageProbeInfo> {
  const opfsSupported = typeof navigator.storage?.getDirectory === 'function';
  const fsAccessSupported = 'showDirectoryPicker' in window || 'showOpenFilePicker' in window;
  const info: StorageProbeInfo = {
    supported: Boolean(navigator.storage?.estimate),
    isSecureContext: window.isSecureContext,
    quotaBytes: null,
    usageBytes: null,
    quotaGiB: null,
    quotaGB: null,
    usageMB: null,
    usageDetails: null,
    persisted: null,
    opfsSupported,
    opfsReady: opfsSupported ? null : false,
    fsAccessSupported,
  };

  if (navigator.storage?.estimate) {
    try {
      const est = (await withTimeout(navigator.storage.estimate(), 800, null)) as
        | (StorageEstimate & { usageDetails?: Record<string, number> })
        | null;
      if (est) {
        info.quotaBytes = typeof est.quota === 'number' ? est.quota : null;
        info.usageBytes = typeof est.usage === 'number' ? est.usage : null;
        info.quotaGiB =
          typeof est.quota === 'number' ? Math.round((est.quota / 1024 ** 3) * 100) / 100 : null;
        info.quotaGB =
          typeof est.quota === 'number' ? Math.round((est.quota / 1000 ** 3) * 100) / 100 : null;
        info.usageMB = typeof est.usage === 'number' ? roundMB(est.usage) : null;
        info.usageDetails =
          est.usageDetails && typeof est.usageDetails === 'object' ? est.usageDetails : null;
      }
    } catch (err) {
      info.error = String(err);
    }
  } else if (!window.isSecureContext) {
    info.error = '当前非 HTTPS/localhost 安全上下文，浏览器禁用 navigator.storage';
  } else {
    info.error = '当前浏览器不支持 navigator.storage.estimate()';
  }

  if (navigator.storage?.persisted) {
    try {
      info.persisted = await withTimeout(navigator.storage.persisted(), 500, null);
    } catch {
      info.persisted = null;
    }
  }

  if (navigator.storage?.getDirectory) {
    try {
      info.opfsReady = await withTimeout(
        navigator.storage.getDirectory().then(
          () => true,
          () => false,
        ),
        500,
        null,
      );
    } catch {
      info.opfsReady = false;
    }
  }

  return info;
}

function roundMB(bytes: number): number {
  return Math.round((bytes / (1024 * 1024)) * 100) / 100;
}

function withTimeout<T>(promise: Promise<T>, ms: number, fallback: T): Promise<T> {
  return Promise.race([
    promise,
    new Promise<T>((resolve) => window.setTimeout(() => resolve(fallback), ms)),
  ]);
}

export async function collectDeviceHardwareReport(): Promise<DeviceProbeReport> {
  const hardwareProfilePromise = probeHardware({ enableWebNNProbe: true, confidentSingle: true }).catch(
    () => undefined,
  );
  const nav = navigator as Navigator & {
    deviceMemory?: number;
    userAgentData?: {
      platform?: string;
      mobile?: boolean;
      brands?: unknown[];
      getHighEntropyValues?: (hints: string[]) => Promise<Record<string, unknown>>;
    };
    getBattery?: () => Promise<{
      charging: boolean;
      level: number;
      chargingTime: number;
      dischargingTime: number;
    }>;
    connection?: {
      type?: string;
      effectiveType?: string;
      downlink?: number;
      rtt?: number;
      saveData?: boolean;
    };
    gpu?: {
      requestAdapter?: () => Promise<{
        info?: Record<string, unknown>;
        limits?: { maxBufferSize?: number; maxTextureDimension2D?: number };
        features?: Set<string>;
      } | null>;
    };
  };

  // 1. User-Agent Client Hints
  let uaData: Record<string, unknown> | string = '不支持 navigator.userAgentData';
  if (nav.userAgentData) {
    try {
      const base = {
        platform: nav.userAgentData.platform,
        mobile: nav.userAgentData.mobile,
        brands: nav.userAgentData.brands,
      };
      if (nav.userAgentData.getHighEntropyValues) {
        const high = await withTimeout(
          nav.userAgentData.getHighEntropyValues([
            'architecture',
            'bitness',
            'model',
            'platformVersion',
            'uaFullVersion',
            'wow64',
          ]),
          800,
          {},
        );
        uaData = { ...base, ...high };
      } else {
        uaData = base;
      }
    } catch (err) {
      uaData = `读取失败: ${String(err)}`;
    }
  }

  // 2. Compute Pressure
  const PressureObs = (window as unknown as { PressureObserver?: unknown }).PressureObserver as
    | (new (cb: (records: Array<{ state: string }>) => void) => {
        observe: (source: string) => Promise<void>;
        disconnect: () => void;
      })
    | undefined;
  let computePressureState: string | undefined;
  if (PressureObs) {
    try {
      computePressureState = await withTimeout(
        new Promise<string>((resolve) => {
          const obs = new PressureObs((records) => {
            const latest = records[records.length - 1];
            obs.disconnect();
            resolve(latest?.state ?? 'unknown');
          });
          obs.observe('cpu').catch((e) => resolve(`不可用 (${String(e)})`));
        }),
        700,
        '超时未返回（需安全上下文/权限或暂无采样）',
      );
    } catch (err) {
      computePressureState = `不可用: ${String(err)}`;
    }
  }

  // 3. Memory
  const perfMem = (performance as Performance & {
    memory?: { usedJSHeapSize: number; totalJSHeapSize: number; jsHeapSizeLimit: number };
  }).memory;

  // 4. GPU (WebGL + WebGPU)
  const gpuInfo: DeviceProbeReport['gpu'] = {
    webgpuSupported: Boolean(nav.gpu?.requestAdapter),
  };
  try {
    const canvas = document.createElement('canvas');
    const gl =
      (canvas.getContext('webgl2') as WebGL2RenderingContext | null) ||
      (canvas.getContext('webgl') as WebGLRenderingContext | null);
    if (gl) {
      gpuInfo.webglVersion = String(gl.getParameter(gl.VERSION) ?? '');
      gpuInfo.webglMaxTextureSize = Number(gl.getParameter(gl.MAX_TEXTURE_SIZE) ?? 0);
      const dbg = gl.getExtension('WEBGL_debug_renderer_info');
      if (dbg) {
        gpuInfo.webglVendor = String(gl.getParameter(dbg.UNMASKED_VENDOR_WEBGL) ?? '');
        gpuInfo.webglRenderer = String(gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) ?? '');
      } else {
        gpuInfo.webglVendor = String(gl.getParameter(gl.VENDOR) ?? '');
        gpuInfo.webglRenderer = String(gl.getParameter(gl.RENDERER) ?? '');
      }
    }
  } catch (err) {
    gpuInfo.webglRenderer = `WebGL 探测失败: ${String(err)}`;
  }

  if (nav.gpu?.requestAdapter) {
    try {
      const adapter = await withTimeout(nav.gpu.requestAdapter(), 1000, null);
      if (adapter) {
        const info = adapter.info ?? {};
        gpuInfo.webgpuAdapterInfo = {
          vendor: info.vendor ?? '',
          architecture: info.architecture ?? '',
          device: info.device ?? '',
          description: info.description ?? '',
        };
        gpuInfo.webgpuLimits = {
          maxBufferSizeMB: adapter.limits?.maxBufferSize ? roundMB(adapter.limits.maxBufferSize) : undefined,
          maxTextureDimension2D: adapter.limits?.maxTextureDimension2D,
          featuresCount: adapter.features ? adapter.features.size : undefined,
        };
      } else {
        gpuInfo.webgpuAdapterInfo = '未获取到 Adapter（可能非安全上下文或被禁用）';
      }
    } catch (err) {
      gpuInfo.webgpuAdapterInfo = `WebGPU 探测失败: ${String(err)}`;
    }
  }

  // 5. Battery
  let battery: Record<string, unknown> | string = '不支持 navigator.getBattery()';
  if (nav.getBattery) {
    try {
      const b = await withTimeout(nav.getBattery(), 800, null);
      if (b) {
        battery = {
          levelPercent: Math.round(b.level * 100),
          charging: b.charging,
          chargingTimeSec: Number.isFinite(b.chargingTime) ? b.chargingTime : 'Infinity',
          dischargingTimeSec: Number.isFinite(b.dischargingTime) ? b.dischargingTime : 'Infinity',
        };
      }
    } catch (err) {
      battery = `读取失败: ${String(err)}`;
    }
  }

  // 6. Network & Session Resource Timing
  const resEntries = performance.getEntriesByType('resource') as PerformanceResourceTiming[];
  const totalTransfer = resEntries.reduce((sum, r) => sum + (r.transferSize || 0), 0);

  // 7. Storage
  const storage = await collectStorageProbeInfo();

  // 8. Peripherals (no permission prompts)
  let mediaDevicesSummary: Record<string, number> | string = '不支持';
  if (nav.mediaDevices?.enumerateDevices) {
    try {
      const list = await withTimeout(nav.mediaDevices.enumerateDevices(), 600, []);
      mediaDevicesSummary = {
        audioinput: list.filter((d) => d.kind === 'audioinput').length,
        videoinput: list.filter((d) => d.kind === 'videoinput').length,
        audiooutput: list.filter((d) => d.kind === 'audiooutput').length,
      };
    } catch {
      mediaDevicesSummary = '读取失败';
    }
  }

  const gamepads = nav.getGamepads ? Array.from(nav.getGamepads()).filter(Boolean).map((g) => g?.id) : [];
  const hardwareProfile = await hardwareProfilePromise;

  return {
    timestamp: new Date().toLocaleString(),
    context: {
      origin: window.location.origin,
      isSecureContext: window.isSecureContext,
      language: nav.language,
      languages: nav.languages,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      userAgent: nav.userAgent,
      platformLegacy: nav.platform,
      uaData,
    },
    cpu: {
      hardwareConcurrency: nav.hardwareConcurrency ?? null,
      computePressureSupport: Boolean(PressureObs),
      computePressureState,
    },
    memory: {
      deviceMemoryGiB: nav.deviceMemory ?? '不支持（需 Chromium + 安全上下文）',
      jsHeapUsedMB: perfMem ? roundMB(perfMem.usedJSHeapSize) : undefined,
      jsHeapTotalMB: perfMem ? roundMB(perfMem.totalJSHeapSize) : undefined,
      jsHeapLimitMB: perfMem ? roundMB(perfMem.jsHeapSizeLimit) : undefined,
    },
    gpu: gpuInfo,
    display: {
      screenResolution: `${window.screen.width} × ${window.screen.height}`,
      availableScreen: `${window.screen.availWidth} × ${window.screen.availHeight}`,
      viewport: `${window.innerWidth} × ${window.innerHeight}`,
      devicePixelRatio: window.devicePixelRatio,
      colorDepth: window.screen.colorDepth,
      orientation: window.screen.orientation?.type ?? 'unknown',
      hdrSupported: window.matchMedia('(dynamic-range: high)').matches,
      p3GamutSupported: window.matchMedia('(color-gamut: p3)').matches,
      maxTouchPoints: nav.maxTouchPoints ?? 0,
      coarsePointer: window.matchMedia('(pointer: coarse)').matches,
    },
    battery,
    network: {
      onLine: nav.onLine,
      connection: nav.connection
        ? {
            type: nav.connection.type ?? '未暴露',
            effectiveType: nav.connection.effectiveType,
            downlinkMbps: nav.connection.downlink,
            rttMs: nav.connection.rtt,
            saveData: nav.connection.saveData,
          }
        : '不支持 navigator.connection',
      sessionResourcesCount: resEntries.length,
      sessionTransferKB: Math.round((totalTransfer / 1024) * 10) / 10,
    },
    storage,
    peripheralsAndApis: {
      mediaDevices: mediaDevicesSummary,
      gamepads,
      deviceOrientationSupported: 'DeviceOrientationEvent' in window,
      deviceMotionSupported: 'DeviceMotionEvent' in window,
      webBluetoothSupported: 'bluetooth' in nav,
      webUsbSupported: 'usb' in nav,
      webHidSupported: 'hid' in nav,
      wakeLockSupported: 'wakeLock' in nav,
    },
    hardwareProfile,
  };
}
