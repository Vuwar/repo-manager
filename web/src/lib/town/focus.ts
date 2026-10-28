import { onWindowFocus } from '../host';

/** Must stay well under the daemon's 15 s focus lease (PermissionBroker.DefaultFocusLease). */
export const FOCUS_HEARTBEAT_MS = 5000;

/**
 * Reports whether the town has the user's focus: true now and every FOCUS_HEARTBEAT_MS while the window is
 * focused and the page visible, false at once when either stops. The returned cleanup reports false.
 */
export function startFocusReporting(post: (focused: boolean) => void): () => void {
  let windowFocused = document.hasFocus();
  let last: boolean | null = null;
  let timer: ReturnType<typeof setInterval> | null = null;

  const update = () => {
    const now = windowFocused && document.visibilityState === 'visible';
    if (now === last) return;
    last = now;
    post(now);
    if (timer) clearInterval(timer);
    timer = now ? setInterval(() => post(true), FOCUS_HEARTBEAT_MS) : null;
  };

  const stopWindow = onWindowFocus((f) => {
    windowFocused = f;
    update();
  });
  document.addEventListener('visibilitychange', update);
  update();

  return () => {
    stopWindow();
    document.removeEventListener('visibilitychange', update);
    if (timer) clearInterval(timer);
    timer = null;
    if (last !== false) post(false);
    last = false;
  };
}
