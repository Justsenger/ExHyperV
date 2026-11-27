export interface CandidateItem {
  attr: string;
  value: string;
  label: string;
  icon?: string;
  active?: boolean;
}

export interface CandidateSection {
  id: string;
  subhead: string;
  scope?: string;
  columns?: number;
  items: CandidateItem[];
}

export interface CandidatePanelConfig {
  title: string;
  sections: CandidateSection[];
  mappings: [string, string][];
}

export const HOST_CANDIDATE_PANEL: CandidatePanelConfig = {
  title: '总览设计候选',
  sections: [
    {
      id: 'dcpHostCpuDense',
      subhead: '高核数排版对比 (64+ 线程)',
      items: [
        { attr: 'data-host-cpu-dense', value: 'paged-8x4', label: '1. 8×4 翻页（保持圆环清晰）', active: true },
        { attr: 'data-host-cpu-dense', value: 'compact-8x8', label: '2. 8×8 压缩全览（单页收紧）' }
      ]
    },
    {
      id: 'dcpHostThreads',
      subhead: 'CPU 逻辑线程',
      columns: 2,
      items: [
        { attr: 'data-host-mock-threads', value: '4', label: '4 线程' },
        { attr: 'data-host-mock-threads', value: '8', label: '8 线程' },
        { attr: 'data-host-mock-threads', value: '12', label: '12 线程', active: true },
        { attr: 'data-host-mock-threads', value: '16', label: '16 线程' },
        { attr: 'data-host-mock-threads', value: '24', label: '24 线程' },
        { attr: 'data-host-mock-threads', value: '32', label: '32 线程' },
        { attr: 'data-host-mock-threads', value: '64', label: '64 线程' },
        { attr: 'data-host-mock-threads', value: '128', label: '128 线程' }
      ]
    }
  ],
  mappings: [
    ['data-host-cpu-dense', 'cpuDenseMode'],
    ['data-host-mock-threads', 'mockThreads']
  ]
};
