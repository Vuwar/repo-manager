import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { agent, instance, project, svc } from '../test/fixtures';

vi.mock('../state/actions', () => ({ loadProjects: vi.fn(), dismissToast: vi.fn() }));
const ok = { ok: true, affected: [], logTail: [] };
const api = {
  townFocus: vi.fn(() => Promise.resolve(ok)),
  townSend: vi.fn(() => Promise.resolve(ok)),
  townAnswer: vi.fn(() => Promise.resolve(ok)),
  townOpen: vi.fn(() => Promise.resolve(ok)),
  townOpenHost: vi.fn(() => Promise.resolve(ok)),
};
vi.mock('../api/client', () => ({ api }));
const { workerSpot } = await import('../lib/town/render');

const { TownView } = await import('./TownView');
const { dispatch } = await import('../state/store');

const p = (name: string) => project(name, [instance(name, [svc(name, 'web')])]);

/** Lot names in street order, as the minimap lists them. */
const order = () => screen.getAllByRole('button', { name: /^Go to / }).map((b) => b.getAttribute('aria-label')!.slice(6));
/** Names of the lots whose labels are on screen. */
const labels = () => [...document.querySelectorAll('.town-lot-name')].map((e) => e.textContent);

function setup(projects = [p('alpha'), p('beta'), p('gamma')], agents = [] as ReturnType<typeof agent>[]) {
  dispatch({ type: 'projectsLoaded', projects });
  dispatch({ type: 'agentsLoaded', agents });
  const onLeave = vi.fn();
  render(<TownView onLeave={onLeave} />);
  return { onLeave, stage: document.querySelector('.town-stage') as HTMLElement };
}

describe('TownView', () => {
  beforeEach(() => {
    // 960 x 540 CSS px: zoom 3, so 320 logical px are visible (about 6 lots).
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(960);
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(540);
    const ctx = new Proxy({}, { get: () => () => {}, set: () => true });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(ctx as never);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('shows a label per lot in urgency order', () => {
    setup(undefined, [agent('1', { project: 'gamma', state: 'waiting' }), agent('2', { project: 'alpha', state: 'working' })]);
    expect(labels()).toEqual(['gamma', 'alpha', 'beta']);
  });

  it('puts labels below the road, as wide as a lot', () => {
    setup();
    const label = document.querySelector('.town-label') as HTMLElement;
    // zoom 3: the road ends at y 168
    expect(parseFloat(label.style.top)).toBeGreaterThanOrEqual(168 * 3);
    const name = document.querySelector('.town-lot-name') as HTMLElement;
    expect(name.style.maxWidth).toBe(`${(51 - 4) * 3}px`);
  });

  it('marks temporary lots', () => {
    setup([], [agent('1', { folder: 'tool', cwd: 'C:\\x\\tool', state: 'working' })]);
    expect(screen.getByText('not in RepoManager')).toBeTruthy();
    expect(labels()).toEqual(['tool']);
  });

  it('shows a worktree sign', () => {
    setup(undefined, [agent('1', { project: 'alpha', worktree: 'feat-x', state: 'working' })]);
    expect(screen.getByText('feat-x')).toBeTruthy();
  });

  it('shows +N past five workers', () => {
    const many = Array.from({ length: 7 }, (_, i) => agent('w' + i, { project: 'alpha', state: 'working' }));
    setup(undefined, many);
    expect(screen.getByText('+2')).toBeTruthy();
  });

  it('names off-screen sessions in an edge arrow that jumps there', () => {
    const projects = Array.from({ length: 40 }, (_, i) => p('p' + String(i).padStart(2, '0')));
    // Seven waiting lots fill the view; the working one sorts eighth, off the right edge.
    const waiting = Array.from({ length: 7 }, (_, i) => agent('w' + i, { project: 'p' + String(i + 10), state: 'waiting' }));
    setup(projects, [...waiting, agent('x', { project: 'p39', state: 'working' })]);
    expect(labels()).not.toContain('p39');
    fireEvent.click(screen.getByRole('button', { name: '1 working →' }));
    expect(labels()).toContain('p39');
    // Now the waiting lots are off the left edge.
    expect(screen.getByRole('button', { name: /^← \d need you$/ })).toBeTruthy();
  });

  it('edge arrows still work when a press on them would start a drag', () => {
    const capture = vi.fn();
    HTMLElement.prototype.setPointerCapture = capture;
    const projects = Array.from({ length: 40 }, (_, i) => p('p' + String(i).padStart(2, '0')));
    const waiting = Array.from({ length: 7 }, (_, i) => agent('w' + i, { project: 'p' + String(i + 10), state: 'waiting' }));
    setup(projects, [...waiting, agent('x', { project: 'p39', state: 'working' })]);
    const arrow = screen.getByRole('button', { name: '1 working →' });
    fireEvent.pointerDown(arrow, { button: 0, pointerId: 1 });
    expect(capture).not.toHaveBeenCalled();
    fireEvent.pointerDown(document.querySelector('.town-stage')!, { button: 0, pointerId: 1 });
    expect(capture).toHaveBeenCalled();
  });

  it('keeps holding the order while the pointer is on the minimap', () => {
    setup();
    fireEvent.pointerEnter(document.querySelector('.town-minimap')!);
    act(() => dispatch({ type: 'agent', agent: agent('1', { project: 'gamma', state: 'waiting' }) }));
    expect(order()).toEqual(['alpha', 'beta', 'gamma']);
  });

  it('has one minimap dot per lot; clicking one jumps there', () => {
    const projects = Array.from({ length: 40 }, (_, i) => p('p' + String(i).padStart(2, '0')));
    setup(projects);
    expect(order()).toHaveLength(40);
    expect(labels()).not.toContain('p30');
    fireEvent.click(screen.getByRole('button', { name: 'Go to p30' }));
    expect(labels()).toContain('p30');
  });

  it('holds the order while hovered and re-sorts 3 s after leaving', () => {
    vi.useFakeTimers();
    const { stage } = setup();
    expect(order()).toEqual(['alpha', 'beta', 'gamma']);
    fireEvent.pointerEnter(stage);
    act(() => dispatch({ type: 'agent', agent: agent('1', { project: 'gamma', state: 'waiting' }) }));
    expect(order()).toEqual(['alpha', 'beta', 'gamma']);
    fireEvent.pointerLeave(stage);
    act(() => vi.advanceTimersByTime(2999));
    expect(order()).toEqual(['alpha', 'beta', 'gamma']);
    act(() => vi.advanceTimersByTime(1));
    expect(order()).toEqual(['gamma', 'alpha', 'beta']);
  });

  it('re-sorts at once on Home and returns to the left end', () => {
    const projects = Array.from({ length: 40 }, (_, i) => p('p' + String(i).padStart(2, '0')));
    const { stage } = setup(projects);
    fireEvent.pointerEnter(stage);
    act(() => dispatch({ type: 'agent', agent: agent('1', { project: 'p39', state: 'waiting' }) }));
    expect(order()[0]).toBe('p00');
    fireEvent.click(screen.getByRole('button', { name: 'Go to p20' }));
    expect(labels()).not.toContain('p00');
    fireEvent.keyDown(window, { key: 'Home' });
    expect(order()[0]).toBe('p39');
    expect(labels()[0]).toBe('p39');
  });

  it('pans with the arrow keys', () => {
    const projects = Array.from({ length: 40 }, (_, i) => p('p' + String(i).padStart(2, '0')));
    setup(projects);
    expect(labels()).toContain('p00');
    for (let i = 0; i < 4; i++) fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(labels()).not.toContain('p00');
  });

  it('shows an empty state with no projects and no sessions', () => {
    setup([], []);
    expect(screen.getByText(/No projects yet/)).toBeTruthy();
  });

  it('Leave town calls onLeave', () => {
    const { onLeave } = setup();
    fireEvent.click(screen.getByRole('button', { name: 'Leave town' }));
    expect(onLeave).toHaveBeenCalled();
  });

  describe('talking to workers', () => {
    /** Presses and releases on the screen point of lot `i`'s worker `k` (zoom 3, camera 0). */
    const clickWorker = (i: number, k: number, state: Parameters<typeof workerSpot>[2], move = 0) => {
      const [x, y] = workerSpot(i, k, state);
      const stage = document.querySelector('.town-stage')!;
      fireEvent.pointerDown(stage, { button: 0, pointerId: 1, clientX: x * 3, clientY: (y - 6) * 3 });
      if (move) fireEvent.pointerMove(stage, { pointerId: 1, clientX: x * 3 + move, clientY: (y - 6) * 3 });
      fireEvent.pointerUp(stage, { button: 0, pointerId: 1, clientX: x * 3 + move, clientY: (y - 6) * 3 });
    };
    const dialog = () => screen.queryByRole('dialog');

    it('clicking a worker opens its dialogue', () => {
      setup(undefined, [agent('s1', { project: 'alpha', state: 'idle' })]);
      clickWorker(0, 0, 'idle');
      expect(dialog()?.getAttribute('aria-label')).toBe('ALPHA · Session A');
      expect(document.querySelector('.town-stage')!.classList.contains('town-dim')).toBe(true);
    });

    it('a drag does not open a dialogue', () => {
      setup(undefined, [agent('s1', { project: 'alpha', state: 'idle' })]);
      clickWorker(0, 0, 'idle', 20);
      expect(dialog()).toBeNull();
    });

    it('Tab cycles workers that need you', () => {
      setup(undefined, [
        agent('w1', { project: 'alpha', state: 'waiting', since: '2026-10-28T10:00:00Z' }),
        agent('x', { project: 'beta', state: 'working' }),
        agent('w2', { project: 'gamma', state: 'waiting', since: '2026-10-28T10:05:00Z' }),
      ]);
      fireEvent.keyDown(window, { key: 'Tab' });
      expect(dialog()?.getAttribute('aria-label')).toBe('ALPHA · Session A');
      fireEvent.keyDown(window, { key: 'Tab' });
      expect(dialog()?.getAttribute('aria-label')).toBe('GAMMA · Session A');
      fireEvent.keyDown(window, { key: 'Tab' });
      expect(dialog()?.getAttribute('aria-label')).toBe('ALPHA · Session A');
    });

    it('Q shows the quest log; a quest opens; a seen done session leaves the log', () => {
      setup(undefined, [agent('w1', { project: 'alpha', state: 'waiting', message: 'Which db?' }), agent('d1', { project: 'beta', state: 'done' })]);
      fireEvent.keyDown(window, { key: 'q' });
      const quests = () => [...document.querySelectorAll('.quest-title')].map((e) => e.textContent);
      expect(quests()).toEqual(['alpha: Which db?', "Review beta's result"]);
      fireEvent.click(screen.getByRole('button', { name: /Review beta's result/ }));
      expect(dialog()?.getAttribute('aria-label')).toBe('BETA · Session A');
      fireEvent.keyDown(window, { key: 'Escape' });
      fireEvent.keyDown(window, { key: 'q' });
      expect(quests()).toEqual(['alpha: Which db?']);
    });

    it('keys typed in the message box do not move the town', () => {
      const projects = Array.from({ length: 40 }, (_, i) => p('p' + String(i).padStart(2, '0')));
      setup(projects, [agent('s1', { project: 'p00', state: 'done', canSend: true })]);
      clickWorker(0, 0, 'done');
      const box = screen.getByRole('textbox', { name: 'Message' });
      for (const key of ['ArrowRight', 'ArrowRight', 'ArrowRight', 'ArrowRight', 'q', 'Tab', 'Home']) fireEvent.keyDown(box, { key });
      expect(labels()).toContain('p00');
      expect(document.querySelector('.quest-log')).toBeNull();
      expect(dialog()).toBeTruthy();
    });

    it('the box closes when its session ends', () => {
      setup(undefined, [agent('s1', { project: 'alpha', state: 'idle' })]);
      clickWorker(0, 0, 'idle');
      act(() => dispatch({ type: 'agentEnd', id: 's1' }));
      expect(dialog()).toBeNull();
    });

    it('the box switches to "Answer it there" when the prompt is released', () => {
      const prompt = { requestId: 'r1', toolName: 'Bash', summary: 'npm i', answerable: true };
      setup(undefined, [agent('s1', { project: 'alpha', state: 'waiting', prompts: [prompt] })]);
      fireEvent.keyDown(window, { key: 'Tab' });
      expect(screen.getByRole('menu')).toBeTruthy();
      act(() => dispatch({ type: 'agent', agent: agent('s1', { project: 'alpha', state: 'waiting', prompts: [{ ...prompt, answerable: false }] }) }));
      expect(screen.queryByRole('menu')).toBeNull();
      fireEvent.keyDown(window, { key: ' ' });
      expect(document.querySelector('.dlg-text')!.textContent).toBe('I asked in my window: npm i. Answer it there.');
    });

    it('Tab is left to the page when nobody needs you', () => {
      setup(undefined, [agent('s1', { project: 'alpha', state: 'working' })]);
      expect(fireEvent.keyDown(window, { key: 'Tab' })).toBe(true);
      expect(dialog()).toBeNull();
    });

    it('the quest log does not lose a half-typed message', () => {
      setup(undefined, [agent('s1', { project: 'alpha', state: 'done', canSend: true })]);
      clickWorker(0, 0, 'done');
      fireEvent.change(screen.getByRole('textbox', { name: 'Message' }), { target: { value: 'half typed' } });
      fireEvent.keyDown(window, { key: 'q' });
      fireEvent.keyDown(window, { key: 'Escape' });
      expect((screen.getByRole('textbox', { name: 'Message' }) as HTMLTextAreaElement).value).toBe('half typed');
    });

    it('a link to another session opens its box, or the session in Claude when it is not in town', () => {
      setup(undefined, [
        agent('s1', { project: 'alpha', state: 'done', lastTurn: 'Told [Chat work](#local_bbb) and [Gone](#local_zzz).' }),
        agent('s2', { project: 'beta', state: 'working', hostSessionId: 'local_bbb', title: 'Chat work' }),
      ]);
      // beta (working) sorts before alpha (done): alpha is lot 1.
      clickWorker(1, 0, 'done');
      fireEvent.keyDown(window, { key: ' ' });
      fireEvent.click(screen.getByRole('button', { name: 'Chat work' }));
      expect(dialog()?.getAttribute('aria-label')).toBe('BETA · Chat work');
      fireEvent.keyDown(window, { key: 'Escape' });
      clickWorker(1, 0, 'done');
      fireEvent.keyDown(window, { key: ' ' });
      fireEvent.click(screen.getByRole('button', { name: 'Gone' }));
      expect(api.townOpenHost).toHaveBeenCalledWith('local_zzz');
    });

    it('reports focus while mounted', async () => {
      vi.spyOn(document, 'hasFocus').mockReturnValue(true);
      api.townFocus.mockClear();
      setup();
      expect(api.townFocus).toHaveBeenLastCalledWith(true);
      cleanup();
      await act(async () => void (await new Promise((r) => setTimeout(r, 0))));
      expect(api.townFocus).toHaveBeenLastCalledWith(false);
    });
  });
});
