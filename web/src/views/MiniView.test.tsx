import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { sampleProjects } from '../test/fixtures';

const serviceAction = vi.fn(() => Promise.resolve({ ok: true, affected: [], logTail: [] }));
const instanceAction = vi.fn(() => Promise.resolve({ ok: true, affected: [], logTail: [] }));
vi.mock('../state/actions', () => ({
  serviceAction: (...a: unknown[]) => serviceAction(...(a as [])),
  instanceAction: (...a: unknown[]) => instanceAction(...(a as [])),
  loadProjects: vi.fn(),
  dismissToast: vi.fn(),
}));

const { MiniView } = await import('./MiniView');
const { dispatch } = await import('../state/store');

function setup() {
  dispatch({ type: 'projectsLoaded', projects: sampleProjects() });
  const onExpand = vi.fn();
  render(<MiniView onExpand={onExpand} />);
  return { onExpand, user: userEvent.setup() };
}

describe('MiniView', () => {
  beforeEach(() => {
    localStorage.clear();
    serviceAction.mockClear();
    instanceAction.mockClear();
  });

  it('starts and stops services with one click', async () => {
    const { user } = setup();
    await user.click(screen.getByRole('button', { name: 'Start web' }));
    expect(serviceAction).toHaveBeenCalledWith('start', 'panel-pro/web');
    await user.click(screen.getByRole('button', { name: 'Stop api' }));
    expect(serviceAction).toHaveBeenCalledWith('stop', 'panel-pro/api');
    expect(screen.getByRole('link', { name: ':5080' })).toBeTruthy();
    expect(screen.queryByText('docs')).toBeNull();
  });

  it('stops a whole checkout and expands back to the big view', async () => {
    const { user, onExpand } = setup();
    await user.click(screen.getByRole('button', { name: 'Stop all' }));
    expect(instanceAction).toHaveBeenCalledWith('stop', 'panel-pro');
    await user.click(screen.getByRole('button', { name: 'Expand' }));
    expect(onExpand).toHaveBeenCalled();
  });

  it('Enter in the search starts the only matching stopped service', async () => {
    const { user } = setup();
    await user.type(screen.getByRole('textbox', { name: 'Find a service' }), 'web{Enter}');
    expect(serviceAction).toHaveBeenCalledWith('start', 'panel-pro/web');
  });

  it('cycles the background style and applies what the shell reports', async () => {
    const listeners: ((e: { data: unknown }) => void)[] = [];
    const postMessage = vi.fn();
    const webview = {
      postMessage,
      addEventListener: (_: string, l: (e: { data: unknown }) => void) => listeners.push(l),
      removeEventListener: (_: string, l: (e: { data: unknown }) => void) => listeners.splice(listeners.indexOf(l), 1),
    };
    (window as unknown as { chrome?: unknown }).chrome = { webview };
    try {
      const { user } = setup();
      expect(postMessage).toHaveBeenCalledWith({ type: 'ambient', mode: 'glass' });
      await user.click(screen.getByRole('button', { name: 'Background: Glass' }));
      expect(postMessage).toHaveBeenCalledWith({ type: 'ambient', mode: 'match' });
      expect(localStorage.getItem('repomanager.mini.ambient')).toBe('"match"');

      act(() => listeners.forEach((l) => l({ data: { type: 'ambient', glass: false, rgb: [235, 240, 245] } })));
      const root = document.querySelector('.mini') as HTMLElement;
      expect(root.classList.contains('is-matched')).toBe(true);
      expect(root.style.getPropertyValue('--bg')).toBe('#ebf0f5');
    } finally {
      delete (window as unknown as { chrome?: unknown }).chrome;
    }
  });
});
