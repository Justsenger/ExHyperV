import {
  formatCpuTime,
  formatMemKb,
  formatMemMb,
  getHeatClass,
  type TmAppHistoryRow,
  type TmProcessNode,
  type TmResourceDisplayMode,
  type TmServiceRow,
  type TmStartupRow,
  type TmTabId,
} from './tm-data';
import type { VirtualHardwareSnapshot } from './virtual-hardware';

export type TmThemePref = 'system' | 'dark' | 'light';
export type TmUpdateSpeed = 'high' | 'normal' | 'low' | 'paused';

export interface TmSettingsState {
  themePref: TmThemePref;
  defaultTab: Exclude<TmTabId, 'settings'>;
  updateSpeed: TmUpdateSpeed;
  winMgmtExpanded: boolean;
  otherOptsExpanded: boolean;
  alwaysOnTop: boolean;
  minimizeOnUse: boolean;
  hideWhenMinimized: boolean;
  showFullAccountName: boolean;
  showAllHistory: boolean;
  confirmEfficiencyMode: boolean;
}

function esc(v: string): string {
  return v.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

export function matchesSearch(query: string, ...fields: Array<string | number | undefined | null>): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  return fields.some((f) => (f !== undefined && f !== null ? String(f).toLowerCase().includes(q) : false));
}

export function renderHistoryPage(opts: {
  rows: readonly TmAppHistoryRow[];
  selectedId: string | null;
  search: string;
  showAllHistory: boolean;
  historySince: string;
}): string {
  const filtered = opts.rows.filter(
    (r) => (opts.showAllHistory || !r.isSystemProcess) && matchesSearch(opts.search, r.name, r.publisher),
  );
  return `
    <div class="w11-tm-subbar is-stacked">
      <span>自 ${esc(opts.historySince)} 以来，当前用户和系统账户的资源使用情况。</span>
      <button type="button" class="w11-tm-link-btn" data-hist-clear>删除使用情况历史记录</button>
    </div>
    <div class="w11-tm-proc-wrap">
      <table class="w11-tm-proc-table is-bounded">
        <thead>
          <tr>
            <th style="width:280px">名称</th>
            <th class="is-num" style="width:128px">CPU 时间</th>
            <th class="is-num" style="width:128px">网络</th>
            <th class="is-num" style="width:152px">按流量计费的网络</th>
            <th class="is-num" style="width:116px">磁贴更新</th>
            <th class="is-num" style="width:96px">通知</th>
            <th class="is-spacer" aria-hidden="true"></th>
          </tr>
        </thead>
        <tbody>
          ${
            filtered.length === 0
              ? `<tr><td colspan="7" class="w11-tm-empty">没有匹配的应用历史记录</td></tr>`
              : filtered
                  .map((r) => {
                    const sel = opts.selectedId === r.id ? 'is-selected' : '';
                    return `
                      <tr class="w11-tm-proc-row ${sel}" data-row-id="${esc(r.id)}">
                        <td class="w11-tm-name-cell"><span class="w11-tm-appdot is-app"></span><span class="w11-tm-ellipsis">${esc(r.name)}</span></td>
                        <td class="w11-tm-num-cell ${getHeatClass(r.cpuTimeSec / 30)}">${formatCpuTime(r.cpuTimeSec)}</td>
                        <td class="w11-tm-num-cell ${getHeatClass(r.netMb / 25)}">${r.netMb.toFixed(1)} MB</td>
                        <td class="w11-tm-num-cell ${getHeatClass(r.meteredNetMb / 10)}">${r.meteredNetMb} MB</td>
                        <td class="w11-tm-num-cell ${getHeatClass(r.tileUpdatesMb / 5)}">${r.tileUpdatesMb} MB</td>
                        <td class="w11-tm-num-cell ${getHeatClass(r.notifications / 4)}">${r.notifications}</td>
                        <td class="w11-tm-spacer-cell"></td>
                      </tr>
                    `;
                  })
                  .join('')
          }
        </tbody>
      </table>
    </div>
  `;
}

export function renderStartupPage(opts: {
  rows: readonly TmStartupRow[];
  expandedIds: Set<string>;
  selectedId: string | null;
  search: string;
}): string {
  const filtered = opts.rows.filter(
    (r) =>
      matchesSearch(opts.search, r.name, r.publisher, r.status) ||
      Boolean(r.children?.some((c) => matchesSearch(opts.search, c.name, c.status))),
  );
  const rowsHtml: string[] = [];
  for (const r of filtered) {
    const hasChildren = Boolean(r.children && r.children.length > 0);
    const expanded = opts.expandedIds.has(r.id);
    const sel = opts.selectedId === r.id ? 'is-selected' : '';
    const countBadge = hasChildren ? ` (${r.children!.length})` : '';
    rowsHtml.push(`
      <tr class="w11-tm-proc-row ${sel}" data-row-id="${esc(r.id)}">
        <td class="w11-tm-name-cell">
          ${
            hasChildren
              ? `<button type="button" class="w11-tm-exp-btn" data-startup-toggle="${esc(r.id)}" aria-label="展开或折叠">${expanded ? '⌄' : '›'}</button>`
              : '<span class="w11-tm-exp-pad"></span>'
          }
          <span class="w11-tm-appdot is-startup"></span>
          <span class="w11-tm-ellipsis">${esc(r.name)}${countBadge}</span>
        </td>
        <td><span class="w11-tm-ellipsis">${esc(r.publisher)}</span></td>
        <td>${esc(r.status)}</td>
        <td>${esc(r.impact)}</td>
        <td class="w11-tm-spacer-cell"></td>
      </tr>
    `);
    if (hasChildren && expanded) {
      for (const c of r.children!) {
        const cSel = opts.selectedId === c.id ? 'is-selected' : '';
        rowsHtml.push(`
          <tr class="w11-tm-proc-row is-child ${cSel}" data-row-id="${esc(c.id)}">
            <td class="w11-tm-name-cell"><span class="w11-tm-exp-pad"></span><span class="w11-tm-exp-pad"></span><span class="w11-tm-appdot is-child"></span><span class="w11-tm-ellipsis">${esc(c.name)}</span></td>
            <td class="w11-tm-muted-text">${esc(r.publisher)}</td>
            <td>${esc(c.status)}</td>
            <td>${esc(c.impact)}</td>
            <td class="w11-tm-spacer-cell"></td>
          </tr>
        `);
      }
    }
  }
  return `
    <div class="w11-tm-subbar is-right">
      <span>上次 BIOS 所用时间: 0.3 秒</span>
    </div>
    <div class="w11-tm-proc-wrap">
      <table class="w11-tm-proc-table is-bounded">
        <thead>
          <tr>
            <th style="width:290px">名称</th>
            <th style="width:190px">发布者</th>
            <th style="width:110px">状态</th>
            <th style="width:110px">启动影响</th>
            <th class="is-spacer" aria-hidden="true"></th>
          </tr>
        </thead>
        <tbody>
          ${rowsHtml.length > 0 ? rowsHtml.join('') : `<tr><td colspan="5" class="w11-tm-empty">没有匹配的启动应用</td></tr>`}
        </tbody>
      </table>
    </div>
  `;
}

export function renderUsersPage(opts: {
  processes: readonly TmProcessNode[];
  hw: VirtualHardwareSnapshot;
  expanded: boolean;
  selectedId: string | null;
  search: string;
  showFullAccountName: boolean;
  memMode: TmResourceDisplayMode;
}): string {
  const userName = opts.showFullAccountName ? 'MOBAN11\\admin' : 'admin';
  const userProcs = opts.processes.filter((p) => p.user === 'admin' && matchesSearch(opts.search, p.name, p.pid));
  const totalMemMb = opts.hw.memory.usableGb * 1024;
  const sumCpu = Math.min(99, userProcs.reduce((a, b) => a + b.cpu, 0) + Math.max(0.8, opts.hw.cpu.usagePct * 0.28));
  const sumMemMb = userProcs.reduce((a, b) => a + b.memMb, 0) + 920;
  const userVisible = matchesSearch(opts.search, userName, '活跃') || userProcs.length > 0;

  return `
    <div class="w11-tm-proc-wrap">
      <table class="w11-tm-proc-table is-bounded">
        <thead>
          <tr>
            <th style="width:260px">用户</th>
            <th style="width:92px">状态</th>
            <th class="is-num" style="width:92px"><div>${opts.hw.cpu.usagePct}%</div>CPU</th>
            <th class="is-num" style="width:104px"><div>${opts.hw.memory.usagePct}%</div>内存</th>
            <th class="is-num" style="width:96px"><div>${opts.hw.storage.activePct}%</div>磁盘</th>
            <th class="is-num" style="width:96px"><div>0%</div>网络</th>
            <th class="is-spacer" aria-hidden="true"></th>
          </tr>
        </thead>
        <tbody>
          ${
            !userVisible
              ? `<tr><td colspan="7" class="w11-tm-empty">没有匹配的用户或进程</td></tr>`
              : `
                <tr class="w11-tm-proc-row ${opts.selectedId === 'user-admin' ? 'is-selected' : ''}" data-row-id="user-admin">
                  <td class="w11-tm-name-cell">
                    <button type="button" class="w11-tm-exp-btn" data-user-toggle="admin" aria-label="展开或折叠">${opts.expanded ? '⌄' : '›'}</button>
                    <span class="w11-tm-user-badge">A</span>
                    <span class="w11-tm-ellipsis">${esc(userName)} (${userProcs.length})</span>
                  </td>
                  <td>活跃</td>
                  <td class="w11-tm-num-cell ${getHeatClass(sumCpu / 100)}">${sumCpu.toFixed(1)}%</td>
                  <td class="w11-tm-num-cell ${getHeatClass(sumMemMb / totalMemMb)}">${formatMemMb(sumMemMb, opts.memMode, totalMemMb)}</td>
                  <td class="w11-tm-num-cell">0.1 MB/秒</td>
                  <td class="w11-tm-num-cell">0 Mbps</td>
                  <td class="w11-tm-spacer-cell"></td>
                </tr>
                ${
                  opts.expanded
                    ? userProcs
                        .map(
                          (p) => `
                            <tr class="w11-tm-proc-row is-child ${opts.selectedId === p.id ? 'is-selected' : ''}" data-row-id="${esc(p.id)}">
                              <td class="w11-tm-name-cell"><span class="w11-tm-exp-pad"></span><span class="w11-tm-exp-pad"></span><span class="w11-tm-appdot"></span><span class="w11-tm-ellipsis">${esc(p.name)}</span></td>
                              <td class="w11-tm-muted-text">${p.status === 'suspended' ? '已挂起' : '正在运行'}</td>
                              <td class="w11-tm-num-cell ${getHeatClass(p.cpu / 18)}">${p.cpu.toFixed(1)}%</td>
                              <td class="w11-tm-num-cell ${getHeatClass(p.memMb / 120)}">${formatMemMb(p.memMb, opts.memMode, totalMemMb)}</td>
                              <td class="w11-tm-num-cell">${p.diskMbS.toFixed(1)} MB/秒</td>
                              <td class="w11-tm-num-cell">${p.netMbps.toFixed(1)} Mbps</td>
                              <td class="w11-tm-spacer-cell"></td>
                            </tr>
                          `,
                        )
                        .join('')
                    : ''
                }
              `
          }
        </tbody>
      </table>
    </div>
  `;
}

export function flattenDetailProcesses(list: readonly TmProcessNode[]): TmProcessNode[] {
  const flat: TmProcessNode[] = [];
  const seen = new Set<number>();
  for (const p of list) {
    if (p.children && p.children.length > 0) {
      for (const c of p.children) {
        if (!seen.has(c.pid)) {
          seen.add(c.pid);
          flat.push(c);
        }
      }
    }
    if (!seen.has(p.pid)) {
      seen.add(p.pid);
      flat.push(p);
    }
  }
  return flat;
}

export function renderDetailsPage(opts: {
  processes: readonly TmProcessNode[];
  selectedPid: number | null;
  search: string;
  sortKey: 'name' | 'pid' | 'status' | 'user' | 'cpu' | 'mem' | 'arch' | 'desc';
  sortDir: 'asc' | 'desc';
  showFullAccountName: boolean;
}): string {
  const flat = flattenDetailProcesses(opts.processes)
    .filter((p) => matchesSearch(opts.search, p.processName, p.pid, p.desc, p.user))
    .sort((a, b) => {
      const m = opts.sortDir === 'asc' ? 1 : -1;
      switch (opts.sortKey) {
        case 'pid':
          return (a.pid - b.pid) * m;
        case 'cpu':
          return (a.cpu - b.cpu) * m;
        case 'mem':
          return (a.memMb - b.memMb) * m;
        case 'user':
          return a.user.localeCompare(b.user) * m;
        case 'arch':
          return a.arch.localeCompare(b.arch) * m;
        case 'status':
          return a.status.localeCompare(b.status) * m;
        case 'desc':
          return a.desc.localeCompare(b.desc, 'zh-CN') * m;
        default:
          return a.processName.localeCompare(b.processName, 'en-US') * m;
      }
    });

  const caret = (k: typeof opts.sortKey) => (opts.sortKey === k ? (opts.sortDir === 'asc' ? ' ∧' : ' ∨') : '');
  const userLabel = (u: string) => (opts.showFullAccountName && u === 'admin' ? 'MOBAN11\\admin' : u);
  const statusLabel = (s: TmProcessNode['status']) =>
    s === 'suspended' ? '已挂起' : s === 'efficiency' ? '正在使用效率模式' : '正在运行';

  return `
    <div class="w11-tm-proc-wrap">
      <table class="w11-tm-proc-table is-fill">
        <thead>
          <tr>
            <th data-det-sort="name" style="width:170px">名称${caret('name')}</th>
            <th data-det-sort="pid" class="is-num" style="width:68px">PID${caret('pid')}</th>
            <th data-det-sort="status" style="width:120px">状态${caret('status')}</th>
            <th data-det-sort="user" style="width:110px">用户名${caret('user')}</th>
            <th data-det-sort="cpu" class="is-num" style="width:68px">CPU${caret('cpu')}</th>
            <th data-det-sort="mem" class="is-num" style="width:118px" title="内存(活动专用工作集)">内存(活动专用工作集)${caret('mem')}</th>
            <th data-det-sort="arch" style="width:78px">体系结构${caret('arch')}</th>
            <th data-det-sort="desc" style="width:auto;min-width:190px">描述${caret('desc')}</th>
          </tr>
        </thead>
        <tbody>
          ${
            flat.length === 0
              ? `<tr><td colspan="8" class="w11-tm-empty">没有匹配的进程详细信息</td></tr>`
              : flat
                  .map((p) => {
                    const sel = opts.selectedPid === p.pid ? 'is-selected' : '';
                    const cpuStr = String(Math.round(p.cpu)).padStart(2, '0');
                    const st = statusLabel(p.status);
                    return `
                      <tr class="w11-tm-proc-row ${sel}" data-det-pid="${p.pid}">
                        <td class="w11-tm-name-cell"><span class="w11-tm-appdot is-exe"></span><span class="w11-tm-ellipsis">${esc(p.processName)}</span></td>
                        <td class="w11-tm-num-cell">${p.pid}</td>
                        <td title="${esc(st)}"><span class="w11-tm-ellipsis">${st}</span></td>
                        <td><span class="w11-tm-ellipsis">${esc(userLabel(p.user))}</span></td>
                        <td class="w11-tm-num-cell">${cpuStr}</td>
                        <td class="w11-tm-num-cell">${formatMemKb(p.memMb)}</td>
                        <td>${esc(p.arch)}</td>
                        <td><span class="w11-tm-ellipsis">${esc(p.desc)}</span></td>
                      </tr>
                    `;
                  })
                  .join('')
          }
        </tbody>
      </table>
    </div>
  `;
}

export function renderServicesPage(opts: {
  services: readonly TmServiceRow[];
  selectedName: string | null;
  search: string;
  sortKey: 'name' | 'pid' | 'desc' | 'status' | 'group';
  sortDir: 'asc' | 'desc';
}): string {
  const list = opts.services
    .filter((s) => matchesSearch(opts.search, s.name, s.pid, s.desc, s.status, s.group))
    .sort((a, b) => {
      const m = opts.sortDir === 'asc' ? 1 : -1;
      switch (opts.sortKey) {
        case 'pid':
          return ((a.pid ?? 0) - (b.pid ?? 0)) * m;
        case 'status':
          return a.status.localeCompare(b.status, 'zh-CN') * m;
        case 'desc':
          return a.desc.localeCompare(b.desc, 'zh-CN') * m;
        case 'group':
          return a.group.localeCompare(b.group) * m;
        default:
          return a.name.localeCompare(b.name, 'en-US') * m;
      }
    });
  const caret = (k: typeof opts.sortKey) => (opts.sortKey === k ? (opts.sortDir === 'asc' ? ' ∧' : ' ∨') : '');

  return `
    <div class="w11-tm-proc-wrap">
      <table class="w11-tm-proc-table is-fill">
        <thead>
          <tr>
            <th data-svc-sort="name" style="width:190px">名称${caret('name')}</th>
            <th data-svc-sort="pid" class="is-num" style="width:74px">PID${caret('pid')}</th>
            <th data-svc-sort="desc" style="width:auto;min-width:240px">描述${caret('desc')}</th>
            <th data-svc-sort="status" style="width:94px">状态${caret('status')}</th>
            <th data-svc-sort="group" style="width:170px">组${caret('group')}</th>
          </tr>
        </thead>
        <tbody>
          ${
            list.length === 0
              ? `<tr><td colspan="5" class="w11-tm-empty">没有匹配的服务</td></tr>`
              : list
                  .map(
                    (s) => `
                      <tr class="w11-tm-proc-row ${opts.selectedName === s.name ? 'is-selected' : ''}" data-svc-name="${esc(s.name)}">
                        <td class="w11-tm-name-cell"><span class="w11-tm-svc-gear">⚙</span><span class="w11-tm-ellipsis">${esc(s.name)}</span></td>
                        <td class="w11-tm-num-cell">${s.pid ?? ''}</td>
                        <td><span class="w11-tm-ellipsis">${esc(s.desc)}</span></td>
                        <td>${esc(s.status)}</td>
                        <td><span class="w11-tm-ellipsis">${esc(s.group)}</span></td>
                      </tr>
                    `,
                  )
                  .join('')
          }
        </tbody>
      </table>
    </div>
  `;
}

export function renderSettingsPage(s: TmSettingsState): string {
  const chk = (key: keyof TmSettingsState, checked: boolean, label: string) => `
    <label class="w11-tm-set-check">
      <input type="checkbox" data-set-chk="${key}" ${checked ? 'checked' : ''} />
      <span>${label}</span>
    </label>
  `;

  return `
    <div class="w11-tm-settings-scroll">
      <section class="w11-tm-set-group">
        <h3 class="w11-tm-set-heading">外观</h3>
        <div class="w11-tm-set-card">
          <div class="w11-tm-set-info">
            <strong>应用主题</strong>
            <span>选择要显示的应用主题</span>
          </div>
          <select class="w11-tm-select" data-set-select="themePref">
            <option value="system" ${s.themePref === 'system' ? 'selected' : ''}>使用系统设置</option>
            <option value="light" ${s.themePref === 'light' ? 'selected' : ''}>浅色</option>
            <option value="dark" ${s.themePref === 'dark' ? 'selected' : ''}>深色</option>
          </select>
        </div>
      </section>

      <section class="w11-tm-set-group">
        <h3 class="w11-tm-set-heading">常规</h3>
        <div class="w11-tm-set-card">
          <div class="w11-tm-set-info">
            <strong>默认起始页</strong>
            <span>指定启动任务管理器时显示的页面</span>
          </div>
          <select class="w11-tm-select" data-set-select="defaultTab">
            <option value="processes" ${s.defaultTab === 'processes' ? 'selected' : ''}>进程</option>
            <option value="performance" ${s.defaultTab === 'performance' ? 'selected' : ''}>性能</option>
            <option value="history" ${s.defaultTab === 'history' ? 'selected' : ''}>应用历史记录</option>
            <option value="startup" ${s.defaultTab === 'startup' ? 'selected' : ''}>启动应用</option>
            <option value="users" ${s.defaultTab === 'users' ? 'selected' : ''}>用户</option>
            <option value="details" ${s.defaultTab === 'details' ? 'selected' : ''}>详细信息</option>
            <option value="services" ${s.defaultTab === 'services' ? 'selected' : ''}>服务</option>
          </select>
        </div>

        <div class="w11-tm-set-card">
          <div class="w11-tm-set-info">
            <strong>实时更新速度</strong>
            <span>选择更新系统资源使用情况报告的频率</span>
          </div>
          <select class="w11-tm-select" data-set-select="updateSpeed">
            <option value="high" ${s.updateSpeed === 'high' ? 'selected' : ''}>高</option>
            <option value="normal" ${s.updateSpeed === 'normal' ? 'selected' : ''}>常规</option>
            <option value="low" ${s.updateSpeed === 'low' ? 'selected' : ''}>低</option>
            <option value="paused" ${s.updateSpeed === 'paused' ? 'selected' : ''}>已暂停</option>
          </select>
        </div>

        <div class="w11-tm-set-expander">
          <button type="button" class="w11-tm-set-exp-head" data-set-exp="winMgmtExpanded">
            <div class="w11-tm-set-info">
              <strong>窗口管理</strong>
              <span>指定默认任务管理器窗口行为</span>
            </div>
            <span class="w11-tm-set-chev">${s.winMgmtExpanded ? '⌃' : '⌄'}</span>
          </button>
          ${
            s.winMgmtExpanded
              ? `
                <div class="w11-tm-set-exp-body">
                  ${chk('alwaysOnTop', s.alwaysOnTop, '置于顶层')}
                  ${chk('minimizeOnUse', s.minimizeOnUse, '使用时最小化')}
                  ${chk('hideWhenMinimized', s.hideWhenMinimized, '最小化时隐藏')}
                </div>
              `
              : ''
          }
        </div>

        <div class="w11-tm-set-expander">
          <button type="button" class="w11-tm-set-exp-head" data-set-exp="otherOptsExpanded">
            <div class="w11-tm-set-info">
              <strong>其他选项</strong>
              <span>显示完整帐户名, 显示所有进程的历史记录, 确认对话框设置</span>
            </div>
            <span class="w11-tm-set-chev">${s.otherOptsExpanded ? '⌃' : '⌄'}</span>
          </button>
          ${
            s.otherOptsExpanded
              ? `
                <div class="w11-tm-set-exp-body">
                  ${chk('showFullAccountName', s.showFullAccountName, '显示完整帐户名')}
                  ${chk('showAllHistory', s.showAllHistory, '显示所有进程的历史记录')}
                  ${chk('confirmEfficiencyMode', s.confirmEfficiencyMode, '应用效率模式前询问我')}
                </div>
              `
              : ''
          }
        </div>
      </section>
    </div>
  `;
}
