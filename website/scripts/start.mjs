import { spawn, spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const astro = path.join(root, 'node_modules/astro/bin/astro.mjs');
if (!existsSync(astro)) {
  console.log('首次启动：安装 website 的锁定依赖，需要 Node.js 24、pnpm 11.25 和 .NET 10 SDK。');
  const windows = process.platform === 'win32';
  const install = spawnSync(windows ? 'cmd.exe' : 'pnpm', windows
    ? ['/d', '/s', '/c', 'pnpm install --frozen-lockfile'] : ['install', '--frozen-lockfile'],
  { cwd: root, stdio: 'inherit', windowsHide: true });
  if (install.error || install.status !== 0) {
    console.error('依赖安装失败。请安装 pnpm 后，在 website 目录执行 pnpm install --frozen-lockfile。');
    process.exit(1);
  }
}
// Reusing the entry point should show the running site, not start another server.
const { default: config } = await import('../astro.config.mjs');
let runningPort = config.server.port;
try {
  const running = JSON.parse(readFileSync(path.join(root, '.astro/dev.json'), 'utf8'));
  if (Number.isInteger(running.pid) && running.pid > 0 && Number.isInteger(running.port)) {
    process.kill(running.pid, 0);
    runningPort = running.port;
  }
} catch { /* A build can clear .astro while the existing server is still running. */ }
try {
  const response = await fetch(`http://127.0.0.1:${runningPort}/api/manifest.json`, { signal: AbortSignal.timeout(2000) });
  if (response.ok && (await response.json()).source?.repository === 'Justsenger/ExHyperV') {
    console.log(`文档网站已运行：http://localhost:${runningPort}/（API：/api/，系统图鉴：/os/）`);
    process.exit(0);
  }
} catch { /* No matching website is listening; start it below. */ }
const child = spawn(process.execPath, [astro, 'dev'], { cwd: root, stdio: 'inherit', windowsHide: true });
child.once('error', error => { console.error(error.message); process.exitCode = 1; });
child.once('exit', code => { process.exitCode = code ?? 1; });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
