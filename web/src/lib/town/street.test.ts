import { describe, expect, it } from 'vitest';
import type { AgentSessionDto } from '../../api/types';
import { agent, instance, project, svc } from '../../test/fixtures';
import { buildStreet, GROUP_ORDER, lotKeyFor, needsYouCount, windowTitle } from './street';

describe('needs-you count and title', () => {
  it('counts waiting sessions only', () => {
    const list = [
      agent('a', { state: 'waiting' }),
      agent('b', { state: 'working' }),
      agent('c', { state: 'done' }),
      agent('d', { state: 'failed' }),
      agent('e', { state: 'waiting' }),
    ];
    expect(needsYouCount(Object.fromEntries(list.map((a) => [a.id, a])))).toBe(2);
  });

  it('puts the count in front of the app name', () => {
    expect(windowTitle(0)).toBe('RepoManager');
    expect(windowTitle(3)).toBe('(3) RepoManager');
  });
});

const p = (name: string, running = false) => project(name, [instance(name, [svc(name, 'web', { state: running ? 'running' : 'stopped' })])]);
const map = (...list: AgentSessionDto[]) => Object.fromEntries(list.map((a) => [a.id, a]));
const at = (min: number) => `2026-10-28T10:${String(min).padStart(2, '0')}:00Z`;

describe('buildStreet', () => {
  it('orders groups waiting, failed, working, done, idle, then no session', () => {
    const projects = ['a', 'b', 'c', 'd', 'e', 'f'].map((n) => p(n));
    const lots = buildStreet(
      projects,
      map(
        agent('1', { project: 'a', state: 'idle' }),
        agent('2', { project: 'b', state: 'done' }),
        agent('3', { project: 'c', state: 'working' }),
        agent('4', { project: 'd', state: 'failed' }),
        agent('5', { project: 'e', state: 'waiting' }),
      ),
    );
    expect(lots.map((l) => l.name)).toEqual(['e', 'd', 'c', 'b', 'a', 'f']);
    expect(lots.map((l) => l.group)).toEqual(GROUP_ORDER);
  });

  it('within a group, longest waiting first', () => {
    const lots = buildStreet(
      [p('x'), p('y')],
      map(agent('1', { project: 'x', state: 'waiting', since: at(5) }), agent('2', { project: 'y', state: 'waiting', since: at(1) })),
    );
    expect(lots.map((l) => l.name)).toEqual(['y', 'x']);
    expect(lots[0].since).toBe(Date.parse(at(1)));
  });

  it('includes projects without sessions, sorted by name, lit while a server runs', () => {
    const lots = buildStreet([p('zeta', true), p('Alpha')], {});
    expect(lots.map((l) => [l.name, l.group, l.serversUp, l.since])).toEqual([
      ['Alpha', 'none', false, null],
      ['zeta', 'none', true, null],
    ]);
  });

  it('takes the group of its most urgent worker', () => {
    const [lot] = buildStreet(
      [p('a')],
      map(agent('1', { project: 'a', state: 'working', since: at(0) }), agent('2', { project: 'a', state: 'waiting', since: at(9) })),
    );
    expect(lot.group).toBe('waiting');
    expect(lot.workers.map((w) => w.id)).toEqual(['2', '1']);
    expect(lot.since).toBe(Date.parse(at(9)));
  });

  it('puts a worktree session at the main project lot', () => {
    const lots = buildStreet([p('a')], map(agent('1', { project: 'a', worktree: 'feat-x', state: 'working' })));
    expect(lots).toHaveLength(1);
    expect(lots[0].workers[0].worktree).toBe('feat-x');
    expect(lots[0].temporary).toBe(false);
  });

  it('gives a session for an unknown project a temporary lot named after the project', () => {
    for (const projects of [[p('a')], null]) {
      const lots = buildStreet(projects, map(agent('1', { project: 'gone', folder: 'gone-dir', state: 'working' })));
      const lot = lots.find((l) => l.temporary)!;
      expect(lot.name).toBe('gone');
      expect(lot.key).toBe('project:gone');
    }
  });

  it('keeps unregistered folders with the same name apart', () => {
    const lots = buildStreet(
      [],
      map(agent('1', { folder: 'tool', cwd: 'C:\\a\\tool' }), agent('2', { folder: 'tool', cwd: 'D:\\b\\tool\\' })),
    );
    expect(lots).toHaveLength(2);
    expect(lots.every((l) => l.name === 'tool' && l.temporary)).toBe(true);
    expect(new Set(lots.map((l) => l.key))).toEqual(new Set(['folder:c:\\a\\tool', 'folder:d:\\b\\tool']));
  });

  it('drops a temporary lot with its last session', () => {
    expect(buildStreet([], map(agent('1', { folder: 'tool', cwd: 'C:\\a\\tool' })))).toHaveLength(1);
    expect(buildStreet([], {})).toHaveLength(0);
  });

  it('matches project names case-insensitively', () => {
    const lots = buildStreet([p('Demo')], map(agent('1', { project: 'demo', state: 'working' })));
    expect(lots).toHaveLength(1);
    expect(lots[0].temporary).toBe(false);
    expect(lotKeyFor(agent('1', { project: 'DEMO' }))).toBe('project:demo');
  });
});
