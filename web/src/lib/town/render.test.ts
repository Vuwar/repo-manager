import { describe, expect, it } from 'vitest';
import { agent } from '../../test/fixtures';
import { SLOT_W } from './camera';
import { paletteFor, TownScene, type Draw2D, type TownFrame } from './render';
import type { Lot } from './street';

/** Records draw calls; fillRect x is recorded in screen space (after the current translate). */
function fakeCtx() {
  const calls = { fillRect: [] as [number, number, number, number][], translate: [] as [number, number][] };
  let tx = 0;
  const ctx: Draw2D = {
    fillStyle: '',
    globalAlpha: 1,
    fillRect: (x: number, y: number, w: number, h: number) => void calls.fillRect.push([x + tx, y, w, h]),
    clearRect: () => {},
    save: () => {},
    restore: () => {},
    beginPath: () => {},
    rect: () => {},
    clip: () => {},
    translate: (x: number, y: number) => {
      calls.translate.push([x, y]);
      tx += x;
    },
  } as unknown as Draw2D;
  return { ctx, calls };
}

const lot = (i: number, over: Partial<Lot> = {}): Lot => ({
  key: 'project:p' + i,
  name: 'p' + i,
  temporary: false,
  group: 'none',
  since: null,
  workers: [],
  serversUp: false,
  ...over,
});

const frame = (lots: Lot[], camX: number, over: Partial<TownFrame> = {}): TownFrame => ({
  lots,
  camX,
  viewW: 320,
  now: 1000,
  phase: 0.42,
  reducedMotion: false,
  ...over,
});

describe('TownScene', () => {
  it('draws only lots on screen', () => {
    const lots = Array.from({ length: 40 }, (_, i) => lot(i));
    const { ctx, calls } = fakeCtx();
    new TownScene(false).draw(ctx, frame(lots, 0));
    const maxX = Math.max(...calls.fillRect.map(([x]) => x));
    // fillRect x is screen space here; nothing far past the right edge of the view
    expect(maxX).toBeLessThan(320 + SLOT_W);
  });

  it('fills grass below the road far past the scene, so a tall window has no empty band', () => {
    const { ctx, calls } = fakeCtx();
    new TownScene(false).draw(ctx, frame([lot(0)], 0));
    expect(calls.fillRect.some(([, y, , h]) => y >= 168 && y + h >= 600)).toBe(true);
  });

  it('translates by the camera', () => {
    const lots = Array.from({ length: 40 }, (_, i) => lot(i));
    const { ctx, calls } = fakeCtx();
    new TownScene(false).draw(ctx, frame(lots, 200));
    expect(calls.translate).toContainEqual([-200, 0]);
  });

  it('throws confetti when a worker finishes, but not with reduced motion', () => {
    const working = [lot(0, { group: 'working', workers: [agent('s1', { state: 'working' })] })];
    const done = [lot(0, { group: 'done', workers: [agent('s1', { state: 'done' })] })];
    const lively = new TownScene(false);
    lively.update(working, 0.016, 0);
    lively.update(done, 0.016, 16);
    expect(lively.particleCount()).toBeGreaterThan(0);

    const calm = new TownScene(true);
    calm.update(working, 0.016, 0);
    calm.update(done, 0.016, 16);
    expect(calm.particleCount()).toBe(0);
  });

  it('does not blink with reduced motion', () => {
    const lots = [lot(0, { group: 'failed', workers: [agent('s1', { state: 'failed' })] }), lot(1, { group: 'waiting', workers: [agent('s2', { state: 'waiting' })] })];
    const colours = (now: number) => {
      const scene = new TownScene(true);
      scene.update(lots, 0.016, 0);
      const seen: string[] = [];
      const { ctx } = fakeCtx();
      const rec = new Proxy(ctx, { set: (t, k, v) => ((k === 'fillStyle' && seen.push(String(v)), ((t as Record<string, unknown>)[k as string] = v)), true) });
      scene.draw(rec, frame(lots, 0, { now, reducedMotion: true }));
      return seen.join(',');
    };
    expect(colours(1000)).toBe(colours(1137));
  });

  it('keeps a building palette by lot key', () => {
    expect(paletteFor('project:kodex')).toBe(paletteFor('project:kodex'));
    for (const k of ['a', 'b', 'project:x', 'folder:c:\\t']) {
      expect(paletteFor(k)).toBeGreaterThanOrEqual(0);
      expect(paletteFor(k)).toBeLessThan(6);
    }
  });
});
