import { cpSync, existsSync, lstatSync, mkdirSync, readlinkSync, rmSync, symlinkSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const WEBSITE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SOURCE_DIR = path.resolve(WEBSITE_ROOT, '..', 'src', 'ExHyperV.WoW', 'public', 'ui-design');
const TARGET_DIR = path.join(WEBSITE_ROOT, 'public', 'ui-design');

function linkOrCopyDir(sourceDir, targetDir, preferCopy = false) {
  if (!existsSync(sourceDir)) {
    throw new Error(`Missing ExHyperV.WoW asset directory: ${sourceDir}`);
  }
  mkdirSync(path.dirname(targetDir), { recursive: true });

  const existingStat = lstatSync(targetDir, { throwIfNoEntry: false });
  if (existingStat) {
    if (!preferCopy && existingStat.isSymbolicLink()) {
      const currentTarget = path.resolve(path.dirname(targetDir), readlinkSync(targetDir));
      if (currentTarget === path.resolve(sourceDir)) return;
    }
    rmSync(targetDir, { recursive: true, force: true });
  }

  if (!preferCopy) {
    try {
      const linkType = process.platform === 'win32' ? 'junction' : 'dir';
      symlinkSync(sourceDir, targetDir, linkType);
      return;
    } catch {
      // Fall back to copying when symlinks/junctions are disallowed.
    }
  }
  cpSync(sourceDir, targetDir, { recursive: true, force: true });
}

const preferCopy = Boolean(process.env.CI);
linkOrCopyDir(SOURCE_DIR, TARGET_DIR, preferCopy);
console.log(`[sync-exhyperv-wow] Ready: ${SOURCE_DIR}`);
