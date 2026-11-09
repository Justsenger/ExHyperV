import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { isEligibleSource, selectImageSources, sourceHref, sourceProvider } from '../src/data/image-source-selection.js';

const read = async file => JSON.parse(await readFile(new URL(file, import.meta.url), 'utf8'));
const auditBytes = await readFile(new URL('../docs/reviewed-windows-sources-2026-10-24.json', import.meta.url));
const audit = JSON.parse(auditBytes.toString('utf8'));
const receipt = await read('../docs/windows-source-import-2026-10-25.json');
const data = await read('../src/data/image-sources.json');
const catalog = await read('../src/data/catalog.json');

test('accounts for all B changes, preserving the two explicit user exclusions', () => {
  assert.equal(createHash('sha256').update(auditBytes).digest('hex'), receipt.audit_sha256);
  assert.equal(receipt.changes.length, audit.changes.length);
  assert.deepEqual(receipt.changes.filter(c => c.status === 'skipped_user_instruction').map(c => c.change_id), ['WDL-003', 'WDL-006']);
  assert.equal(receipt.summary.applied_changes, 389);
  const ids = new Set();
  function visit(source) {
    if (source.review_import) ids.add(source.review_import.change_id);
    source.variants?.forEach(visit);
  }
  for (const panels of Object.values(data.records)) for (const sources of Object.values(panels)) sources.forEach(visit);
  for (const change of receipt.changes.filter(c => c.status === 'applied')) assert(ids.has(change.change_id), `Unmaterialized change ${change.change_id}`);
  for (const sources of Object.values(data.records['windows-11-26h2'])) {
    assert(!selectImageSources(sources).some(source => sourceProvider(source).id === 'microsoft'));
  }
});

test('excludes B-retired media without dropping valid language alternatives or temporary 429s', () => {
  for (const panels of Object.values(data.records)) for (const sources of Object.values(panels)) {
    const selected = selectImageSources(sources);
    assert(selected.every(isEligibleSource));
    assert(!selected.some(source => source.display_eligible === false));
    for (const source of sources.filter(source => source.review_import && source.default_suitability?.source_page_default_allowed === false)) {
      assert.equal(isEligibleSource(source), false);
    }
  }
  const excluded = { url: 'https://example.org/wrong.iso', display_eligible: false, variants: [{ url: 'https://example.org/chinese.iso', language: 'zh-CN' }] };
  assert.deepEqual(selectImageSources([excluded]), []);
  const throttled = { url: 'https://example.org/mirror.iso', availability: 'temporarily_rate_limited', verification: { status: 'login_or_blocked', http_status: 429 } };
  assert.equal(selectImageSources([throttled]).length, 1);
});

test('opens a specific source page while retaining the actual download file separately', () => {
  const source = { url: 'https://example.org/original.iso', source_page_url: 'https://example.org/file/123', direct_download_url: 'https://example.org/original.iso' };
  assert.equal(sourceHref(source), 'https://example.org/file/123');
  assert.equal(source.url, 'https://example.org/original.iso');
  for (const panels of Object.values(data.records)) for (const sources of Object.values(panels)) {
    for (const source of selectImageSources(sources)) {
      assert(/^https?:\/\//.test(sourceHref(source)));
      if (source.source_page_url) assert.equal(sourceHref(source), source.source_page_url);
    }
  }
});

test('keeps catalog visibility, architecture boundaries and Microsoft brand labels', () => {
  assert.equal(catalog.total, 389);
  const server = catalog.categories.find(c => c.name === 'Windows').groups.find(g => g.name === 'Windows Server');
  assert.equal(server.devices.length - server.primary_ids.length, 65);
  for (const language of ['zh-CN', 'zh-TW', 'en']) {
    const microsoft = selectImageSources([{ url: 'https://www.microsoft.com/download/details.aspx?id=123' }], { language });
    assert.equal(microsoft[0].displayLabel, 'Microsoft');
  }
  const sources = data.records['windows-10-2004'].x86;
  const corrected = selectImageSources(sources).find(source => source.provider === 'uup');
  assert(sources.some(source => source.url.includes('0dbf0ce9-e3de-4421-bbae-d01c6a59fbd9') && isEligibleSource(source)));
  assert(!corrected.url.includes('8b83c3ec-2f8a-4d48-b1c5-11fbfc5287b3'));
});
