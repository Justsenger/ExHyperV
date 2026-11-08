import {BrowserConsole} from './console-video.js';
import {scanCodes, keyboardToggles, desktopPoint} from './keyboard.js';

const $ = id => document.getElementById(id);
let machines = [], selected = null, transport = null, video = null, live = false, canInput = false;
let width = 0, height = 0, pendingMove = null, moveQueued = false;
let backends = [];
const selectedBackend = () => backends.find(backend => backend.id === $('mode').value);
const stateNames = {Running:'运行中',Off:'已关机',Saved:'已保存',Paused:'已暂停',Starting:'启动中',Stopping:'停止中'};
const setStatus = text => $('connection-status').textContent = text;
function notice(text) { $('notice').textContent = text; $('notice').hidden = !text; }
function input(message) { if (live && canInput) transport?.send(message); }
function release() { pendingMove = null; input({type:'release'}); }
function buttons() {
  $('connect').disabled = !selected?.isRunning || !!transport || !selectedBackend()?.available;
  $('disconnect').disabled = !transport; $('cad').disabled = !live || !canInput; $('mode').disabled = !!transport || !backends.length;
  $('quality').disabled = !!transport;
}
function disconnect(message = '未连接') {
  release();
  if (transport) {
    const old = transport; transport = null; old.stop();
  }
  live = canInput = false; video?.remove(); video = null; width = height = 0;
  $('placeholder').hidden = false; $('stats').hidden = true; $('resolution').textContent = '—';
  setStatus(message); buttons();
}
function renderList() {
  $('vm-list').replaceChildren();
  const search = $('search').value.toLocaleLowerCase();
  for (const vm of machines.filter(vm => vm.name.toLocaleLowerCase().includes(search))) {
    const button = document.createElement('button');
    button.className = `vm-item${vm.isRunning ? ' running' : ''}${selected?.id === vm.id ? ' active' : ''}`;
    button.setAttribute('aria-current',selected?.id === vm.id ? 'true' : 'false');
    const dot = document.createElement('span'); dot.className = 'indicator';
    const description = document.createElement('span'); description.className = 'description';
    const name = document.createElement('span'); name.className = 'name'; name.textContent = vm.name; name.title = vm.name;
    const state = document.createElement('span'); state.className = 'state'; state.textContent = stateNames[vm.state] || vm.state;
    description.append(name,state); button.append(dot,description);
    button.onclick = () => {
      if (selected?.id === vm.id && transport) return;
      disconnect(); selected = vm; $('vm-name').textContent = vm.name; renderList(); buttons();
      if (vm.isRunning) connect(); else notice('这台虚拟机未运行。请在宿主管理程序中启动后刷新列表。');
    };
    $('vm-list').append(button);
  }
}
async function refresh() {
  $('refresh').disabled = true; $('list-status').textContent = '正在读取虚拟机…';
  try {
    const response = await fetch('/api/vms');
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail || '读取虚拟机列表失败');
    machines = data.virtualMachines; $('count').textContent = machines.length;
    if (selected) selected = machines.find(vm => vm.id === selected.id) ?? null;
    const issues = data.issues;
    $('list-status').textContent = [
      machines.length ? '' : '本机没有虚拟机。',
      issues.length ? `部分附加信息未能读取：${issues.map(issue => issue.error || issue.component).join('；')}` : ''
    ].filter(Boolean).join(' ');
    renderList(); buttons();
  } catch (error) { $('list-status').textContent = error.message; }
  finally { $('refresh').disabled = false; }
}
async function loadBackends() {
  try {
    const response = await fetch('/api/console/backends');
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail || '读取连接方式失败');
    backends = data.backends;
    $('mode').replaceChildren();
    for (const backend of backends) {
      const option = document.createElement('option');
      option.value = backend.id; option.textContent = backend.name;
      option.disabled = !backend.available; option.title = backend.unavailableReason || '';
      $('mode').append(option);
    }
    $('mode').value = data.defaultBackendId;
    if (!selectedBackend()?.available) notice(selectedBackend()?.unavailableReason || '默认连接方式当前不可用，请选择其他方式。');
  } catch (error) { notice(error.message); }
  finally { buttons(); }
}
function flushMove() {
  if (pendingMove) { input(pendingMove); pendingMove = null; }
}
function bindInput(element) {
  const heldButtons = new Set();
  const point = event => desktopPoint(element.getBoundingClientRect(),width,height,event.clientX,event.clientY,element.videoWidth || width,element.videoHeight || height);
  element.addEventListener('contextmenu',event => event.preventDefault());
  element.addEventListener('pointermove',event => {
    const p = point(event); if (!p || !live || !canInput) return;
    pendingMove = {type:'pointer',...p};
    if (!moveQueued) { moveQueued = true; requestAnimationFrame(() => { moveQueued = false; flushMove(); }); }
  });
  for (const name of ['pointerdown','pointerup']) element.addEventListener(name,event => {
    if (![0,1,2].includes(event.button) || !live || !canInput) return;
    event.preventDefault(); element.focus({preventScroll:true}); flushMove();
    const p = point(event); if (!p) return;
    if (name === 'pointerdown') element.setPointerCapture(event.pointerId);
    if (name === 'pointerdown') heldButtons.add(event.button); else heldButtons.delete(event.button);
    input({type:'sync',toggles:keyboardToggles(event)});
    input({type:'pointer',...p,button:[1,4,2][event.button],down:name === 'pointerdown'});
  });
  element.addEventListener('pointercancel',() => { heldButtons.clear(); release(); });
  element.addEventListener('lostpointercapture',() => { if (heldButtons.size) { heldButtons.clear(); release(); } });
  element.addEventListener('wheel',event => {
    if (!live || !canInput) return; event.preventDefault(); flushMove();
    const p = point(event); if (!p) return;
    const delta = event.deltaMode === 1 ? event.deltaY * 40 : event.deltaMode === 2 ? event.deltaY * 400 : event.deltaY;
    input({type:'pointer',...p,wheel:Math.max(-255,Math.min(255,Math.round(-delta)))});
  },{passive:false});
  for (const name of ['keydown','keyup']) element.addEventListener(name,event => {
    if (!live || !canInput) return;
    if (event.code === 'Pause') { event.preventDefault(); if (name === 'keydown' && !event.repeat) input({type:'pause'}); return; }
    const code = scanCodes.get(event.code); if (!code) return;
    event.preventDefault(); event.stopPropagation();
    if (event.isComposing) { notice('请使用来宾系统内的输入法；浏览器输入法组合文本尚不支持。'); return; }
    input({type:'key',scanCode:code[0],extended:code[1],down:name === 'keydown'});
  });
  element.addEventListener('blur',release);
}
function connect() {
  if (!selected?.isRunning || transport || !selectedBackend()?.available) return;
  if (!window.RTCPeerConnection || !HTMLVideoElement.prototype.requestVideoFrameCallback) { notice('请使用新版 Edge 或 Chrome。'); return; }
  notice(''); setStatus('正在连接…');
  video = document.createElement('video'); video.muted = true; video.autoplay = true; video.playsInline = true; video.tabIndex = 0; video.setAttribute('aria-label',`${selected.name} 的控制台`);
  $('canvas-container').replaceChildren(video); bindInput(video);
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/api/console/${selected.id}?backend=${encodeURIComponent($('mode').value)}&quality=${$('quality').value}`;
  const current = transport = new BrowserConsole(video,url,message => {
    if (transport !== current) return;
    if (message.type === 'connected') {
      live = true; canInput = message.canInput === true;
      setStatus(canInput ? '已连接' : '已连接 · 仅查看'); $('placeholder').hidden = true;
      $('stats').hidden = !$('show-stats').checked; video.focus({preventScroll:true}); buttons();
    } else if (message.type === 'size') {
      width = message.width; height = message.height; $('resolution').textContent = `${width} × ${height} → ${message.videoWidth} × ${message.videoHeight} · ${message.encoder}`;
    } else if (message.type === 'stats') {
      $('stats').textContent = `${message.fps.toFixed(0)} FPS · 解码 ${message.decodedFps.toFixed(0)} FPS${message.hidden ? ' · 后台' : ''}\n`
        + `源更新 ${(message.sourceFps ?? 0).toFixed(0)} 次/秒\n`
        + `${message.mbps.toFixed(2)} Mbps · 控制往返 ${message.rtt.toFixed(1)} ms\n`
        + `编码 ${(message.encodeMs ?? 0).toFixed(1)} ms · 解码 ${message.decodeMs.toFixed(1)} ms\n`
        + `抖动缓冲 ${message.bufferMs.toFixed(1)} ms · 最大回调间隔 ${message.maxGap.toFixed(0)} ms`;
      $('stats').title = `编码输出 ${(message.encodedFps ?? 0).toFixed(1)} FPS；编码器 ${(message.bitrate / 1e6 || 0).toFixed(1)} Mbps；浏览器丢帧 ${message.browserDropped}；冻结 ${message.freezes} 次 / ${message.freezeSeconds.toFixed(2)} 秒；网络丢包 ${message.packetsLost}。FPS 为浏览器合成器提交帧计数，非显示器扫描率；往返不含来宾处理和显示。`;
      $('stats').dataset.sample = JSON.stringify(message);
    } else if (message.type === 'error') { disconnect('连接已结束'); notice(message.message); }
  });
  buttons();
}
$('connect').onclick = connect; $('disconnect').onclick = () => { disconnect(); notice(''); };
$('refresh').onclick = refresh; $('search').oninput = renderList;
$('mode').onchange = () => { notice(''); buttons(); };
$('show-stats').onchange = () => { $('stats').hidden = !live || !$('show-stats').checked; };
$('fullscreen').onclick = async () => {
  try { if (document.fullscreenElement) await document.exitFullscreen(); else await $('viewport').requestFullscreen(); }
  catch { notice('浏览器未允许进入全屏。'); }
};
$('cad').onclick = () => {
  release();
  input({type:'cad'});
  video?.focus({preventScroll:true});
};
window.addEventListener('blur',release);
document.addEventListener('visibilitychange',() => { if (document.hidden) release(); });
window.addEventListener('pagehide',() => disconnect());
void refresh();
void loadBackends();
