import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installBundle, readBundle } from './core-api.mjs';

export const websiteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const coreRoot = path.resolve(websiteRoot, '../src/ExHyperV.Core');

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: websiteRoot, stdio: 'inherit', windowsHide: true });
    child.once('error', reject);
    child.once('exit', code => code === 0 ? resolve() : reject(new Error(`${command} exited with ${code}`)));
  });
}

export async function syncCoreApi({ updateMarkdown = false } = {}) {
  const isCI = !!(process.env.CI && process.env.CI !== 'false');
  if (isCI && updateMarkdown) throw new Error('CI checks committed API.md; update it locally before committing.');
  const cache = path.join(websiteRoot, '.cache');
  await mkdir(cache, { recursive: true });
  const temporary = await mkdtemp(path.join(cache, 'core-api-'));
  try {
    if (updateMarkdown) {
      await run('dotnet', ['run', '--project', path.join(coreRoot, 'Documentation/Generator'),
        '--configuration', 'Release', '--', '--write', '--root', coreRoot]);
    }
    await run(process.execPath, [path.join(coreRoot, 'Documentation/export-website.mjs'), temporary,
      ...(isCI ? ['--require-clean'] : [])]);
    const manifest = await installBundle(await readBundle(temporary), path.join(websiteRoot, 'public/api'), {
      allowDirty: !isCI, stagingDirectory: cache,
    });
    console.log(`API ready: ${manifest.version}, ${manifest.types} types, ${manifest.members} members, Core ${manifest.source.revision.slice(0, 12)}${manifest.source.dirty ? ' + local changes' : ''}`);
    return manifest;
  } finally {
    await rm(temporary, { recursive: true, force: true });
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const args = process.argv.slice(2).filter(arg => arg !== '--');
  if (args.some(arg => arg !== '--write')) throw new Error('Usage: pnpm api:sync [--write]');
  try { await syncCoreApi({ updateMarkdown: args.includes('--write') }); }
  catch (error) {
    console.error(`Core API generation failed: ${error.message}`);
    console.error('Existing API files were kept. If API.md is stale, run pnpm api:sync --write and review its changes.');
    process.exitCode = 1;
  }
}
