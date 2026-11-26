import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const websiteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const requirements = JSON.parse(await readFile(path.join(websiteRoot, 'src/data/gpu-pv-driver-requirements.json'), 'utf8'));
const research = JSON.parse(await readFile(path.join(websiteRoot, 'src/data/gpu-pv-driver-research.json'), 'utf8'));

const FORBIDDEN_PROSE_CHARS = /[；;·]/;
const FORBIDDEN_FULLWIDTH_AND_DOT = /[；·]/;

function assertCleanText(value, label) {
  assert.equal(FORBIDDEN_PROSE_CHARS.test(value), false, `Forbidden punctuation (;, ；, or ·) in ${label}: ${value}`);
}

function assertCleanOptionalIdentifier(value, label) {
  assert.equal(FORBIDDEN_FULLWIDTH_AND_DOT.test(value), false, `Forbidden punctuation (； or ·) in ${label}: ${value}`);
}

test('gpu-pv-driver-requirements keeps valid unique rows and clean copy punctuation', () => {
  assert.equal(requirements.schema_version, 1);
  assertCleanText(requirements.title, 'requirements.title');
  assertCleanText(requirements.disclaimer, 'requirements.disclaimer');
  const vendorIds = new Set(requirements.vendors.map((vendor) => vendor.id));
  const rowIds = new Set();

  assert(requirements.rows.length >= 9, 'Expected at least 9 GPU driver requirement rows');
  for (const row of requirements.rows) {
    assert(row.id && !rowIds.has(row.id), `Duplicate or missing row id: ${row.id}`);
    rowIds.add(row.id);
    assert(vendorIds.has(row.vendor), `Unknown vendor ${row.vendor} in row ${row.id}`);
    assertCleanText(row.driver_line, `row.${row.id}.driver_line`);
    assertCleanText(row.windows_min_version, `row.${row.id}.windows_min_version`);
    assertCleanText(row.linux_min_version, `row.${row.id}.linux_min_version`);
    assertCleanText(row.notes || '', `row.${row.id}.notes`);
    for (const tier of row.tiers || []) {
      assertCleanText(tier.name, `row.${row.id}.tier.name`);
      assertCleanText(tier.windows_min_version, `row.${row.id}.tier.windows_min_version`);
      assertCleanText(tier.linux_min_version, `row.${row.id}.tier.linux_min_version`);
    }
  }
});

test('gpu-pv-driver-research stays aligned with requirements and corpus schema', () => {
  assert.equal(research.schema_version, 1);
  assert(['awaiting_corpus_import', 'complete'].includes(research.status), `Invalid research.status: ${research.status}`);
  if (research.title) assertCleanText(research.title, 'research.title');
  if (research.subtitle) assertCleanText(research.subtitle, 'research.subtitle');
  assertCleanText(research.scope_note, 'research.scope_note');
  if (research.corpus_summary?.collection_window) {
    assertCleanText(research.corpus_summary.collection_window, 'corpus_summary.collection_window');
  }

  const linesById = new Map(research.driver_lines.map((line) => [line.id, line]));
  assert.equal(linesById.size, research.driver_lines.length, 'Duplicate driver_lines id');

  for (const row of requirements.rows) {
    const line = linesById.get(row.research_anchor);
    assert(line, `Missing research anchor ${row.research_anchor} for row ${row.id}`);
    assert.equal(line.vendor, row.vendor, `Vendor mismatch for ${row.id}`);
    assert.equal(line.windows_threshold.version, row.windows_min_version, `Windows version mismatch for ${row.id}`);
    assert.equal(line.linux_threshold.version, row.linux_min_version, `Linux version mismatch for ${row.id}`);
  }

  for (const method of research.methodology || []) {
    assertCleanText(method.title, `methodology.${method.id}.title`);
    assertCleanText(method.summary, `methodology.${method.id}.summary`);
    for (const criterion of method.criteria || []) assertCleanText(criterion, `methodology.${method.id}.criteria`);
  }

  for (const line of research.driver_lines) {
    assertCleanText(line.title || line.series || line.id, `line.${line.id}.title`);
    assertCleanText(line.architecture_limits || '', `line.${line.id}.architecture_limits`);
    for (const side of ['windows_threshold', 'linux_threshold']) {
      const t = line[side];
      assertCleanText(t.previous_tested_version || '', `line.${line.id}.${side}.previous_tested_version`);
      assertCleanText(t.evidence_summary || '', `line.${line.id}.${side}.evidence_summary`);
      for (const artifact of t.artifacts || []) assertCleanOptionalIdentifier(artifact, `line.${line.id}.${side}.artifacts`);
    }
    for (const finding of line.key_findings || []) assertCleanText(finding, `line.${line.id}.key_findings`);
    for (const question of line.open_questions || []) assertCleanText(question, `line.${line.id}.open_questions`);
  }

  for (const section of research.narrative_sections || []) {
    assertCleanText(section.title, `section.${section.id}.title`);
    for (const p of section.paragraphs || []) assertCleanText(p, `section.${section.id}.paragraphs`);
    for (const b of section.bullets || []) assertCleanText(b, `section.${section.id}.bullets`);
    for (const link of section.links || []) assertCleanText(link.label, `section.${section.id}.links`);
    if (section.table) {
      for (const h of section.table.headers || []) assertCleanText(h, `section.${section.id}.table.headers`);
      for (const r of section.table.rows || []) {
        for (const c of r) assertCleanOptionalIdentifier(String(c), `section.${section.id}.table.cell`);
      }
    }
  }

  const pkgIds = new Set();
  for (const pkg of research.packages || []) {
    assert(pkg.id && !pkgIds.has(pkg.id), `Duplicate or missing package id: ${pkg.id}`);
    pkgIds.add(pkg.id);
    assert(pkg.vendor && pkg.series && pkg.version && pkg.filename, `Incomplete package entry: ${pkg.id}`);
    if (pkg.sha256) assert(/^[0-9a-f]{64}$/i.test(pkg.sha256), `Invalid SHA-256 on ${pkg.id}: ${pkg.sha256}`);
    assertCleanText(pkg.notes || '', `pkg.${pkg.id}.notes`);
    for (const f of pkg.evidence_files || []) assertCleanOptionalIdentifier(f, `pkg.${pkg.id}.evidence_files`);
  }

  if (research.status === 'complete') {
    assert(research.packages.length > 0, 'Complete research status requires populated packages');
    assert.equal(research.corpus_summary.total_packages, research.packages.length, 'total_packages must match packages.length');
  }
});

test('built docs pages expose GPU requirements, sidebar research sub-routes, and corpus anchors', async (t) => {
  const docsIndexPath = path.join(websiteRoot, 'dist/docs/index.html');
  if (!existsSync(docsIndexPath)) {
    t.skip('dist output not built yet');
    return;
  }

  const docsIndex = await readFile(docsIndexPath, 'utf8');
  assert(docsIndex.includes('href="/api/"') && docsIndex.includes('data-astro-reload'));
  assert(docsIndex.includes('href="/docs/gpu/"'));
  assert(docsIndex.includes('href="/docs/gpu-driver-research/"'));

  const gpuHtml = await readFile(path.join(websiteRoot, 'dist/docs/gpu/index.html'), 'utf8');
  assert(gpuHtml.includes('data-docs-gpu'));
  assert(gpuHtml.includes('href="/docs/gpu-driver-research/"'), 'Missing link to driver research archive in /docs/gpu/');
  for (const row of requirements.rows) {
    assert(gpuHtml.includes(row.driver_line), `Missing driver line in /docs/gpu/: ${row.driver_line}`);
    assert(gpuHtml.includes(`/docs/gpu-driver-research/${row.vendor}/#${row.research_anchor}`), `Missing anchor link for ${row.id}`);
  }

  const subRoutes = ['', 'nvidia', 'intel', 'amd', 'qualcomm', 'packages', 'appendix'];
  const allBuiltHtml = [];
  for (const sub of subRoutes) {
    const filePath = sub
      ? path.join(websiteRoot, 'dist/docs/gpu-driver-research', sub, 'index.html')
      : path.join(websiteRoot, 'dist/docs/gpu-driver-research/index.html');
    const html = await readFile(filePath, 'utf8');
    assert(html.includes('data-docs-gpu-research'), `Missing marker on ${sub || 'index'}`);
    assert(!html.includes('<title>undefined'), `Undefined title on ${sub || 'index'}`);
    allBuiltHtml.push(html);
  }
  const combined = allBuiltHtml.join('\n');
  for (const line of research.driver_lines) {
    assert(combined.includes(`id="${line.id}"`), `Missing section anchor id="${line.id}"`);
    const expectedLineTitle = line.title || line.series || line.id;
    assert(combined.includes(expectedLineTitle), `Missing line heading ${expectedLineTitle}`);
  }
  for (const section of research.narrative_sections || []) {
    assert(combined.includes(`id="${section.id}"`), `Missing narrative section anchor id="${section.id}"`);
  }
  const packagesHtml = allBuiltHtml[subRoutes.indexOf('packages')];
  if (research.status === 'complete') {
    assert(packagesHtml.includes(`显示 ${research.packages.length} / ${research.packages.length} 条`), 'Corpus counter mismatch');
    const firstSha = research.packages[0]?.sha256;
    if (firstSha) assert(packagesHtml.includes(`data-copy-text="${firstSha}"`), 'Missing SHA-256 copy button');
  }
});
