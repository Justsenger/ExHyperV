import { taskManagerApp } from '../task-manager';
import type {
  WinAppHostContext,
  WinAppHostHooks,
  WinAppLifecycle,
  WinAppModule,
  WinAppShellApi,
  WinAppLaunchArgs,
} from '../types';
import { getManifestById } from '../registry';
import { APP_COMMANDS } from './commands';
import { mountIframeApp } from './iframe-host';
import { placeholderApp } from './placeholder-app';
import { settingsApp } from './settings-app';

const BUILTIN_APP_MODULES: Record<string, WinAppModule> = {
  'task-manager': taskManagerApp,
  settings: settingsApp,
};

export function executeCommandApp(
  appId: string,
  shell: WinAppShellApi,
  args?: WinAppLaunchArgs,
): boolean {
  const handler = APP_COMMANDS[appId];
  if (!handler) return false;
  handler(shell, args);
  return true;
}

export function mountAppInstance(
  ctx: WinAppHostContext,
  hooks: WinAppHostHooks = {},
): WinAppLifecycle {
  const manifest = getManifestById(ctx.appId);
  if (!manifest) {
    return placeholderApp.mount(ctx) ?? {};
  }

  if (manifest.runtime === 'iframe') {
    return mountIframeApp(ctx, hooks);
  }

  const mod = BUILTIN_APP_MODULES[manifest.id] ?? placeholderApp;
  return mod.mount(ctx) ?? {};
}
