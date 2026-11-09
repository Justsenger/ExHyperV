import { defineConfig } from 'astro/config';
import coreApi from './scripts/core-api-integration.mjs';

export default defineConfig({
  output: 'static',
  trailingSlash: 'always',
  integrations: [coreApi()],
  server: { host: '0.0.0.0', port: 5189 },
  vite: { build: { assetsInlineLimit: 0 }, server: { strictPort: true, watch: { ignored: ['**/.cache/**'] } } },
});
