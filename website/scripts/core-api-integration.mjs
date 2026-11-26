import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { coreRoot, websiteRoot, syncCoreApi } from './sync-core-api.mjs';
import { watchCore } from './watch-core.mjs';
import { createReviewPanelBridge } from './review-panel.mjs';

export default function coreApi() {
  let stop;
  return {
    name: 'exhyperv-core-api',
    hooks: {
      'astro:server:setup': async ({ server, logger }) => {
        const hasFallbackApi = existsSync(path.join(websiteRoot, 'public/api/manifest.json'))
          && existsSync(path.join(websiteRoot, 'public/api/index.html'));
        let initialSync = true;
        stop = await watchCore({
          root: [coreRoot, path.join(websiteRoot, 'src/api')],
          generate: async () => {
            try {
              await syncCoreApi({ updateMarkdown: true });
            } catch (error) {
              if (!initialSync || !hasFallbackApi) throw error;
              logger.warn(`API 首次同步跳过（保留已有页面并继续监听源码变更）：${error.message}`);
            } finally {
              initialSync = false;
            }
          },
          onSuccess: () => server.ws.send({ type: 'full-reload' }),
          onError: error => logger.error(`API 更新失败，保留上次成功页面；修正源码后自动重试。${error.message}`),
        });
        const review = createReviewPanelBridge(websiteRoot);
        server.middlewares.use(review.middleware);
        // Add Vite's reload client only in development; exported API HTML stays standalone.
        server.middlewares.use(async (req, res, next) => {
          if (!['GET', 'HEAD'].includes(req.method) || !['/api/', '/api/index.html'].includes(req.url?.split('?')[0])) return next();
          try {
            const html = await review.inject(await readFile(path.join(websiteRoot, 'public/api/index.html'), 'utf8'));
            res.setHeader('Content-Type', 'text/html; charset=utf-8');
            res.setHeader('Cache-Control', 'no-store');
            res.end(req.method === 'HEAD' ? undefined : html.replace('</head>', '<script type="module" src="/@vite/client"></script></head>'));
          } catch (error) { next(error); }
        });
        logger.info('Core API 与网站共用此服务；修改 Core 文档材料后自动更新。');
      },
      'astro:server:done': () => stop?.(),
    },
  };
}
