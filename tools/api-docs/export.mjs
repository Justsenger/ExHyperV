// Export documentation only. This never loads Core or executes its examples.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
if (!args[0] || args.some((arg, i) => i > 0 && arg !== '--require-clean')) {
  throw new Error('Usage: node tools/api-docs/export.mjs <output-directory> [--require-clean]');
}
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const core = path.join(repository, 'src/ExHyperV.Core');
const documentation = path.join(repository, 'doc/api');
const output = path.resolve(args[0]);
const git = (...args) => execFileSync('git', ['-C', core, ...args], { encoding: 'utf8' }).trim();
const revision = git('rev-parse', 'HEAD');
const sourceBranch = git('branch', '--show-current');
const dirty = git('status', '--porcelain', '--untracked-files=all', '--', '.', '../../doc/api', '../../tools/api-docs', '../../website/src/api') !== '';
if (args.includes('--require-clean') && dirty) throw new Error('Refusing to publish uncommitted Core documentation.');
await mkdir(output, { recursive: true });
execFileSync('dotnet', ['run', '--project', path.join(repository, 'tools/api-docs/Generator.csproj'),
  '--configuration', 'Release', '--', '--html', path.join(output, 'index.html'), '--root', core],
  { cwd: core, stdio: 'inherit' });
const html = await readFile(path.join(output, 'index.html'));
const markdown = await readFile(path.join(documentation, 'API.md'));
const model = await readFile(path.join(documentation, 'API.json'));
await writeFile(path.join(output, 'API.json'), model);
const match = html.toString('utf8').match(/<script\b[^>]*id="api-data"[^>]*>([\s\S]*?)<\/script>/);
if (!match) throw new Error('Generated HTML has no API data.');
const data = JSON.parse(match[1]);
const describe = bytes => ({ sha256: createHash('sha256').update(bytes).digest('hex'), bytes: bytes.length });
await writeFile(path.join(output, 'API.md'), markdown);
await writeFile(path.join(output, 'manifest.json'), JSON.stringify({
  schemaVersion: 1,
  coreSource: data.source,
  source: { repository: 'Justsenger/ExHyperV', branch: sourceBranch, revision, dirty },
  version: data.version,
  generatedAt: new Date().toISOString(),
  types: data.types.length,
  members: data.types.reduce((sum, type) => sum + type.members.length, 0),
  files: { 'index.html': describe(html), 'API.md': describe(markdown), 'API.json': describe(model) },
}, null, 2) + '\n');
console.log(`Website API export: ${data.version}, ${revision.slice(0, 12)}${dirty ? ' (local changes)' : ''}`);
