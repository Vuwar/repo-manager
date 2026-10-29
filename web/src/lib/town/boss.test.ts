import { describe, expect, it } from 'vitest';
import { agent } from '../../test/fixtures';
import { BOSS_RUN, BOSS_SPEED, followCamera, nearestWorker, stepBoss } from './boss';
import { workerSpot } from './render';
import type { Lot } from './street';

const lot = (i: number, workers = 0): Lot => ({
  key: 'project:p' + i,
  name: 'p' + i,
  temporary: false,
  group: workers ? 'working' : 'none',
  since: null,
  workers: Array.from({ length: workers }, (_, k) => agent(`${i}-${k}`, { state: 'working' })),
  serversUp: false,
});

describe('stepBoss', () => {
  it('walks at its speed and stays on the street', () => {
    expect(stepBoss({ x: 100, target: null }, 1, 0.5, 1000).x).toBe(100 + BOSS_SPEED * 0.5);
    expect(stepBoss({ x: 5, target: null }, -1, 1, 1000).x).toBe(0);
    expect(stepBoss({ x: 995, target: null }, 1, 1, 1000).x).toBe(1000);
  });

  it('runs to a target when no key is held, then stops there', () => {
    let b = { x: 0, target: 60 as number | null };
    b = stepBoss(b, 0, 0.25, 1000);
    expect(b.x).toBe(BOSS_RUN * 0.25);
    b = stepBoss(b, 0, 1, 1000);
    expect(b).toEqual({ x: 60, target: null });
  });

  it('a held key wins over a target and clears it', () => {
    expect(stepBoss({ x: 100, target: 500 }, -1, 0.1, 1000)).toEqual({ x: 100 - BOSS_SPEED * 0.1, target: null });
  });
});

describe('followCamera', () => {
  it('keeps the boss 40 px inside the view', () => {
    expect(followCamera(0, 100, 320, 2000)).toBe(0);
    expect(followCamera(0, 300, 320, 2000)).toBe(300 + 40 - 320);
    expect(followCamera(500, 520, 320, 2000)).toBe(480);
    expect(followCamera(0, 1990, 320, 2000)).toBe(2000 - 320);
  });
});

describe('nearestWorker', () => {
  const lots = [lot(0, 2), lot(1, 1)];
  it('finds the closest worker within 14 px', () => {
    const [x] = workerSpot(1, 0, 'working');
    expect(nearestWorker(lots, x + 10)?.session.id).toBe('1-0');
  });
  it('finds nobody further away', () => {
    const [x] = workerSpot(1, 0, 'working');
    expect(nearestWorker(lots, x + 30)).toBeNull();
  });
});
