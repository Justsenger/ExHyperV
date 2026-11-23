import { execFileSync } from 'node:child_process';
import { cpSync, existsSync, lstatSync, mkdirSync, readlinkSync, rmSync, symlinkSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const WEBSITE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const REPO_ROOT = path.resolve(WEBSITE_ROOT, '..');
const DEFAULT_SIBLING = path.resolve(REPO_ROOT, '..', 'WinOnWeb');
const CACHE_CLONE = path.join(WEBSITE_ROOT, '.cache', 'winonweb');
const REMOTE_URL = process.env.WINONWEB_REPO || 'https://github.com/Justsenger/WinOnWeb.git';
const REMOTE_REF = process.env.WINONWEB_REF || 'main';

function hasShellSource(dir) {
  return (
    Boolean(dir) &&
    existsSync(path.join(dir, 'src', 'shells', 'win11')) &&
    existsSync(path.join(dir, 'src', 'wow'))
  );
}

function resolveSourceRoot() {
  const explicit = process.env.WINONWEB_DIR ? path.resolve(process.env.WINONWEB_DIR) : null;
  if (hasShellSource(explicit)) {
    return { dir: explicit, mode: 'env' };
  }
  if (process.env.WINONWEB_REMOTE !== '1' && hasShellSource(DEFAULT_SIBLING)) {
    return { dir: DEFAULT_SIBLING, mode: 'local-sibling' };
  }

  mkdirSync(path.dirname(CACHE_CLONE), { recursive: true });
  if (existsSync(path.join(CACHE_CLONE, '.git'))) {
    try {
      execFileSync('git', ['-C', CACHE_CLONE, 'fetch', '--depth', '1', 'origin', REMOTE_REF], {
        stdio: 'inherit',
      });
      execFileSync('git', ['-C', CACHE_CLONE, 'checkout', '-f', 'FETCH_HEAD'], { stdio: 'inherit' });
    } catch (error) {
      if (!process.env.CI && hasShellSource(CACHE_CLONE)) {
        console.warn(`[sync-winonweb] Unable to reach ${REMOTE_URL}; using cached checkout.`);
        return { dir: CACHE_CLONE, mode: 'cached-offline' };
      }
      throw error;
    }
  } else {
    rmSync(CACHE_CLONE, { recursive: true, force: true });
    execFileSync('git', ['clone', '--depth', '1', '--branch', REMOTE_REF, REMOTE_URL, CACHE_CLONE], {
      stdio: 'inherit',
    });
  }
  return { dir: CACHE_CLONE, mode: 'remote-clone' };
}

function linkOrCopyDir(sourceDir, targetDir, preferCopy = false) {
  if (!existsSync(sourceDir)) {
    throw new Error(`Missing WinOnWeb directory: ${sourceDir}`);
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

const { dir: winOnWebDir, mode } = resolveSourceRoot();
if (mode === 'local-sibling' || mode === 'env') {
  rmSync(CACHE_CLONE, { recursive: true, force: true });
}
const preferCopy = Boolean(process.env.CI) || mode === 'remote-clone' || mode === 'cached-offline';
rmSync(path.join(WEBSITE_ROOT, 'src', 'win11-shell'), { recursive: true, force: true });
linkOrCopyDir(path.join(winOnWebDir, 'src', 'shells', 'win11'), path.join(WEBSITE_ROOT, 'src', 'shells', 'win11'), preferCopy);
linkOrCopyDir(path.join(winOnWebDir, 'src', 'wow'), path.join(WEBSITE_ROOT, 'src', 'wow'), preferCopy);
linkOrCopyDir(path.join(winOnWebDir, 'public', 'win11'), path.join(WEBSITE_ROOT, 'public', 'win11'), preferCopy);
linkOrCopyDir(path.join(winOnWebDir, 'public', 'wow'), path.join(WEBSITE_ROOT, 'public', 'wow'), preferCopy);
linkOrCopyDir(path.join(winOnWebDir, 'public', 'wow-bundles'), path.join(WEBSITE_ROOT, 'public', 'wow-bundles'), preferCopy);
linkOrCopyDir(path.join(winOnWebDir, 'public', 'packages'), path.join(WEBSITE_ROOT, 'public', 'packages'), preferCopy);

console.log(`[sync-winonweb] Ready (${mode}): ${winOnWebDir}`);
