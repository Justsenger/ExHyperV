import catalog from './catalog.json';

export const categories = catalog.categories;
export const versions = categories.flatMap(category => category.groups.flatMap(group =>
  group.devices.map(version => ({ ...version, family: category.name, branch: group.name }))
));
export const versionRoutes = [...versions, ...[...catalog.version_aliases, ...catalog.architecture_aliases].map(alias => {
  const base = versions.find(version => version.codename === alias.base_version_id)!;
  return { ...base, ...alias };
})];
