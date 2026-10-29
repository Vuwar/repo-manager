import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';

describe('App without a token', () => {
  const fetchSpy = vi.fn();

  beforeEach(() => {
    sessionStorage.clear();
    window.history.replaceState(null, '', '/');
    vi.stubGlobal('fetch', fetchSpy);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    fetchSpy.mockReset();
  });

  it('shows the "open from the tray" screen and makes no API calls', () => {
    render(<App />);
    expect(screen.getByRole('heading', { name: 'Open RepoManager from the tray icon' })).toBeTruthy();
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});

describe('token handling', () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  it('reads the token from the hash, stores it and strips it from the URL', async () => {
    window.history.replaceState(null, '', '/#token=abc123');
    const { initToken } = await import('./api/token');
    expect(initToken()).toBe('abc123');
    expect(window.location.hash).toBe('');
    expect(sessionStorage.getItem('repomanager.token')).toBe('abc123');
    // a reload (no hash) still finds it
    expect(initToken()).toBe('abc123');
  });
});

describe('window title', () => {
  beforeEach(() => {
    sessionStorage.setItem('repomanager.token', 'tok');
    window.history.replaceState(null, '', '/');
    // Every API call answers with an empty list; the event stream never opens.
    vi.stubGlobal('fetch', (url: string) =>
      String(url).startsWith('/api/events') ? new Promise(() => {}) : Promise.resolve(new Response('[]', { status: 200 })),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    sessionStorage.clear();
  });

  it('reloads the session list every 30 s while visible', async () => {
    vi.useFakeTimers();
    const calls: string[] = [];
    vi.stubGlobal('fetch', (url: string) => {
      calls.push(String(url));
      return String(url).startsWith('/api/events') ? new Promise(() => {}) : Promise.resolve(new Response('[]', { status: 200 }));
    });
    render(<App />);
    const sessions = () => calls.filter((u) => u.startsWith('/api/town/sessions')).length;
    const before = sessions();
    await act(async () => void vi.advanceTimersByTime(30_000));
    expect(sessions()).toBeGreaterThan(before);
    vi.useRealTimers();
  });

  it('shows the needs-you count', async () => {
    const { dispatch } = await import('./state/store');
    const { agent } = await import('./test/fixtures');
    render(<App />);
    act(() => dispatch({ type: 'agentsLoaded', agents: [agent('w1', { state: 'waiting' }), agent('w2', { state: 'waiting' })] }));
    expect(document.title).toBe('(2) RepoManager');
    act(() => dispatch({ type: 'agentEnd', id: 'w1' }));
    expect(document.title).toBe('(1) RepoManager');
  });
});

describe('shell commands', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    sessionStorage.clear();
    localStorage.clear();
    delete (window as unknown as { chrome?: unknown }).chrome;
  });

  it('a notification click from the shell opens that worker in the town', async () => {
    sessionStorage.setItem('repomanager.token', 'tok');
    localStorage.setItem('repomanager.mode', JSON.stringify('simple'));
    const listeners: ((e: { data: unknown }) => void)[] = [];
    (window as unknown as { chrome: unknown }).chrome = {
      webview: { postMessage: () => {}, addEventListener: (_: string, l: (e: { data: unknown }) => void) => listeners.push(l), removeEventListener: () => {} },
    };
    vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }));
    vi.stubGlobal('fetch', (url: string) =>
      String(url).startsWith('/api/events') ? new Promise(() => {}) : Promise.resolve(new Response('[]', { status: 200 })),
    );
    const { dispatch } = await import('./state/store');
    const { agent } = await import('./test/fixtures');
    render(<App />);
    act(() => dispatch({ type: 'agentsLoaded', agents: [agent('n1', { folder: 'tool', cwd: 'C:\t\tool', state: 'waiting' })] }));
    act(() => listeners.forEach((l) => l({ data: { type: 'openSession', id: 'n1' } })));
    expect(document.querySelector('.town')).toBeTruthy();
    expect(screen.getByRole('dialog').getAttribute('aria-label')).toBe('TOOL · Session A');
  });
});

describe('shell handshake', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    sessionStorage.clear();
    delete (window as unknown as { chrome?: unknown }).chrome;
  });

  it('tells the shell the page is ready, so it can deliver a waiting notification click', () => {
    sessionStorage.setItem('repomanager.token', 'tok');
    const posted: unknown[] = [];
    (window as unknown as { chrome: unknown }).chrome = {
      webview: { postMessage: (m: unknown) => posted.push(m), addEventListener: () => {}, removeEventListener: () => {} },
    };
    vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }));
    vi.stubGlobal('fetch', (url: string) =>
      String(url).startsWith('/api/events') ? new Promise(() => {}) : Promise.resolve(new Response('[]', { status: 200 })),
    );
    render(<App />);
    expect(posted).toContainEqual({ type: 'ready' });
  });
});
