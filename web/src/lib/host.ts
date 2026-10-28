import type { AmbientMode, AmbientState, Rgb } from './ambient';
import type { UiMode } from './simple';

// Bridge to the desktop shell (WebView2). In a plain browser these calls do nothing.
interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (e: { data: unknown }) => void): void;
}

function webview(): WebView | undefined {
  return (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview;
}

/** True inside the RepoManager window, where the shell can resize and pin it. */
export const inDesktopShell = (): boolean => !!webview();

/** Tells the shell which layout is showing, so it can shrink the window for the compact view. */
export function postMode(mode: UiMode): void {
  webview()?.postMessage({ type: 'mode', mode });
}

/**
 * Calls back when the window gains or loses focus: from the desktop shell's activation messages, and from the
 * page's own focus and blur events (both may fire; repeats are harmless). Returns a cleanup.
 */
export function onWindowFocus(cb: (focused: boolean) => void): () => void {
  const view = webview();
  const fromShell = (e: { data: unknown }) => {
    const d = e.data as { type?: string; focused?: unknown } | null;
    if (d?.type === 'windowFocus' && typeof d.focused === 'boolean') cb(d.focused);
  };
  const onFocus = () => cb(true);
  const onBlur = () => cb(false);
  view?.addEventListener('message', fromShell);
  window.addEventListener('focus', onFocus);
  window.addEventListener('blur', onBlur);
  return () => {
    view?.removeEventListener('message', fromShell);
    window.removeEventListener('focus', onFocus);
    window.removeEventListener('blur', onBlur);
  };
}

/** Toggles borderless full screen, or with `on` sets it (the shell ignores it outside the town). */
export function postFullscreen(on?: boolean): void {
  webview()?.postMessage(on === undefined ? { type: 'fullscreen' } : { type: 'fullscreen', on });
}

/** Keeps the window above other windows (compact view only). */
export function postTopmost(on: boolean): void {
  webview()?.postMessage({ type: 'topmost', on });
}

/**
 * Asks the shell for a compact-view background style and reports what it applied
 * (acrylic on or off, color sampled around the window) until the returned cleanup runs.
 */
export function syncAmbient(mode: AmbientMode, onState: (state: AmbientState) => void): () => void {
  const view = webview();
  if (!view) return () => {};
  const listener = (e: { data: unknown }) => {
    const d = e.data as { type?: string; glass?: unknown; rgb?: unknown } | null;
    if (d?.type !== 'ambient') return;
    const rgb = Array.isArray(d.rgb) && d.rgb.length === 3 && d.rgb.every((v) => typeof v === 'number') ? (d.rgb as Rgb) : null;
    onState({ glass: d.glass === true, rgb });
  };
  view.addEventListener('message', listener);
  view.postMessage({ type: 'ambient', mode });
  return () => view.removeEventListener('message', listener);
}

/**
 * Paints the native title bar in the app's own colors (Windows 11 tints the caption via DWM).
 * Sends the current theme now and again whenever the system switches between light and dark.
 * Returns a cleanup that stops listening.
 */
export function syncTitleBar(): () => void {
  const view = webview();
  if (!view) return () => {};
  const send = () => {
    const css = getComputedStyle(document.documentElement);
    const token = (name: string) => css.getPropertyValue(name).trim();
    view.postMessage({
      type: 'titlebar',
      dark: css.colorScheme !== 'light',
      caption: token('--bg-sidebar'),
      text: token('--text-2'),
      border: token('--border-soft'),
    });
  };
  send();
  const media = window.matchMedia('(prefers-color-scheme: light)');
  media.addEventListener('change', send);
  return () => media.removeEventListener('change', send);
}
