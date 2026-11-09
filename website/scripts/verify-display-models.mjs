import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const previews = JSON.parse(await readFile(new URL('../src/data/system-previews.json', import.meta.url), 'utf8'));
const evidence = JSON.parse(await readFile(new URL('../src/data/windows-display-model-evidence.json', import.meta.url), 'utf8'));

test('Windows 10 shared-core releases keep the correct supported WDDM model', () => {
  const expected = { '1507': '2.0', '1511': '2.0', '1607': '2.1', '1703': '2.2', '1709': '2.3', '1803': '2.4', '1809': '2.5',
    '1903': '2.6', '1909': '2.6', '2004': '2.7', '20h2': '2.7', '21h1': '2.7', '21h2': '2.7', '22h2': '2.7' };
  for (const [version, wddm] of Object.entries(expected)) {
    const id = `windows-10-${version}`;
    for (const preview of Object.values(previews[id])) {
      assert.equal(preview.wddm, wddm, id);
      assert.equal(preview.wddm_scope, 'os-supported');
      assert(evidence.records[id].sources.length);
    }
  }
});

test('legacy models retain evidence and do not invent XDDM versions or label all old Windows as XDDM', () => {
  for (const [id, panels] of Object.entries(previews)) {
    if (id.startsWith('windows-xp') || id.startsWith('windows-2000-professional')) {
      for (const entry of Object.values(panels)) {
        assert.equal(entry.xddm, true, id);
        assert.equal(entry.wddm, null, id);
      }
      assert.equal(evidence.records[id].xddm_version, null);
    }
    if (/^windows-(?:1-|2-|3-|95|98|me$|nt)/.test(id)) assert(!Object.values(panels).some(entry => entry.xddm), id);
  }
  assert.equal(previews['windows-vista'].x64.wddm, '1.0');
  assert.equal(previews['windows-vista-service-pack-1'].x64.wddm, '1.05');
  assert.equal(evidence.records['windows-vista-service-pack-2'].status, 'inherited-sp1-extension');
  assert.equal(previews['windows-7'].x86.wddm, '1.1');
  assert.equal(previews['windows-8'].x86.wddm, '1.2');
  assert.equal(previews['windows-8-1'].x64.wddm, '1.3');
});

test('built pages put Build and graphics-model metadata inside the image right column', async () => {
  for (const id of ['windows-11-24h2', 'windows-10-22h2', 'windows-7-service-pack-1', 'windows-xp-service-pack-3']) {
    const html = await readFile(new URL(`../dist/os/${id}/index.html`, import.meta.url), 'utf8');
    const image = html.indexOf('class="system-image"');
    const copy = html.indexOf('class="version-copy"');
    assert(image > -1 && copy > image, id);
    assert(html.indexOf('data-build-number') > copy, id);
    const toolbar = html.slice(html.indexOf('class="version-toolbar"'), html.indexOf('class="preview-layout"'));
    assert(!/data-build-number|data-wddm-version|data-xddm/.test(toolbar), id);
    const modelAttribute = id.startsWith('windows-xp') ? 'data-xddm' : 'data-wddm-version';
    assert(html.indexOf(modelAttribute) > copy, id);
  }
});
