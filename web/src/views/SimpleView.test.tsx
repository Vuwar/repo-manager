import { render, screen, within } from '@testing-library/react';
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

const { SimpleView } = await import('./SimpleView');
const { dispatch } = await import('../state/store');

function setup() {
  dispatch({ type: 'projectsLoaded', projects: sampleProjects() });
  const onFullView = vi.fn();
  render(<SimpleView onFullView={onFullView} />);
  return { onFullView, user: userEvent.setup() };
}

describe('SimpleView', () => {
  beforeEach(() => {
    localStorage.clear();
    serviceAction.mockClear();
    instanceAction.mockClear();
  });

  it('opens running repos as tabs and lists every repo under All repos', () => {
    setup();
    const tabs = screen.getByRole('navigation', { name: 'Repos' });
    expect(within(tabs).getByRole('button', { name: /^panel-pro/ })).toBeTruthy();
    expect(within(tabs).getByRole('button', { name: /^other/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Start web' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Stop api' })).toBeTruthy();
    expect(screen.queryByText('docs')).toBeNull();
  });

  it('starts the only matching service on Enter', async () => {
    const { user } = setup();
    await user.type(screen.getByRole('textbox', { name: 'Find a repo or service' }), 'web');
    expect(screen.getByText('Enter starts web')).toBeTruthy();
    await user.keyboard('{Enter}');
    expect(serviceAction).toHaveBeenCalledWith('start', 'panel-pro/web');
  });

  it('shows a repo tab with its worktrees and stop all', async () => {
    const { user } = setup();
    await user.click(within(screen.getByRole('navigation', { name: 'Repos' })).getByRole('button', { name: /^panel-pro/ }));
    expect(screen.getByRole('heading', { level: 1, name: 'panel-pro' })).toBeTruthy();
    expect(screen.getByText('1 more worktree')).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Stop all' }));
    expect(instanceAction).toHaveBeenCalledWith('stop', 'panel-pro');
  });

  it('closing a tab keeps it closed while the repo keeps running', async () => {
    const { user } = setup();
    await user.click(screen.getByRole('button', { name: 'Close other tab' }));
    dispatch({ type: 'projectsLoaded', projects: sampleProjects() });
    expect(within(screen.getByRole('navigation', { name: 'Repos' })).queryByRole('button', { name: /^other/ })).toBeNull();
  });
});
