import { render, screen } from '@testing-library/react';
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
