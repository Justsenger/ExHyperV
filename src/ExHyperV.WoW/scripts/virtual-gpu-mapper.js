/**
 * 将虚拟硬件层快照中的 GPU 列表转换为宿主/虚拟机通用的标准设备结构。
 */
export function buildVirtualGpuEntries(hw) {
  const list = Array.isArray(hw?.gpus) ? hw.gpus : [];
  return list.map((g, idx) => {
    const devHex = g.pciDeviceIdHex ? g.pciDeviceIdHex.replace(/^0x/i, '').toUpperCase() : '';
    const archSuffix = g.architecture ? ` (${g.architecture})` : '';
    const instanceId = devHex
      ? `PCI\\DEV_${devHex}${archSuffix}`
      : g.tooltip || (g.isSoftwareRenderer ? 'ROOT\\BASICDISPLAY\\0000' : `WEBGL\\GPU_${idx}`);
    const partitionPath = g.isSoftwareRenderer
      ? ''
      : devHex
        ? `\\\\?\\PCI#DEV_${devHex}#{064092b3-625e-43bf-9eb5-dc845897dd95}\\GPUPARAV`
        : `\\\\?\\DISPLAY#WEBGL_GPU_${idx}#{064092b3-625e-43bf-9eb5-dc845897dd95}\\GPUPARAV`;
    const vendor = g.vendor || 'GPU';
    return {
      id: `host-gpu-${idx + 1}`,
      hostId: g.id || `gpu-${idx}`,
      name: g.name || 'GPU',
      manu: vendor,
      vendor,
      memoryText: '浏览器受限（未知）',
      driverVersion: g.webglVersion || (hw?.probed ? 'WebGL' : '---'),
      instanceId,
      partitionPath,
      partitionable: !g.isSoftwareRenderer,
      unpartitionableReason: g.isSoftwareRenderer ? '当前检测到软件渲染/基础显示驱动，不支持 GPU-PV 分区' : '',
      usagePct: typeof g.usagePct === 'number' ? g.usagePct : 0,
      pts3d: Array.isArray(g.pts3d) && g.pts3d.length > 1 ? g.pts3d : [0, 0, 0, 0, 0, 0],
      ptsCopy: Array.isArray(g.ptsCopy) && g.ptsCopy.length > 1 ? g.ptsCopy : [0, 0, 0, 0, 0, 0],
      ptsEnc: Array.isArray(g.ptsEnc) && g.ptsEnc.length > 1 ? g.ptsEnc : [0, 0, 0, 0, 0, 0],
      ptsDec: Array.isArray(g.ptsDec) && g.ptsDec.length > 1 ? g.ptsDec : [0, 0, 0, 0, 0, 0],
      tooltip: g.tooltip || ''
    };
  });
}
