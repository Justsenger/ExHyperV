import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import { sourceFingerprint, watchCore } from './watch-core.mjs';

async function eventually(predicate) {
  for (let i = 0; i < 200; i++) {
    if (predicate()) return;
    await delay(10);
  }
  assert.fail('Watcher did not reach the expected state');
}

test('generated blocks and build outputs do not retrigger generation, but authored content does', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'core-watch-input-'));
  try {
    const document = content => `Guide\n<!-- api-reference:start -->${content}<!-- api-reference:end -->\n<!-- api-example-list:start -->${content}<!-- api-example-list:end -->`;
    await writeFile(path.join(root, 'API.md'), document('old'));
    const initial = await sourceFingerprint(root);
    await writeFile(path.join(root, 'API.md'), document('new'));
    await mkdir(path.join(root, 'bin'));
    await writeFile(path.join(root, 'bin/generated.cs'), 'ignored');
    assert.equal(await sourceFingerprint(root), initial);
    await writeFile(path.join(root, 'API.md'), 'New introduction\n' + document('new'));
    assert.notEqual(await sourceFingerprint(root), initial);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('watcher serializes builds, catches edits during a build and recovers after a failed edit', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'core-watch-live-'));
  const source = path.join(root, 'Example.cs');
  let stop;
  let release;
  let active = 0;
  let peak = 0;
  let errors = 0;
  const builds = [];
  try {
    await writeFile(source, 'initial');
    stop = await watchCore({ root, interval: 10,
      onError: () => { errors++; },
      generate: async () => {
        peak = Math.max(peak, ++active);
        const content = await readFile(source, 'utf8');
        builds.push(content);
        try {
          if (content === 'slow') await new Promise(resolve => { release = resolve; });
          if (content === 'invalid') throw new Error('Invalid documentation');
        } finally { active--; }
      },
    });
    await writeFile(source, 'slow');
    await eventually(() => !!release);
    await writeFile(source, 'edit-during-build');
    release();
    await eventually(() => builds.includes('edit-during-build'));
    await writeFile(source, 'invalid');
    await eventually(() => errors === 1);
    await delay(70);
    assert.equal(errors, 1, 'Do not repeatedly rebuild unchanged invalid input');
    await writeFile(source, 'fixed');
    await eventually(() => builds.includes('fixed'));
    assert.equal(peak, 1);
    stop();
    const count = builds.length;
    await writeFile(source, 'after-stop');
    await delay(70);
    assert.equal(builds.length, count);
  } finally {
    release?.();
    stop?.();
    await rm(root, { recursive: true, force: true });
  }
});
