import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const catalog = JSON.parse(await readFile(new URL('../src/data/catalog.json',import.meta.url),'utf8'));
const evidence = JSON.parse(await readFile(new URL('../src/data/ubuntu-architecture-evidence.json',import.meta.url),'utf8'));
const ubuntu = catalog.categories.find(category => category.name === 'Ubuntu');
const records = ubuntu.groups.flatMap(group => group.devices);
const byId = new Map(records.map(record => [record.codename,record]));
const proofById = new Map(evidence.records.map(record => [record.id,record]));
assert.equal(records.length,193);
assert.equal(proofById.size,records.length);
for (const record of records) {
  assert(record.architectures?.length,`Missing Ubuntu architecture: ${record.codename}`);
  assert.deepEqual(record.architectures,proofById.get(record.codename)?.architectures,`Architecture evidence mismatch: ${record.codename}`);
  assert(record.architectures.includes(record.default_architecture),`Invalid default architecture: ${record.codename}`);
  for (const architecture of record.architectures) {
    assert(['x86','x64','arm64'].includes(architecture),`Architecture outside Hyper-V catalog scope: ${architecture}`);
    const sources = proofById.get(record.codename).media[architecture];
    assert(sources.length && sources.every(source => {
      const url = new URL(source);
      return url.protocol === 'https:' && /(^|\.)ubuntu\.com$/.test(url.hostname) && !/\+raspi|armhf|powerpc|ppc64|s390|riscv/.test(url.pathname);
    }),`Missing official generic-media evidence: ${record.codename} ${architecture}`);
  }
}
// Keep architecture boundaries specific to the installation media and edition.
assert.deepEqual(byId.get('ubuntu-17-04').architectures,['x86','x64']);
assert.deepEqual(byId.get('ubuntu-18-04').architectures,['x64']);
assert(byId.get('ubuntu-server-17-10').architectures.includes('x86'));
assert(!byId.get('ubuntu-server-18-04').architectures.includes('x86'));
assert(byId.get('ubuntu-server-22-04-5').architectures.includes('arm64'));
assert(!byId.get('ubuntu-22-04-5').architectures.includes('arm64'),'Raspberry Pi desktop images must not imply generic ARM64 desktop media');
assert(byId.get('ubuntu-24-04-3').architectures.includes('arm64'));
assert.deepEqual(byId.get('ubuntu-23-10').architectures,['x64']);
assert.deepEqual(byId.get('ubuntu-24-04-5').architectures,['x64','arm64']);
assert.deepEqual(byId.get('ubuntu-24-04-5-1').architectures,['x64'],'A respun amd64 artifact must not inherit other architectures');
console.log(`Ubuntu audit passed: ${records.length} version records have architecture evidence; edition boundaries and image respins checked.`);
