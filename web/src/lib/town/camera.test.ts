import { describe, expect, it } from 'vitest';
import { agent } from '../../test/fixtures';
import { BW, cameraToShow, clampCamera, edgeHints, edgeText, lotX, scaleFor, visibleRange, worldWidth } from './camera';
import type { Lot, LotGroup } from './street';

function lot(i: number, group: LotGroup = 'none', workers = 0): Lot {
  const state = group === 'none' ? 'idle' : group;
  return {
    key: 'project:p' + i,
    name: 'p' + i,
    temporary: false,
    group,
    since: null,
    workers: Array.from({ length: workers }, (_, k) => agent(`${i}-${k}`, { state })),
    serversUp: false,
  };
}

describe('camera', () => {
  it('stays at 0 when the street is narrower than the view', () => {
    expect(clampCamera(50, 200, 400)).toBe(0);
    expect(clampCamera(-5, 200, 400)).toBe(0);
  });

  it('clamps to both ends', () => {
    expect(clampCamera(-10, 1000, 300)).toBe(0);
    expect(clampCamera(900, 1000, 300)).toBe(700);
    expect(clampCamera(123, 1000, 300)).toBe(123);
  });

  it('centres a lot', () => {
    const world = worldWidth(40);
    expect(cameraToShow(10, world, 320)).toBe(lotX(10) + BW / 2 - 160);
    expect(cameraToShow(0, world, 320)).toBe(0);
    expect(cameraToShow(39, world, 320)).toBe(world - 320);
  });

  it('lists lots at least partly on screen', () => {
    expect(visibleRange(0, 320, 40)).toEqual([0, 6]);
    // lot 1 spans [63, 103]; a camera at 100 still shows its last 3 px
    expect(visibleRange(100, 320, 40)[0]).toBe(1);
    expect(visibleRange(104, 320, 40)[0]).toBe(2);
    expect(visibleRange(0, 320, 3)).toEqual([0, 2]);
    expect(visibleRange(0, 320, 0)).toEqual([0, -1]);
  });

  it('picks the integer pixel scale that fits the scene height', () => {
    expect(scaleFor(1080)).toBe(6);
    expect(scaleFor(100)).toBe(1);
  });
});

describe('edge hints', () => {
  it('counts off-screen workers of the most urgent group and targets the nearest lot', () => {
    const lots = Array.from({ length: 30 }, (_, i) => lot(i));
    lots[20] = lot(20, 'waiting', 2);
    lots[25] = lot(25, 'waiting', 1);
    lots[22] = lot(22, 'working', 4);
    const [hint] = edgeHints(lots, 0, 320);
    expect(hint).toEqual({ side: 'right', group: 'waiting', count: 3, target: 20 });
    expect(edgeText(hint)).toBe('3 need you →');
  });

  it('names one waiting worker on the left in the singular', () => {
    const lots = Array.from({ length: 30 }, (_, i) => lot(i));
    lots[1] = lot(1, 'waiting', 1);
    const hints = edgeHints(lots, lotX(20), 320);
    expect(hints).toHaveLength(1);
    expect(edgeText(hints[0])).toBe('← 1 needs you');
  });

  it('gives no hint for idle sessions or empty lots', () => {
    const lots = Array.from({ length: 30 }, (_, i) => lot(i));
    lots[29] = lot(29, 'idle', 2);
    expect(edgeHints(lots, 0, 320)).toEqual([]);
  });

  it('labels failed, working and done', () => {
    expect(edgeText({ side: 'right', group: 'failed', count: 2, target: 0 })).toBe('2 failed →');
    expect(edgeText({ side: 'left', group: 'working', count: 1, target: 0 })).toBe('← 1 working');
    expect(edgeText({ side: 'right', group: 'done', count: 4, target: 0 })).toBe('4 done →');
  });
});
