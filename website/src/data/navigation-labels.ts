// Presentation only: catalog names and route IDs are never rewritten.
function displayName(name: string) {
  return name.replace(/^Microsoft (?=Windows\b)/, '');
}

export function relativeName(name: string, prefixes: string[] = []) {
  name = displayName(name);
  for (const prefix of prefixes) {
    const context = displayName(prefix);
    if (context && name.startsWith(context + ' ')) return name.slice(context.length + 1);
  }
  return name;
}

export function versionLabel(name: string, prefixes: string[] = [], parentName = '') {
  if (parentName.startsWith('Windows ') && displayName(parentName) === displayName(name)) return 'Origin';
  let label = relativeName(name, prefixes);
  if (prefixes.includes('Windows IoT')) label = label.replace(/^(\d+) IoT /, '$1 ');
  return label.replace(/\bEnterprise (?=LTS[BC]\b)/g, '');
}
