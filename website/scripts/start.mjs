import { spawn, spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ensureDevHttpsCert, loadDevHttpsOptions } from './dev-https.mjs';

function listLanUrls(port, protocol = 'http') {
  const ips = [];
  for (const entries of Object.values(os.networkInterfaces())) {
    for (const addr of entries ?? []) {
      if (!addr.internal && addr.family === 'IPv4' && !addr.address.startsWith('169.254.')) {
        ips.push(addr.address);
      }
    }
  }
  const rank = ip => (ip.startsWith('192.168.') ? 0 : ip.startsWith('10.') ? 1 : 2);
  ips.sort((a, b) => rank(a) - rank(b));
  const urls = ips.map(ip => `${protocol}://${ip}:${port}/`);
  const host = os.hostname()?.trim();
  if (host) urls.push(`${protocol}://${host.toLowerCase()}.local:${port}/`);
  return urls;
}

function probeManifest(port, useHttps) {
  const client = useHttps ? https : http;
  return new Promise((resolve) => {
    const req = client.request(
      {
        hostname: '127.0.0.1',
        port,
        path: '/api/manifest.json',
        method: 'GET',
        timeout: 2000,
        ...(useHttps ? { rejectUnauthorized: false } : {}),
      },
      (res) => {
        if (res.statusCode !== 200) {
          res.resume();
          resolve(null);
          return;
        }
        let body = '';
        res.setEncoding('utf8');
        res.on('data', chunk => { body += chunk; });
        res.on('end', () => {
          try {
            resolve(JSON.parse(body));
          } catch {
            resolve(null);
          }
        });
      },
    );
    req.on('timeout', () => {
      req.destroy();
      resolve(null);
    });
    req.on('error', () => resolve(null));
    req.end();
  });
}

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
spawnSync(process.execPath, [path.join(root, 'scripts/sync-winonweb.mjs')], { cwd: root, stdio: 'inherit', windowsHide: true });
const astro = path.join(root, 'node_modules/astro/bin/astro.mjs');
if (!existsSync(astro)) {
  console.log('首次启动：安装 website 的锁定依赖，需要 Node.js 24、pnpm 11.25 和 .NET 11 SDK。');
  const windows = process.platform === 'win32';
  const install = spawnSync(windows ? 'cmd.exe' : 'pnpm', windows
    ? ['/d', '/s', '/c', 'pnpm install --frozen-lockfile'] : ['install', '--frozen-lockfile'],
  { cwd: root, stdio: 'inherit', windowsHide: true });
  if (install.error || install.status !== 0) {
    console.error('依赖安装失败。请安装 pnpm 后，在 website 目录执行 pnpm install --frozen-lockfile。');
    process.exit(1);
  }
}

const certResult = ensureDevHttpsCert();
if (!certResult.ready && certResult.error) {
  console.warn(`HTTPS 开发证书不可用，将回退到 HTTP：${certResult.error}`);
}
const hasHttps = Boolean(loadDevHttpsOptions());
const protocol = hasHttps ? 'https' : 'http';

// Reusing the entry point should show the running site, not start another server.
const { default: config } = await import('../astro.config.mjs');
const listenHost = config.server?.host || '0.0.0.0';
const listenPort = config.server?.port || 5189;
let runningPort = listenPort;
try {
  const running = JSON.parse(readFileSync(path.join(root, '.astro/dev.json'), 'utf8'));
  if (Number.isInteger(running.pid) && running.pid > 0 && Number.isInteger(running.port)) {
    process.kill(running.pid, 0);
    runningPort = running.port;
  }
} catch { /* A build can clear .astro while the existing server is still running. */ }

const httpsManifest = hasHttps ? await probeManifest(runningPort, true) : null;
const runningManifest = httpsManifest ?? (await probeManifest(runningPort, false));
if (runningManifest?.source?.repository === 'Justsenger/ExHyperV') {
  const activeProto = httpsManifest ? 'https' : 'http';
  const lanUrls = listLanUrls(runningPort, activeProto);
  console.log(`文档网站已运行：${activeProto}://localhost:${runningPort}/（API：/api/，系统图鉴：/os/）`);
  if (lanUrls.length) console.log(`内网设备可访问：${lanUrls.join('  ')}`);
  process.exit(0);
}

const startLanUrls = listLanUrls(listenPort, protocol);
if (startLanUrls.length) console.log(`即将以内网可访问模式启动（${protocol}://0.0.0.0:${listenPort}）：${startLanUrls.join('  ')}`);
const child = spawn(process.execPath, [astro, 'dev', '--host', listenHost, '--port', String(listenPort)], { cwd: root, stdio: 'inherit', windowsHide: true });
child.once('error', error => { console.error(error.message); process.exitCode = 1; });
child.once('exit', code => { process.exitCode = code ?? 1; });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
