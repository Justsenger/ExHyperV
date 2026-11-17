import type { WinCommandHandler } from '../types';

export const APP_COMMANDS: Record<string, WinCommandHandler> = {
  fullscreen: (shell) => {
    shell.toggleFullscreen();
  },
};
