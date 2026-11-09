import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir, access } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';

// This is a schema import, not a second factual review of the supplied audit.
const [auditPath, baselinePath, backupDirectory, mode] = process.argv.slice(2);
assert(auditPath && baselinePath && backupDirectory && mode === '--apply',
  'Usage: node scripts/import-reviewed-sources.mjs AUDIT BASELINE BACKUP_DIRECTORY --apply');
const root = process.cwd();
const input = await readFile(auditPath);
const audit = JSON.parse(input.toString('utf8').replace(/^\uFEFF/, ''));
const baselineBytes = await readFile(baselinePath);
const baseline = JSON.parse(baselineBytes.toString('utf8'));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
assert.equal(digest(baselineBytes), audit.baseline_sha256, 'Wrong export baseline');
const dataPath = path.join(root, 'src/data/image-sources.json');
const before = await readFile(dataPath);
const data = JSON.parse(before.toString('utf8'));
const receiptPath = path.join(root, 'docs/windows-source-import-2026-10-25.json');
try {
  await access(receiptPath);
  throw new Error('Import receipt already exists; do not apply this delta twice.');
} catch (error) { if (error.code !== 'ENOENT') throw error; }
const getPointer = pointer => pointer.split('/').slice(1).reduce((value, part) =>
  value[part.replaceAll('~1', '/').replaceAll('~0', '~')], baseline);
const falseFlags = new Set(['default_eligible', 'recommended_default', 'default_install_media_eligible',
  'default_eligible_as_original', 'original_installation_media_verified']);
function disqualified(fields) {
  return Object.entries(fields).some(([key, value]) =>
    (falseFlags.has(key) && value === false)
    || (/^(exclude_from_|remove_from_)/.test(key) && value === true))
    || fields.default_suitability?.source_page_default_allowed === false;
}
function normalizedBuild(value) {
  if (typeof value !== 'string' && typeof value !== 'number') return undefined;
  return String(value).match(/\b\d{3,5}(?:\.\d+)*\b/)?.[0];
}
function normalize(source, channel, checkedOn) {
  if (!source.url) source.url = source.source_page_url || source.download_landing_url || source.direct_download_url;
  assert(source.url && /^https?:\/\//.test(source.url), 'A source needs an HTTP(S) page URL');
  if (source.build_number === undefined && source.build !== undefined) source.build_number = normalizedBuild(source.build);
  if (source.media_format === undefined && source.format !== undefined) source.media_format = source.format;
  if (typeof source.architecture === 'string') {
    const arch = source.architecture.toLowerCase();
    source.architecture = ({ amd64: 'x64', x86_64: 'x64', i386: 'x86' })[arch] || arch;
  }
  if (source.filename) source.declared_file = {
    ...source.declared_file, name: source.filename,
    architecture: source.architecture || source.media_architecture || channel,
    ...(source.size_bytes !== undefined ? { size_bytes: source.size_bytes } : {}),
  };
  source.verification = {
    status: 'reviewed_metadata', checked_on: checkedOn, ...source.verification,
  };
  source.verification.checked_on ||= source.verification.checked_at_utc?.slice(0, 10) || checkedOn;
  if (disqualified(source)) source.display_eligible = false;
  if (source.variants) for (const variant of source.variants) normalize(variant, channel, checkedOn);
  return source;
}
function mergeReview(target, fields, change, { moved = false } = {}) {
  const previousUrl = target.url;
  Object.assign(target, structuredClone(fields));
  if (previousUrl && previousUrl !== target.url) target.previous_url = previousUrl;
  target.review_import = { change_id: change.change_id, operation: change.operation,
    reviewed_on: audit.checked_on, evidence_urls: change.evidence_urls, reason: change.reason };
  const removeDefault = change.default_link_updates?.some(update => update.action === 'remove_from_current_default');
  if (!moved && (removeDefault || disqualified(fields))) target.display_eligible = false;
  if (fields.selected_files?.length) {
    // Each language/member keeps its own identity; the UI still selects one link per provider.
    target.variants = fields.selected_files.map(file => normalize({
      url: file.source_page_url || fields.source_page_url || target.url,
      origin_provider: target.origin_provider,
      match: fields.match || target.match,
      ...structuredClone(file),
    }, change.architecture_panel, audit.checked_on));
  }
  return normalize(target, change.architecture_panel, audit.checked_on);
}

// Resolve references before adding anything. Array indices refer to B's input snapshot.
const targets = new Map();
for (const change of audit.changes) {
  const panel = getPointer(change.target_panel_pointer);
  const record = getPointer('/' + change.target_panel_pointer.split('/').slice(1, 3).join('/'));
  assert.equal(record.version_id, change.version_id);
  assert.equal(panel.architecture, change.architecture_panel);
  targets.set(change.change_id, change.target_candidate_pointers.map(pointer => {
    const old = getPointer(pointer);
    assert.equal(old.url, change.old_url);
    const tail = pointer.split('/all_candidates/')[1].split('/');
    let current = data.records[change.version_id][change.architecture_panel];
    for (const key of tail) current = current[key];
    assert.equal(current.url, change.old_url, `Changed baseline: ${change.change_id}`);
    return current;
  }));
}
const receipt = { imported_on: '2026-10-25', review_date: audit.checked_on,
  audit_sha256: digest(input), baseline_sha256: audit.baseline_sha256,
  source_data_before_sha256: digest(before), changes: [], moved_candidates: [] };
const moves = [];
for (const change of audit.changes) {
  const fields = change.changes;
  const list = data.records[change.version_id][change.architecture_panel];
  if (['WDL-003', 'WDL-006'].includes(change.change_id)) {
    receipt.changes.push({ change_id: change.change_id, status: 'skipped_user_instruction',
      reason: '用户要求26H2不显示Microsoft来源；不恢复该来源。' });
    continue;
  }
  const resolved = targets.get(change.change_id);
  if (change.append_to) {
    assert.equal(resolved.length, 0);
    list.push(mergeReview({}, fields, change));
  } else {
    assert(resolved.length, `No target for ${change.change_id}`);
    for (const target of resolved) mergeReview(target, fields, change);
  }
  const destination = fields.target_version_id || fields.destination_version_id;
  if (destination) {
    assert(data.records[destination], `Unknown destination: ${destination}`);
    for (const target of resolved) {
      const clone = structuredClone(target);
      target.display_eligible = false;
      // A "wrong release" judgement applies to the old panel, not the destination.
      for (const key of Object.keys(clone)) if (/^(exclude_from_|remove_from_)/.test(key) || falseFlags.has(key)) delete clone[key];
      clone.display_eligible = true;
      clone.match = fields.selected_files ? 'exact_version_arch_for_selected_files' : 'exact_version_arch';
      if (fields.actual_build) clone.build_number = normalizedBuild(fields.actual_build);
      if (fields.actual_language) clone.language = fields.actual_language;
      if (fields.actual_architecture) clone.media_architecture = fields.actual_architecture;
      if (fields.primary_filename) clone.filename = fields.primary_filename;
      clone.moved_from_version_id = change.version_id;
      clone.review_import.destination_version_id = destination;
      moves.push({ destination, channel: change.architecture_panel, source: clone, change_id: change.change_id });
    }
  }
  receipt.changes.push({ change_id: change.change_id, status: 'applied', version_id: change.version_id,
    architecture: change.architecture_panel, operation: change.operation,
    new_source: Boolean(change.append_to), source_page_url: fields.source_page_url || null });
}
for (const move of moves) {
  const panels = data.records[move.destination];
  const channel = panels[move.channel] ? move.channel : panels.default ? 'default' : undefined;
  assert(channel, `No matching destination architecture: ${move.change_id}`);
  panels[channel].push(normalize(move.source, channel, audit.checked_on));
  receipt.moved_candidates.push({ change_id: move.change_id, destination_version_id: move.destination, architecture: channel });
}

const all = Object.values(data.records).flatMap(panels => Object.values(panels).flat());
const countBy = values => values.reduce((counts, value) => ({ ...counts, [value]: (counts[value] || 0) + 1 }), {});
data.summary = { ...data.summary,
  candidate_references: all.length, unique_urls: new Set(all.map(source => source.url)).size,
  minimum_candidates_per_panel: Math.min(...Object.values(data.records).flatMap(panels => Object.values(panels).map(sources => sources.length))),
  verification_status_counts: countBy(all.map(source => source.verification?.status || 'not_checked')),
  kind_counts: countBy(all.map(source => source.kind || 'unclassified')),
};
data.review_import = { reviewed_on: audit.checked_on, imported_on: '2026-10-25',
  audit_file: 'docs/reviewed-windows-sources-2026-10-24.json', receipt_file: 'docs/windows-source-import-2026-10-25.json' };
receipt.summary = { total_changes: audit.changes.length,
  applied_changes: receipt.changes.filter(change => change.status === 'applied').length,
  skipped_user_instructions: receipt.changes.filter(change => change.status === 'skipped_user_instruction').length,
  added_sources: receipt.changes.filter(change => change.new_source).length,
  moved_candidates: moves.length, candidate_references_after: all.length };
assert.equal(receipt.changes.length, audit.changes.length);
await mkdir(backupDirectory, { recursive: true });
await writeFile(path.join(backupDirectory, 'image-sources-before-b-import-2026-10-25.json'), before, { flag: 'wx' });
await writeFile(path.join(root, 'docs/reviewed-windows-sources-2026-10-24.json'), input, { flag: 'wx' });
await writeFile(dataPath, JSON.stringify(data, null, 2) + '\n');
await writeFile(receiptPath, JSON.stringify(receipt, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify(receipt.summary));
