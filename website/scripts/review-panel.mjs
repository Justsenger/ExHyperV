import http from 'node:http';
import { readFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

// Optional development-only bridge. Review storage and automation stay in the local service.
export function createReviewPanelBridge(websiteRoot) {
  const configPath = path.join(websiteRoot, '.cache/api-review.json');
  const hosts = new Set(['localhost', '127.0.0.1', '[::1]',
    ...Object.values(os.networkInterfaces()).flat().filter(Boolean)
      .map(item => item.address.includes(':') ? `[${item.address}]` : item.address)]);

  async function target() {
    let config;
    try { config = JSON.parse(await readFile(configPath, 'utf8')); }
    catch (error) { if (error.code === 'ENOENT') return null; throw error; }
    const url = new URL(config.origin);
    if (url.protocol !== 'http:' || !['127.0.0.1', '[::1]'].includes(url.hostname)
      || !url.port || url.username || url.password || url.pathname !== '/' || url.search || url.hash)
      throw new Error('The review service must be a loopback HTTP origin with an explicit port.');
    return url;
  }

  function fail(res, status, message) {
    if (res.headersSent) return res.end();
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
    res.end(JSON.stringify({ error: message }));
  }

  async function middleware(req, res, next) {
    if (!req.url?.startsWith('/__review/')) return next();
    try {
      const upstream = await target();
      if (!upstream) return next();
      const origin = `${req.socket.encrypted ? 'https' : 'http'}://${req.headers.host}`;
      if (!hosts.has(new URL(origin).hostname)
        || (req.headers.origin && req.headers.origin !== origin)
        || (['POST', 'PATCH'].includes(req.method) && req.headers.origin !== origin))
        return fail(res, 403, '请在当前文档页面提交意见。');
      if (!['GET', 'HEAD', 'POST', 'PATCH'].includes(req.method)) return fail(res, 405, '不支持此操作。');
      const headers = { ...req.headers, host: upstream.host };
      if (headers.origin) headers.origin = upstream.origin;
      const forwarded = http.request(upstream, { path: req.url, method: req.method, headers }, response => {
        clearTimeout(timeout);
        res.writeHead(response.statusCode, response.headers);
        response.on('error', () => res.destroy());
        response.pipe(res);
      });
      const timeout = setTimeout(() => forwarded.destroy(new Error('Review service timeout')), 5000);
      forwarded.on('error', () => {
        clearTimeout(timeout);
        fail(res, 503, '本机意见服务暂时不可用，输入内容仍保留在小窗中。');
      });
      req.on('aborted', () => forwarded.destroy());
      res.on('close', () => { clearTimeout(timeout); forwarded.destroy(); });
      req.pipe(forwarded);
    } catch { fail(res, 503, '本机意见服务配置不可用。'); }
  }

  async function inject(html) {
    if (!await target()) return html;
    return html.replace('</body>', '<script src="/__review/panel.js" defer></script></body>');
  }
  return { middleware, inject };
}
