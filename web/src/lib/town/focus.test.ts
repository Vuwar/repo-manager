import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FOCUS_HEARTBEAT_MS, startFocusReporting } from './focus';

let focused = true;
let visibility: DocumentVisibilityState = 'visible';

function setVisibility(v: DocumentVisibilityState) {
  visibility = v;
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('startFocusReporting', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    focused = true;
    visibility = 'visible';
    vi.spyOn(document, 'hasFocus').mockImplementation(() => focused);
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('posts true now and every 5 s while focused', () => {
    const post = vi.fn();
    const stop = startFocusReporting(post);
    expect(post.mock.calls).toEqual([[true]]);
    vi.advanceTimersByTime(FOCUS_HEARTBEAT_MS * 3);
    expect(post.mock.calls).toEqual([[true], [true], [true], [true]]);
    stop();
  });

  it('posts false at once on blur and stops the heartbeat', () => {
    const post = vi.fn();
    const stop = startFocusReporting(post);
    focused = false;
    window.dispatchEvent(new Event('blur'));
    expect(post).toHaveBeenLastCalledWith(false);
    post.mockClear();
    vi.advanceTimersByTime(FOCUS_HEARTBEAT_MS * 3);
    expect(post).not.toHaveBeenCalled();
    focused = true;
    window.dispatchEvent(new Event('focus'));
    expect(post).toHaveBeenLastCalledWith(true);
    stop();
  });

  it('posts false when the page is hidden, true again when visible and focused', () => {
    const post = vi.fn();
    const stop = startFocusReporting(post);
    setVisibility('hidden');
    expect(post).toHaveBeenLastCalledWith(false);
    setVisibility('visible');
    expect(post).toHaveBeenLastCalledWith(true);
    stop();
  });

  it('cleanup posts false and stops', () => {
    const post = vi.fn();
    startFocusReporting(post)();
    expect(post).toHaveBeenLastCalledWith(false);
    post.mockClear();
    vi.advanceTimersByTime(FOCUS_HEARTBEAT_MS * 2);
    window.dispatchEvent(new Event('focus'));
    expect(post).not.toHaveBeenCalled();
  });

  it('starts unfocused: posts false, no heartbeat', () => {
    focused = false;
    const post = vi.fn();
    const stop = startFocusReporting(post);
    expect(post.mock.calls).toEqual([[false]]);
    vi.advanceTimersByTime(FOCUS_HEARTBEAT_MS * 2);
    expect(post.mock.calls).toEqual([[false]]);
    stop();
  });

  it('follows window focus messages from the desktop shell', () => {
    const listeners: ((e: { data: unknown }) => void)[] = [];
    (window as unknown as { chrome: unknown }).chrome = {
      webview: { postMessage: () => {}, addEventListener: (_: string, l: (e: { data: unknown }) => void) => listeners.push(l), removeEventListener: () => {} },
    };
    const post = vi.fn();
    const stop = startFocusReporting(post);
    listeners.forEach((l) => l({ data: { type: 'windowFocus', focused: false } }));
    expect(post).toHaveBeenLastCalledWith(false);
    stop();
    delete (window as unknown as { chrome?: unknown }).chrome;
  });
});
