import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readBundle, validateBundle } from './core-api.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const directory = process.argv.includes('--dist') ? 'dist/api' : 'public/api';
const manifest = validateBundle(await readBundle(path.join(root, directory)), {
  allowDirty: !process.env.CI || process.env.CI === 'false',
});
console.log(`Verified ${directory}: ${manifest.version}, ${manifest.types} types, ${manifest.members} members; HTML and Markdown SHA-256 match.`);
