import { describe, expect, it } from 'vitest';
import { initialState, reducer } from '../../state/reducer';
import { buildingTier, decorations, levelText } from './progress';

const level = (xp: number, lv: number) => ({ xp, level: lv, levelStart: 0, nextLevel: 50 });
const progress = { player: level(60, 2), repos: { kodex: level(10, 1) } };

describe('progress helpers', () => {
  it('writes the level for the top bar', () => {
    expect(levelText(level(60, 2))).toBe('Lv 2 · 60 XP');
  });

  it('picks a building by repo level', () => {
    expect([1, 2, 3, 4, 9].map(buildingTier)).toEqual(['shack', 'house', 'house', 'tower', 'tower']);
  });

  it('adds flowers from level 3 and a flag from level 5', () => {
    expect(decorations(2)).toEqual({ flowers: false, flag: false });
    expect(decorations(3)).toEqual({ flowers: true, flag: false });
    expect(decorations(5)).toEqual({ flowers: true, flag: true });
  });
});

describe('progress in the store', () => {
  it('loads and replaces progress', () => {
    const s = reducer(initialState(), { type: 'progressLoaded', progress });
    expect(s.progress).toEqual(progress);
  });

  it('keeps the last ten awards, newest last, stamped with when they arrived', () => {
    let s = initialState();
    for (let i = 0; i < 12; i++)
      s = reducer(s, { type: 'progress', progress, award: { kind: 'turn', amount: i, sessionId: 's', repo: 'kodex' }, at: 1000 + i });
    expect(s.awards).toHaveLength(10);
    expect(s.awards[0].amount).toBe(2);
    expect(s.awards[9]).toMatchObject({ amount: 11, at: 1011 });
    expect(s.progress).toEqual(progress);
  });
});

describe('progress revisions', () => {
  it('an older snapshot arriving late does not step the totals back, but its award still counts', () => {
    const newer = { revision: 5, player: level(40, 1), repos: {} };
    const older = { revision: 4, player: level(20, 1), repos: {} };
    let s = reducer(initialState(), { type: 'progress', progress: newer, award: { kind: 'answer', amount: 20 }, at: 1 });
    s = reducer(s, { type: 'progress', progress: older, award: { kind: 'answer', amount: 20 }, at: 2 });
    expect(s.progress?.player.xp).toBe(40);
    expect(s.awards).toHaveLength(2);
    // A fresh load from the daemon is the truth (its file may have been reset).
    s = reducer(s, { type: 'progressLoaded', progress: older });
    expect(s.progress?.player.xp).toBe(20);
  });
});
