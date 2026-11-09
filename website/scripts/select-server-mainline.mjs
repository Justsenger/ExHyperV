import { readFile, writeFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

const file = new URL('../src/data/catalog.json', import.meta.url);
const catalog = JSON.parse(await readFile(file, 'utf8'));
const server = catalog.categories.find(c => c.name === 'Windows').groups.find(g => g.key === 'server');
const names = [
  'Windows Server 2025', 'Windows Server 2022', 'Windows Server 2019', 'Windows Server 2016',
  'Windows Server 2012 R2', 'Windows Server 2012', 'Windows Server 2008 R2', 'Windows Server 2008',
  'Windows Server 2003 R2', 'Windows Server 2003',
];
const byName = new Map(server.devices.map(record => [record.name, record]));
assert(names.every(name => byName.has(name)), 'Missing mainline record');
server.primary_ids = names.map(name => byName.get(name).codename);
delete server.collections;
delete server.navigation;
catalog.navigation.windows_server_primary_count = names.length;
catalog.navigation.windows_server_order = names;
catalog.navigation.windows_server_style = 'Only year-based mainline releases and R2; other stored records are omitted from navigation.';
await writeFile(file, JSON.stringify(catalog, null, 2) + '\n');
console.log(`Server navigation now contains ${names.length} mainline/R2 links; other records remain stored without directory entries.`);
