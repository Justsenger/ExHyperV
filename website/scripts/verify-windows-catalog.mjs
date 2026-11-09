import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { relativeName, versionLabel } from '../src/data/navigation-labels.ts';

const catalog = JSON.parse(await readFile(new URL('../src/data/catalog.json', import.meta.url), 'utf8'));
const windows = catalog.categories.find(c => c.name === 'Windows');
const desktop = windows.groups.find(g => g.key === 'desktop');
const records = windows.groups.flatMap(g => g.devices);
const byName = new Map(records.map(v => [v.name, v]));
let leaves = 0;
let originals = 0;
function checkLeaf(record, prefixes, parentName = '', labelOverride) {
  assert(record, 'Navigation refers to a nonexistent version');
  const label = labelOverride || versionLabel(record.name, prefixes, parentName);
  assert(label.trim(), `Empty label: ${record.name}`);
  if (parentName) assert.notEqual(label, relativeName(parentName, prefixes.slice(1)), `Repeated parent label: ${record.name}`);
  if (record.name === parentName) {
    assert.equal(label, 'Origin');
    originals++;
  }
  leaves++;
}
for (const group of windows.groups) {
  const byId = new Map(group.devices.map(v => [v.codename, v]));
  const visible = [];
  for (const id of group.primary_ids ?? group.devices.map(v => v.codename)) {
    checkLeaf(byId.get(id), [group.name, windows.name]);
    visible.push(id);
  }
  for (const collection of group.collections ?? []) {
    assert(!collection.variant_groups, 'Architecture branches must not appear in the version directory');
    const walk = (nodes, prefixes, parentName) => nodes.flatMap(node => {
      if (node.version_id) {
        checkLeaf(byId.get(node.version_id), prefixes, parentName, node.label);
        return [node.version_id];
      }
      assert(node.children.length > 1, `Unnecessary single-item dropdown: ${node.name}`);
      return walk(node.children, [node.name, ...prefixes], node.name);
    });
    const ids = collection.navigation
      ? walk(collection.navigation, [collection.name, group.name, windows.name], collection.name)
      : [...(collection.variant_groups ? walk(collection.variant_groups, [collection.name, group.name, windows.name], collection.name) : []), ...(collection.primary_ids || collection.version_ids).map(id => {
        checkLeaf(byId.get(id), collection.version_ids.length === 1 ? [group.name, windows.name] : [collection.name, group.name, windows.name], collection.version_ids.length === 1 ? '' : collection.name);
        return id;
      })];
    assert.deepEqual([...ids].sort(), [...collection.version_ids].sort(), `Incorrect nested membership: ${collection.name}`);
    visible.push(...ids);
  }
  assert.equal(new Set(visible).size, visible.length, `Duplicate directory entry in ${group.name}`);
  if (group.key !== 'server') assert.equal(visible.length, group.devices.length, `Unlisted version in ${group.name}`);
  else assert.equal(visible.length, 10, 'Server must retain the ten mainline/R2 releases');
}

// Published service-pack inventories, rather than the lifecycle export's
// cross-product edition/service-pack combinations (not reliable for XP x64).
for (const [base, packs] of [
  ['Windows 7', ['1']], ['Windows Vista', ['1','2']],
  ['Windows XP', ['1','1a','2','3']],
  ['Windows XP Professional x64 Edition', ['2']],
  ['Windows 2000 Professional', ['1','2','3','4']],
  ['Windows NT Workstation 4.0', ['1','2','3','4','5','6','6a']],
  ['Windows NT Workstation 3.51', ['1','2','3','4','5']],
  ['Windows NT Workstation 3.5', ['1','2','3']],
  ['Windows NT 3.1', ['1','2','3']],
]) {
  assert(byName.has(base), `Missing original release: ${base}`);
  const actual = desktop.devices.filter(v => v.name.startsWith(`${base} Service Pack `)).map(v => v.name.slice(`${base} Service Pack `.length));
  assert.deepEqual(actual.sort(), [...packs].sort(), `Incorrect service-pack set: ${base}`);
}
for (const [major, releases] of [
  ['11', ['21H2','22H2','23H2','24H2','25H2','26H1','26H2']],
  ['10', ['1507','1511','1607','1703','1709','1803','1809','1903','1909','2004','20H2','21H1','21H2','22H2']],
]) {
  for (const version of releases) assert(byName.has(`Windows ${major} ${version}`), `Missing Desktop release ${major} ${version}`);
}
assert.equal(versionLabel('Microsoft Windows XP Media Center Edition 2004', ['Windows XP','Windows Desktop','Windows'], 'Windows XP'), 'Media Center Edition 2004');
assert.equal(new Set(records.map(v => v.name)).size, records.length, 'Duplicate canonical product names');
assert(!records.some(v => /\b(?:Phone|Mobile)\b/i.test(v.name)), 'Phone/Mobile products are excluded');
assert(!records.some(record => record.base_version_id), 'Architecture aliases must not be counted as versions');
const mergedIds = new Set(['windows-10-team-surface-hub', ...catalog.version_aliases.map(alias => alias.codename)]);
assert(!records.some(record => mergedIds.has(record.codename)), 'Approved editions must not remain as independent Windows records');
assert(!catalog.edition_groups && !catalog.edition_aliases, 'Product-edition selection data must be removed');
assert.equal(catalog.version_aliases.length, 36, 'Former edition links must resolve to their matching build');
for (const alias of catalog.version_aliases) {
  const base = records.find(record => record.codename === alias.base_version_id);
  assert(base?.build_number, `Missing build destination ${alias.codename}`);
  assert(!alias.edition, 'Compatibility links must not select an edition');
}
for (const record of records) {
  assert(!record.architectures?.includes('arm32'), `ARM32 is excluded: ${record.name}`);
  assert(!record.codename.includes('arm32'), `Architecture-specific ARM32 route remains: ${record.codename}`);
}
assert.equal(leaves, catalog.windows_build_audit.visible_windows_records, 'Windows navigation does not match the approved build audit');
for (const alias of catalog.architecture_aliases) assert(records.some(base => base.codename === alias.base_version_id && base.architectures.includes(alias.architecture)), `Invalid architecture alias ${alias.codename}`);
for (const [major, versions] of [
  ['10', ['1511','1703','1709','1803','1809','1903','1909','2004','20H2','21H1','21H2','22H2']],
  ['11', ['21H2','22H2','23H2','24H2','25H2','26H1','26H2']],
]) {
  for (const version of versions) assert(byName.get(`Windows ${major} ${version}`).architectures.includes('arm64'), `Missing Windows ${major} ARM64 ${version}`);
}
assert.deepEqual(byName.get('Windows 11 26H1').architectures, ['x64','arm64']);
assert.equal(byName.get('Windows 11 26H1').architecture_channels.x64, 'insider');
assert.deepEqual(byName.get('Windows 11 24H2').architectures, ['x64','arm64']);
assert.deepEqual(byName.get('Windows 10 22H2').architectures, ['x86','x64','arm64']);
for (const name of ['Windows 8', 'Windows 8.1', 'Windows 8.1 Update', 'Windows Vista', 'Windows Vista Service Pack 1', 'Windows Vista Service Pack 2', 'Windows 7', 'Windows 7 Service Pack 1']) {
  assert.deepEqual(byName.get(name).architectures, ['x86', 'x64'], `Missing architecture choices for ${name}`);
}
for (const version of desktop.devices) {
  assert(version.architectures?.length, `Missing Desktop architecture: ${version.name}`);
  assert(version.architectures.includes(version.default_architecture), `Invalid default architecture: ${version.name}`);
  if (/^Windows XP Professional x64 Edition/.test(version.name)) assert.deepEqual(version.architectures, ['x64']);
}
assert(!records.some(record => /^Windows RT(?: |$)/.test(record.name)), 'Windows RT products are excluded from this Hyper-V catalog');
assert(byName.get('Windows 10 IoT Core 1809').architectures.includes('x86'), 'IoT Core is not ARM-only');
const buildEvidence = JSON.parse(await readFile(new URL('../src/data/windows-build-evidence.json',import.meta.url),'utf8'));
const buildsById = new Map(buildEvidence.records.map(record=>[record.id,record]));
const pending = new Set(catalog.windows_build_audit.unverified_build_ids);
assert.equal(records.length,catalog.windows_build_audit.windows_records);
assert.equal(buildsById.size,records.length);
assert.equal(pending.size,10);
for(const record of records) {
  assert(Object.hasOwn(record,'build_number'), `Missing build field: ${record.name}`);
  const proof=buildsById.get(record.codename);
  assert(proof,`Missing build provenance: ${record.name}`);
  assert.deepEqual(record.build_number,proof.build_number,`Build provenance mismatch: ${record.name}`);
  if(record.build_number===null) {
    assert(pending.has(record.codename)&&record.build_status==='unverified',`Unexplained missing build: ${record.name}`);
  } else {
    const values=Array.isArray(record.build_number)?record.build_number:[record.build_number];
    assert(values.length&&values.every(value=>/^\d+$/.test(value)),`Product/kit version substituted for build: ${record.name}`);
    assert(record.build_sources?.length,`Build lacks sources: ${record.name}`);
  }
}
assert.equal(byName.get('Windows 11 26H1').build_number,'28000');
assert.equal(byName.get('Windows Server 2008').build_number,'6001');
assert(!windows.groups.some(group=>group.key==='embedded'),'Deleted Embedded category must not return');
assert(!records.some(record=>record.codename==='windows-thin-pc'||/^windows-(?:xp-embedded|embedded-|fundamentals-for-legacy-pcs)/.test(record.codename)),'Deleted Embedded records must not return');
assert.deepEqual(byName.get('Windows Small Business Server 2011').build_number,['7600','7601']);
for(const group of windows.groups) {
  const visibleIds=new Set([...(group.primary_ids??group.devices.map(record=>record.codename)),...(group.collections||[]).flatMap(collection=>collection.version_ids)]);
  for(const record of group.devices.filter(record=>visibleIds.has(record.codename))) {
    assert(record.architectures?.length,`Visible Windows entry lacks architecture: ${record.name}`);
    assert(record.architectures.includes(record.default_architecture),`Invalid architecture default: ${record.name}`);
  }
}
assert.deepEqual(byName.get('Windows PE 10.0.22000').architectures,['x86','x64','arm64']);
assert.deepEqual(byName.get('Windows PE 10.0.20348').architectures,['x86','x64']);
assert.deepEqual(byName.get('Windows PE 10.0.17763').architectures,['x86','x64','arm64']);
assert.deepEqual(byName.get('Windows PE 10.0.22621').architectures,['x64','arm64']);
console.log(`Windows audit passed: ${leaves} visible entries, ${originals} unambiguous original-release labels, formal SP/feature inventories and Server scope checked.`);
