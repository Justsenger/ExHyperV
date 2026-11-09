import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile, access, readdir } from 'node:fs/promises';
import path from 'node:path';
import { selectImageSources, sourceHref } from '../src/data/image-source-selection.js';

const root = process.cwd();
await import('./verify-windows-catalog.mjs');
await import('./verify-ubuntu-catalog.mjs');
const catalog = JSON.parse(await readFile(path.join(root, 'src/data/catalog.json'), 'utf8'));
const records = catalog.categories.flatMap(category => category.groups.flatMap(group =>
  group.devices.map(record => ({ ...record, family: category.name, branch: group.name }))));
const previews = JSON.parse(await readFile(path.join(root, 'src/data/system-previews.json'), 'utf8'));
const imageSources = JSON.parse(await readFile(path.join(root, 'src/data/image-sources.json'), 'utf8'));
const sourceReview = JSON.parse(await readFile(path.join(root, 'docs/image-source-review.json'), 'utf8'));
for (const finding of sourceReview.findings.filter(finding => finding.status === 'source_replaced')) {
  const architecture = finding.architecture.toLowerCase();
  const candidates = imageSources.records[finding.version_id][architecture];
  // The imported B review supersedes the older preliminary review for this panel.
  if (candidates.some(source => source.review_import)) continue;
  assert(!candidates.some(source => source.url === finding.url), `Retired mismatched source returned: ${finding.version_id}/${architecture}`);
  assert(candidates.some(source => source.url === finding.replacement_url), `Replacement source missing: ${finding.version_id}/${architecture}`);
}
const ids = records.map(r => r.codename);
assert.equal(new Set(ids).size, ids.length, 'Duplicate route IDs');
assert.equal(ids.length, catalog.total, 'Catalog total changed');
const aliases = [...catalog.architecture_aliases, ...catalog.version_aliases];
assert.equal(ids.length + aliases.length, catalog.route_total, 'Route total changed');
for (const record of records) {
  const file = path.join(root, 'dist/os', record.codename, 'index.html');
  const html = await readFile(file, 'utf8');
  assert(html.includes(`data-version="${record.codename}"`), `Missing route ${record.codename}`);
  assert(html.includes('aria-current="page"'), `No selected item on ${record.codename}`);
  assert(!/HarmonyOS|fonts\.googleapis|fonts\.gstatic|materialize-reference|vendor\.js/.test(html), 'Legacy or remote asset remains');
  assert(!/data-edition|class="edition-options"|data-architecture-(?:choice|static|preview|tags)="arm32"|href="\/[^"\s]*[?&](?:amp;)?edition=/i.test(html), `Unsupported architecture or edition UI on ${record.codename}`);
  const panels = record.architectures?.length ? record.architectures : ['default'];
  assert(html.includes('data-os-preview'), `Missing system preview: ${record.codename}`);
  assert.equal((html.match(/<img\b/g) || []).length, panels.length, `Missing architecture-specific images: ${record.codename}`);
  assert.equal((html.match(/data-image-sources/g) || []).length, panels.length, `Missing image sources rows: ${record.codename}`);
  assert(!html.includes('class="version-description"'), `Descriptions must stay empty: ${record.codename}`);
  for (const architecture of panels) {
    const preview = previews[record.codename][architecture];
    const candidates = imageSources.records[record.codename]?.[architecture];
    assert(candidates?.length >= 1, `Missing source candidates: ${record.codename}/${architecture}`);
    for (const source of candidates) {
      const url = new URL(source.url);
      assert(['http:', 'https:'].includes(url.protocol), `Invalid source URL: ${source.url}`);
      assert(source.verification?.status && source.verification?.checked_on, `Missing verification record: ${source.url}`);
      if (source.media_format === 'ESD') {
        const mediaUrl = new URL(source.direct_download_url || source.download_url || source.url);
        assert(mediaUrl.pathname.toLowerCase().endsWith('.esd'), `ESD source points to another file format: ${source.url}`);
        assert.equal(source.media_role, 'installation_components', `ESD must not be marked as ready ISO: ${source.url}`);
      }
    }
    const selectedSources = selectImageSources(candidates);
    assert.equal(new Set(selectedSources.map(source => source.provider)).size, selectedSources.length, `Duplicate source provider: ${record.codename}/${architecture}`);
    for (const source of selectedSources) {
      const sourceArchitecture = source.architecture || source.media_architecture;
      if (architecture !== 'default' && typeof sourceArchitecture === 'string' && ['x86', 'x64', 'arm64'].includes(sourceArchitecture.toLowerCase())) {
        assert.equal(sourceArchitecture.toLowerCase(), architecture, `Displayed source architecture mismatch: ${record.codename}/${architecture}`);
      }
      if (source.architecture && source.build_number) {
        const builds = Array.isArray(record.build_number) ? record.build_number : [record.build_number];
        assert(builds.map(String).includes(source.build_number.split('.')[0]), `Displayed source base build mismatch: ${record.codename}/${architecture}`);
      }
      const escaped = sourceHref(source).replaceAll('&', '&amp;').replaceAll('"', '&quot;');
      assert(html.includes(`href="${escaped}"`), `Source link not rendered: ${record.codename}/${architecture}: ${source.url}`);
    }
    assert.equal(preview.description, '', `Description must stay empty: ${record.codename}/${architecture}`);
    assert(html.includes(`data-architecture-preview="${architecture}"`), `Missing architecture preview: ${record.codename}/${architecture}`);
    if (preview.image) {
      assert(html.includes(`src="${preview.image}"`), `Wrong screenshot: ${record.codename}/${architecture}`);
      await access(path.join(root, 'dist', preview.image));
    } else {
      assert.equal(preview.image, null, `Missing image state: ${record.codename}/${architecture}`);
      const fullName = record.family === 'Ubuntu' ? record.name.replace('Ubuntu', `Ubuntu ${record.branch}`) : record.name;
      const label = { x86: 'x86', x64: 'x64', arm64: 'ARM64', default: '' }[architecture];
      const caption = `${fullName}${label ? ` ${label}` : ''}`;
      const placeholder = html.match(new RegExp(`<figure\\b[^>]*data-placeholder-architecture="${architecture}"[^>]*>([\\s\\S]*?)<\\/figure>`));
      assert(placeholder?.[1].includes('src="/images/system-placeholder.svg"'), `Missing shared background: ${record.codename}/${architecture}`);
      assert(placeholder[1].includes(`>${caption}</span>`), `Missing live placeholder text: ${record.codename}/${architecture}`);
    }
    if (preview.build_number) {
      const build = Array.isArray(preview.build_number) ? preview.build_number.join(' / ') : preview.build_number;
      assert(html.includes(`data-build-number="${build}"`), `Wrong preview build: ${record.codename}/${architecture}`);
    }
  }
  for (const architecture of record.architectures || []) {
    const attribute = record.architectures.length === 1 ? 'data-architecture-static' : 'data-architecture-choice';
    assert(html.includes(`${attribute}="${architecture}"`), `Missing ${architecture} option on ${record.codename}`);
  }
}
for (const alias of aliases) {
  const html = await readFile(path.join(root, 'dist/os', alias.codename, 'index.html'), 'utf8');
  assert(html.includes(`data-navigation-version="${alias.base_version_id}"`), `Alias does not select its canonical version: ${alias.codename}`);
  if (alias.architecture) assert(html.includes(`data-default-architecture="${alias.architecture}"`), `Wrong alias architecture: ${alias.codename}`);
  assert(!/data-edition|class="edition-options"|data-architecture-(?:choice|static|preview|tags)="arm32"|href="\/[^"\s]*[?&](?:amp;)?edition=/i.test(html), `Unsupported architecture or edition UI on alias ${alias.codename}`);
}
for (const slug of ['', ...ids, ...aliases.map(alias => alias.codename)]) {
  const current = await readFile(path.join(root, 'dist/os', slug, 'index.html'), 'utf8');
  assert(!/href="\/wiki(?:[\/"?#])/.test(current), `Old internal links remain: ${slug}`);
}
await assert.rejects(access(path.join(root, 'dist/wiki')), 'Removed /wiki/ routes must not be generated');
await assert.rejects(access(path.join(root, 'dist/os/windows-10-team-surface-hub/index.html')), 'Team page must be deleted');
const generatedRoutes = await readdir(path.join(root, 'dist/os'));
assert(!generatedRoutes.some(name => name.includes('arm32')), 'ARM32 routes must be removed');
for (const route of ['', 'os', 'changelog']) {
  const html = await readFile(path.join(root, 'dist', route, 'index.html'), 'utf8');
  const main = html.match(/<main\b[^>]*>([\s\S]*?)<\/main>/);
  assert(main && main[1].trim() === '', `Unexpected placeholder content in ${route}`);
}
const docs = await readFile(path.join(root, 'dist/docs/index.html'), 'utf8');
assert(docs.includes('href="/api/"') && docs.includes('data-astro-reload'), 'API entry must load the standalone Core document');
assert(!docs.includes('href="/sdk/"'), 'Old SDK entry remains');
await assert.rejects(access(path.join(root, 'dist/sdk')), 'Removed /sdk/ route must not be generated');
for (const name of ['Comfortaa-OFL.txt', 'Resource-Han-Rounded-OFL.txt', 'Astro-LICENSE.txt', 'THIRD_PARTY_NOTICES.txt']) {
  await access(path.join(root, 'dist/licenses', name));
}
const assets = await readdir(path.join(root, 'dist/_astro'));
assert(assets.some(name => name.endsWith('.woff2')), 'Missing bundled fonts');
const font = JSON.parse(await readFile(path.join(root, 'dist/licenses/resource-rounded-site-font.json'), 'utf8'));
const fontBytes = await readFile(path.join(root, 'dist/typefaces', font.file));
assert.equal(fontBytes.subarray(0, 4).toString(), 'wOF2', 'Chinese font is not a WOFF2 file');
assert.equal(fontBytes.length, font.bytes, 'Chinese font size mismatch');
assert.equal(createHash('sha256').update(fontBytes).digest('hex'), font.sha256, 'Chinese font checksum mismatch');
assert.deepEqual((await readdir(path.join(root, 'dist/typefaces'))).sort(), [font.file, 'resource-rounded.css'].sort(), 'Unexpected extra Chinese font assets');
const fontCss = await readFile(path.join(root, 'dist/typefaces/resource-rounded.css'), 'utf8');
assert.equal((fontCss.match(/@font-face/g) || []).length, 1, 'Chinese font must use one complete file');
assert(fontCss.includes(font.file) && !fontCss.includes('unicode-range'), 'Chinese font must not be subset by Unicode range');
await access(path.join(root, 'dist/images/system-placeholder.svg'));
await assert.rejects(access(path.join(root, 'dist/placeholders')), 'Per-version placeholder images must not be published');
assert(!(await readdir(path.join(root, 'dist'))).some(name => ['app.js','vendor.js','styles.css','fonts','theme.js'].includes(name)), 'Reference assets remain');
console.log(`Verified ${ids.length} version routes and ${aliases.length} compatibility aliases, empty modules, local fonts and license files.`);
