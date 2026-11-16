import { defineConfig } from 'astro/config';
import coreApi from './scripts/core-api-integration.mjs';
import { loadDevHttpsOptions } from './scripts/dev-https.mjs';

const httpsOptions = loadDevHttpsOptions();

export default defineConfig({
  output: 'static',
  trailingSlash: 'always',
  integrations: [coreApi()],
  devToolbar: { enabled: false },
  server: { host: '0.0.0.0', port: 5189 },
  vite: {
    build: { assetsInlineLimit: 0 },
    server: {
      strictPort: true,
      ...(httpsOptions ? { https: httpsOptions } : {}),
      watch: { ignored: ['**/.cache/**'] },
    },
    ...(httpsOptions ? { preview: { https: httpsOptions } } : {}),
  },
});
