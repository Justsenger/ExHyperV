import research from './gpu-pv-driver-research.json';

export const researchNavItems = [
  { slug: '', title: '总览', href: '/docs/gpu-driver-research/' },
  { slug: 'nvidia', title: 'NVIDIA', href: '/docs/gpu-driver-research/nvidia/' },
  { slug: 'intel', title: 'Intel', href: '/docs/gpu-driver-research/intel/' },
  { slug: 'amd', title: 'AMD', href: '/docs/gpu-driver-research/amd/' },
  { slug: 'qualcomm', title: 'Qualcomm', href: '/docs/gpu-driver-research/qualcomm/' },
  { slug: 'packages', title: `驱动包索引（${research.packages.length}）`, href: '/docs/gpu-driver-research/packages/' },
  { slug: 'appendix', title: '附录与出处', href: '/docs/gpu-driver-research/appendix/' },
];

export const researchSectionsMap = {
  nvidia: {
    heading: 'NVIDIA',
    lead: 'GeForce、Studio、Quadro / RTX Enterprise 与 Tesla 四条驱动线的门槛依据，以及 Studio 471.41 的 ELF 清单。',
    vendor: 'nvidia',
    sectionIds: ['nvidia-evolution'],
  },
  intel: {
    heading: 'Intel',
    lead: '第 6 代酷睿及以上核显、Arc 独显与 DCH 三包关联记录。',
    vendor: 'intel',
    sectionIds: ['intel-evolution', 'dch-deploy'],
  },
  amd: {
    heading: 'AMD',
    lead: 'Radeon、Radeon PRO、ROCm 计算门槛与 13 份早期安装包目录前缀。',
    vendor: 'amd',
    sectionIds: ['amd-evolution', 'amd-elf', 'amd-prefix'],
  },
  qualcomm: {
    heading: 'Qualcomm',
    lead: '骁龙 8cx Gen 1 至 Gen 3、X1、X2 的多平台与本机分区记录，以及 SQ1 的说明。',
    vendor: 'qualcomm',
    sectionIds: ['qualcomm-evolution'],
  },
  packages: {
    heading: '驱动包索引',
    lead: '214 个完整官方安装包汇总，支持按厂商、类型、文件名与 SHA-256 搜索。',
    sectionIds: ['sampling', 'sampling-2', 'sampling-3'],
  },
  appendix: {
    heading: '附录与出处',
    lead: '四厂部署清单推演、脚本自检结果、164 条公开 DxDiag 帖与 522 条原始参考来源。',
    sectionIds: ['dch-deploy-2', 'deployment-examples', 'tool-tests', 'historical-dxdiag', 'external-sources'],
  },
};

export function pickDriverLines(vendor) {
  return (research.driver_lines || []).filter((line) => line.vendor === vendor);
}

export function pickNarrativeSections(ids) {
  const byId = new Map((research.narrative_sections || []).map((section) => [section.id, section]));
  return ids.map((id) => byId.get(id)).filter(Boolean);
}

export function isHttpUrl(value) {
  return typeof value === 'string' && /^https?:\/\/\S+$/i.test(value.trim());
}
