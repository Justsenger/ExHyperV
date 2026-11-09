import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';

export const bundleFiles = ['manifest.json', 'index.html', 'API.md'];

export async function readBundle(directory) {
  return Object.fromEntries(await Promise.all(bundleFiles.map(async name => [name, await readFile(path.join(directory, name))])));
}

export function validateBundle(bundle, { allowDirty = false } = {}) {
  const manifest = JSON.parse(bundle['manifest.json'].toString('utf8'));
  assert.equal(manifest.schemaVersion, 1, 'Unsupported Core API bundle schema');
  assert.equal(manifest.source?.repository, 'Justsenger/ExHyperV', 'Unexpected Core repository');
  // Feature branches and detached CI checkouts use the Core in the same checkout.
  assert.equal(typeof manifest.source.branch, 'string', 'Missing Core source branch');
  assert.match(manifest.source.revision, /^[0-9a-f]{40}$/, 'Invalid Core revision');
  assert.equal(typeof manifest.source.dirty, 'boolean', 'Missing Core working-tree state');
  assert(allowDirty || !manifest.source.dirty, 'Uncommitted Core content is only allowed in local previews');
  assert(Number.isFinite(Date.parse(manifest.generatedAt)), 'Invalid API export time');
  for (const name of ['index.html', 'API.md']) {
    const bytes = bundle[name];
    assert(bytes?.length, `Empty API file: ${name}`);
    assert.equal(bytes.length, manifest.files?.[name]?.bytes, `API file size mismatch: ${name}`);
    assert.equal(createHash('sha256').update(bytes).digest('hex'), manifest.files[name].sha256, `API file checksum mismatch: ${name}`);
  }
  const html = bundle['index.html'].toString('utf8');
  assert(/<!doctype html>/i.test(html), 'API export must be a full HTML document');
  const match = html.match(/<script\b[^>]*id="api-data"[^>]*>([\s\S]*?)<\/script>/);
  assert(match, 'API data is missing from the generated document');
  const data = JSON.parse(match[1]);
  assert.equal(data.version, manifest.version, 'API version mismatch');
  assert(data.types?.length > 0, 'API type catalog is empty');
  assert.equal(data.types.length, manifest.types, 'API type count mismatch');
  assert.equal(data.types.reduce((sum, type) => sum + type.members.length, 0), manifest.members, 'API member count mismatch');
  assert(bundle['API.md'].toString('utf8').startsWith('# ExHyperV.Core API'), 'Unexpected Markdown documentation');
  return manifest;
}

// Validate everything before replacing the previous bundle. Keep it on any validation failure.
export async function installBundle(bundle, destination, options) {
  const manifest = validateBundle(bundle, options);
  const parent = path.dirname(destination);
  await mkdir(parent, { recursive: true });
  const stagingDirectory = options?.stagingDirectory || parent;
  await mkdir(stagingDirectory, { recursive: true });
  const stage = await mkdtemp(path.join(stagingDirectory, '.api-sync-'));
  const backup = path.join(stage, 'previous');
  const next = path.join(stage, 'next');
  let hadPrevious = false;
  let preserveBackup = false;
  try {
    await mkdir(next);
    for (const name of bundleFiles) await writeFile(path.join(next, name), bundle[name]);
    try { await rename(destination, backup); hadPrevious = true; }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    try { await rename(next, destination); }
    catch (error) {
      if (hadPrevious) {
        try { await rename(backup, destination); }
        catch (restoreError) {
          preserveBackup = true;
          throw new AggregateError([error, restoreError], `Could not restore the previous API bundle; it is preserved at ${backup}`);
        }
      }
      throw error;
    }
  } finally {
    // Only remove the unique staging directory created above.
    if (!preserveBackup) await rm(stage, { recursive: true, force: true });
  }
  return manifest;
}
