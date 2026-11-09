import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { selectImageSources } from '../src/data/image-source-selection.js';
import { isBlockedSource } from '../src/data/image-source-policy.js';

const source = {
  url: 'https://zerofs.link/f/chinese/', origin_provider: 'massgrave', match: 'exact_version_arch', kind: 'mirror',
  variants: [
    { language: 'en-US', url: 'https://zerofs.link/f/english/' },
    { language: 'fr-FR', url: 'https://zerofs.link/f/french/' },
    { language: 'zh-CN', url: 'https://zerofs.link/f/chinese/' },
    { language: 'zh-TW', url: 'https://zerofs.link/f/traditional/' },
  ],
};

test('selects one actual file per publisher in the visitor language, with explicit fallbacks', () => {
  for (const [language, expected] of [['zh-CN', 'chinese'], ['zh-TW', 'traditional'], ['zh-HK', 'traditional'], ['en', 'english'], ['fr-FR', 'french'], ['ja', 'chinese']]) {
    const selected = selectImageSources([source], { language });
    assert.equal(selected.length, 1);
    assert.equal(selected[0].url, `https://zerofs.link/f/${expected}/`);
    assert.equal(selected[0].provider, 'massgrave');
  }
  const onlyEnglish = { ...source, variants: source.variants.filter(v => !v.language.startsWith('zh')) };
  assert.equal(selectImageSources([onlyEnglish])[0].language, 'en-US');
});

test('does not trade a confirmed architecture match for a preferred-language family collection', () => {
  const candidates = [
    { url: 'https://example.com/english.iso', language: 'en', match: 'exact_version_arch' },
    { url: 'https://example.com/chinese/', language: 'zh-CN', match: 'family_collection' },
  ];
  assert.equal(selectImageSources(candidates)[0].url, candidates[0].url);
});

test('blocks HelloWindows, Xunlei redirects, retired pc528 and their subdomains', () => {
  for (const url of ['https://hellowindows.cn/', 'https://www.hellowindows.cn/file', 'https://pan.xunlei.com/s/example', 'https://www.pc528.net/file']) {
    assert(isBlockedSource({ url }));
    assert.equal(selectImageSources([{ url }]).length, 0);
  }
  assert(isBlockedSource({ url: 'https://example.com/share', payload_probe: { final_url: 'https://pan.xunlei.com/s/example' } }));
});

test('stored Bob Pony and Massgrave alternatives point to individual media and keep their architecture', async () => {
  const data = JSON.parse(await readFile(new URL('../src/data/image-sources.json', import.meta.url), 'utf8'));
  let checked = 0;
  for (const arches of Object.values(data.records)) for (const [architecture, sources] of Object.entries(arches)) {
    assert(!sources.some(isBlockedSource));
    for (const candidate of sources.filter(s => ['bobpony', 'massgrave'].includes(s.origin_provider))) {
      assert(candidate.variants?.length);
      assert.equal(new Set(candidate.variants.map(v => v.language)).size, candidate.variants.length);
      for (const variant of candidate.variants) {
        assert.equal(variant.declared_file.architecture, architecture);
        const url = new URL(variant.url);
        if (candidate.origin_provider === 'bobpony') {
          assert.equal(url.hostname, 'dl.bobpony.com');
          assert(/\.(iso|7z|zip|rar)$/i.test(url.pathname));
        } else {
          assert(url.hostname === 'zerofs.link' && /^\/f\/[^/]+\/?$/.test(url.pathname)
            || url.hostname === 'buzzheavier.com' && /^\/[^/]+\/?$/.test(url.pathname)
            || url.hostname.endsWith('.microsoft.com') && /\.iso$/i.test(url.pathname));
        }
        assert(!isBlockedSource(variant));
        checked++;
      }
    }
    for (const language of ['zh-CN', 'en', 'fr-FR']) {
      const selected = selectImageSources(sources, { language });
      assert.equal(new Set(selected.map(s => s.provider)).size, selected.length);
    }
  }
  assert(checked > 0);
  const win11 = data.records['windows-11-26h2'].x64;
  assert.equal(selectImageSources(win11).find(s => s.provider === 'massgrave').language, 'zh-CN');
  assert(selectImageSources(win11, { language: 'en' }).find(s => s.provider === 'massgrave').language.startsWith('en'));
});
