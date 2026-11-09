import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';

// Poll source content rather than build directories. Works on Windows and Linux,
// handles editor atomic saves, and ignores the Markdown blocks we generate ourselves.
export async function sourceFingerprint(root) {
  const files = [];
  async function visit(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const filename = path.join(directory, entry.name);
      if (entry.isDirectory() && !['bin', 'obj', 'external', '.git'].includes(entry.name.toLowerCase())) await visit(filename);
      else if (entry.isFile() && /\.(cs|csproj|props|targets|resx|xml|html|css|md|mjs|woff2|txt)$/i.test(entry.name)) files.push(filename);
    }
  }
  await visit(root);
  const hash = createHash('sha256');
  for (const file of files.sort()) {
    const relative = path.relative(root, file).replaceAll('\\', '/');
    let content = await readFile(file);
    if (relative === 'API.md') content = Buffer.from(content.toString('utf8').replace(/\r\n/g, '\n')
      .replace(/<!-- api-(reference|examples|example-[\w-]+):start -->[\s\S]*?<!-- api-\1:end -->/g, ''));
    hash.update(relative + '\0' + content.length + '\0');
    hash.update(content);
  }
  return hash.digest('hex');
}

export async function watchCore({ root, generate, onSuccess = () => {}, onError = console.error, interval = 1500 }) {
  let lastAttempt;
  let closed = false;
  let timer;
  async function update(initial = false) {
    try {
      const fingerprint = await sourceFingerprint(root);
      if (fingerprint !== lastAttempt) {
        // Record the input before generation so edits during a build get another pass.
        lastAttempt = fingerprint;
        await generate();
        if (!closed) onSuccess();
      }
    } catch (error) {
      if (initial) throw error;
      onError(error);
    }
  }
  async function tick() {
    await update();
    if (!closed) timer = setTimeout(tick, interval);
  }
  await update(true);
  timer = setTimeout(tick, interval);
  return () => { closed = true; clearTimeout(timer); };
}
