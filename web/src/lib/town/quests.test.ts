import { describe, expect, it } from 'vitest';
import type { AgentSessionDto } from '../../api/types';
import { agent } from '../../test/fixtures';
import { questsFor, waitText } from './quests';
import type { Lot } from './street';

const at = (min: number) => `2026-10-28T10:${String(min).padStart(2, '0')}:00Z`;
const lot = (name: string, workers: AgentSessionDto[]): Lot => ({
  key: 'project:' + name,
  name,
  temporary: false,
  group: workers[0]?.state ?? 'none',
  since: null,
  workers,
  serversUp: false,
});

describe('questsFor', () => {
  it('lists needs-you first, then unseen done sessions, oldest first', () => {
    const lots = [
      lot('kodex', [agent('w2', { state: 'waiting', since: at(5), message: 'Needs permission: npm i' })]),
      lot('pathly', [agent('w1', { state: 'waiting', since: at(1), message: 'Which db?' })]),
      lot('ai-shell', [agent('d1', { state: 'done', since: at(0) }), agent('x', { state: 'working' })]),
    ];
    const q = questsFor(lots, {});
    expect(q.map((x) => [x.sessionId, x.kind, x.title])).toEqual([
      ['w1', 'needsYou', 'pathly: Which db?'],
      ['w2', 'needsYou', 'kodex: Needs permission: npm i'],
      ['d1', 'review', "Review ai-shell's result"],
    ]);
    expect(q[0].since).toBe(Date.parse(at(1)));
  });

  it('drops a seen done session until it finishes again', () => {
    const done = agent('d1', { state: 'done', since: at(0) });
    expect(questsFor([lot('a', [done])], { d1: at(0) })).toEqual([]);
    expect(questsFor([lot('a', [{ ...done, since: at(9) }])], { d1: at(0) })).toHaveLength(1);
  });
});

describe('waitText', () => {
  it('rounds to minutes and hours', () => {
    expect(waitText(59_000)).toBe('just now');
    expect(waitText(60_000)).toBe('1 min');
    expect(waitText(125 * 60_000)).toBe('2 h 5 min');
    expect(waitText(120 * 60_000)).toBe('2 h');
  });
});
