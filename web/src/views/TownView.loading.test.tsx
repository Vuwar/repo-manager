import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../state/actions', () => ({ loadProjects: vi.fn(), dismissToast: vi.fn() }));
vi.mock('../api/client', () => ({
  api: { townFocus: vi.fn(() => Promise.resolve({ ok: true })), townSettings: vi.fn(() => new Promise(() => {})) },
}));

// A fresh store: projects not loaded yet (the app opened straight into the town).
const { TownView } = await import('./TownView');

describe('TownView while projects load', () => {
  it('does not claim there are no projects', () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(960);
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(540);
    render(<TownView onLeave={() => {}} />);
    expect(screen.queryByText(/No projects yet/)).toBeNull();
    expect(screen.getByText('Loading the town…')).toBeTruthy();
  });
});
