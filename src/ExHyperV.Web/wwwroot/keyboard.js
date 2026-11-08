// DOM physical key codes -> PC set-1 scan codes. The guest owns layout/IME conversion.
const rows = [
  [0x01, 'Escape'], [0x02, 'Digit1 Digit2 Digit3 Digit4 Digit5 Digit6 Digit7 Digit8 Digit9 Digit0 Minus Equal Backspace Tab'],
  [0x10, 'KeyQ KeyW KeyE KeyR KeyT KeyY KeyU KeyI KeyO KeyP BracketLeft BracketRight Enter ControlLeft'],
  [0x1e, 'KeyA KeyS KeyD KeyF KeyG KeyH KeyJ KeyK KeyL Semicolon Quote Backquote ShiftLeft Backslash'],
  [0x2c, 'KeyZ KeyX KeyC KeyV KeyB KeyN KeyM Comma Period Slash ShiftRight NumpadMultiply AltLeft Space CapsLock'],
  [0x3b, 'F1 F2 F3 F4 F5 F6 F7 F8 F9 F10 NumLock ScrollLock'],
  [0x47, 'Numpad7 Numpad8 Numpad9 NumpadSubtract Numpad4 Numpad5 Numpad6 NumpadAdd Numpad1 Numpad2 Numpad3 Numpad0 NumpadDecimal'],
  [0x56, 'IntlBackslash F11 F12']
];
export const scanCodes = new Map();
for (const [start, names] of rows) names.split(' ').forEach((name, i) => scanCodes.set(name, [start + i, false]));
for (const [name, code] of Object.entries({NumpadEnter:0x1c,ControlRight:0x1d,NumpadDivide:0x35,PrintScreen:0x37,AltRight:0x38,Home:0x47,ArrowUp:0x48,PageUp:0x49,ArrowLeft:0x4b,ArrowRight:0x4d,End:0x4f,ArrowDown:0x50,PageDown:0x51,Insert:0x52,Delete:0x53,MetaLeft:0x5b,MetaRight:0x5c,ContextMenu:0x5d})) scanCodes.set(name,[code,true]);

export function keyboardToggles(event) {
  return (event.getModifierState('ScrollLock') ? 1 : 0) | (event.getModifierState('NumLock') ? 2 : 0)
    | (event.getModifierState('CapsLock') ? 4 : 0) | (event.getModifierState('KanaMode') ? 8 : 0);
}

export function desktopPoint(rect, width, height, clientX, clientY, videoWidth = width, videoHeight = height) {
  if (!width || !height || !rect.width || !rect.height) return null;
  const scale = Math.min(rect.width / videoWidth, rect.height / videoHeight);
  const left = rect.left + (rect.width - videoWidth * scale) / 2;
  const top = rect.top + (rect.height - videoHeight * scale) / 2;
  return {x: Math.max(0, Math.min(width - 1, Math.floor((clientX - left) / (videoWidth * scale) * width))),
    y: Math.max(0, Math.min(height - 1, Math.floor((clientY - top) / (videoHeight * scale) * height)))};
}
