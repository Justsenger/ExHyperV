import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { coreRoot, websiteRoot, syncCoreApi } from './sync-core-api.mjs';
import { watchCore } from './watch-core.mjs';

export default function coreApi() {
  let stop;
  return {
    name: 'exhyperv-core-api',
    hooks: {
      'astro:server:setup': async ({ server, logger }) => {
        stop = await watchCore({
          root: [coreRoot, path.resolve(websiteRoot, '../doc/api'), path.resolve(websiteRoot, '../tools/api-docs'), path.join(websiteRoot, 'src/api')],
          generate: () => syncCoreApi({ updateMarkdown: true }),
          onSuccess: () => server.ws.send({ type: 'full-reload' }),
          onError: error => logger.error(`API 更新失败，保留上次成功页面；修正源码后自动重试。${error.message}`),
        });
        // Add Vite's reload client only in development; exported API HTML stays standalone.
        server.middlewares.use(async (req, res, next) => {
          if (!['GET', 'HEAD'].includes(req.method) || !['/api/', '/api/index.html'].includes(req.url?.split('?')[0])) return next();
          try {
            const html = await readFile(path.join(websiteRoot, 'public/api/index.html'), 'utf8');
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
