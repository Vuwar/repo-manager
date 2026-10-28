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
