import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { installBundle, validateBundle } from './core-api.mjs';

function fixture({ dirty = false, branch = '2.0.0', version = '2.0.0-Alpha' } = {}) {
  const data = { schemaVersion: 1, source: { contentSha256: 'b'.repeat(64) }, version, types: [{ members: [{ name: 'QueryAsync' }] }] };
  const bundle = {
    'index.html': Buffer.from(`<!doctype html><html><script type="application/json" id="api-data">${JSON.stringify(data)}</script></html>`),
    'API.json': Buffer.from(JSON.stringify(data)),
    'API.md': Buffer.from('# ExHyperV.Core API\n'),
  };
  const files = Object.fromEntries(Object.entries(bundle).map(([name, bytes]) => [name, { bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') }]));
  bundle['manifest.json'] = Buffer.from(JSON.stringify({ schemaVersion: 1, source: { repository: 'Justsenger/ExHyperV', branch, revision: 'a'.repeat(40), dirty }, generatedAt: '2026-11-02T00:00:00Z', version, types: 1, members: 1, files }));
  return bundle;
}

test('production rejects uncommitted content but allows feature branches and detached checkouts', () => {
  for (const branch of ['2.0.0', 'codex/website', '']) assert.equal(validateBundle(fixture({ branch })).types, 1);
  assert.throws(() => validateBundle(fixture({ dirty: true })), /Uncommitted/);
  assert.equal(validateBundle(fixture({ dirty: true }), { allowDirty: true }).source.dirty, true);
});

test('a partial or mixed export cannot replace the last valid documentation', async () => {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'exhyperv-api-test-'));
  const destination = path.join(temporary, 'api');
  try {
    const original = fixture();
    await installBundle(original, destination);
    const broken = fixture({ version: '2.0.1-Alpha' });
    broken['index.html'] = original['index.html'];
    await assert.rejects(installBundle(broken, destination), /checksum mismatch/);
    assert.deepEqual(await readFile(path.join(destination, 'index.html')), original['index.html']);
    const missing = fixture();
    delete missing['API.md'];
    await assert.rejects(installBundle(missing, destination), /Empty API file/);
    const next = fixture({ version: '2.0.2-Alpha' });
    await installBundle(next, destination);
    assert.deepEqual(await readFile(path.join(destination, 'index.html')), next['index.html']);
  } finally { await rm(temporary, { recursive: true, force: true }); }
});

test('a bundle from another repository is rejected', () => {
  const bundle = fixture();
  const manifest = JSON.parse(bundle['manifest.json']);
  manifest.source.repository = 'another/repository';
  bundle['manifest.json'] = Buffer.from(JSON.stringify(manifest));
  assert.throws(() => validateBundle(bundle), /Unexpected Core/);
});
