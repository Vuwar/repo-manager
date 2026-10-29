import { describe, expect, it } from 'vitest';
import { agent } from '../../test/fixtures';
import { lotX, SLOT_W } from './camera';
import { drawPortrait, FLAG_COLOR, FLOWER_COLOR, paletteFor, TownScene, workerAt, workerColors, workerSpot, type Draw2D, type TownFrame } from './render';
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

describe('workerAt', () => {
  const lots = [
    lot(0, { group: 'working', workers: [agent('a', { state: 'working' }), agent('b', { state: 'working' })] }),
    lot(1, { group: 'waiting', workers: [agent('c', { state: 'waiting' })] }),
  ];

  it('finds a worker at its spot and at its bubble', () => {
    const [x, y] = workerSpot(1, 0, 'waiting');
    expect(workerAt(lots, x, y)?.session.id).toBe('c');
    expect(workerAt(lots, x + 2, y - 24)?.session.id).toBe('c');
    expect(workerAt(lots, x, y)).toMatchObject({ lot: 1, index: 0 });
  });

  it('finds a finished worker in the middle of its jump', () => {
    const [x, y] = workerSpot(1, 0, 'waiting');
    expect(workerAt(lots, x, y - 30)?.session.id).toBe('c');
  });

  it('uses where a worker is drawn when the scene knows it (walking in)', () => {
    const [x, y] = workerSpot(1, 0, 'waiting');
    const drawnAt = (id: string): [number, number] | null => (id === 'c' ? [x - 30, y] : null);
    expect(workerAt(lots, x - 30, y, drawnAt)?.session.id).toBe('c');
    expect(workerAt(lots, x, y, drawnAt)).toBeNull();
  });

  it('finds nothing between workers', () => {
    expect(workerAt(lots, 2, 10)).toBeNull();
    expect(workerAt(lots, 48, 165)).toBeNull();
  });

  it('prefers the front-most worker where they overlap', () => {
    // lot 0 working lanes: a at (12,158), b at (30,158); add a third at (21,164) overlapping both
    const three = [lot(0, { group: 'working', workers: ['a', 'b', 'z'].map((id) => agent(id, { state: 'working' })) })];
    expect(workerAt(three, lotX(0) + 16, 158)?.session.id).toBe('z');
  });
});

describe('drawPortrait', () => {
  it("uses the worker's own hat and shirt colours", () => {
    const seen: string[] = [];
    const { ctx } = fakeCtx();
    const rec = new Proxy(ctx, { set: (t, k, v) => ((k === 'fillStyle' && seen.push(String(v)), ((t as Record<string, unknown>)[k as string] = v)), true) });
    drawPortrait(rec, agent('portrait-1', { state: 'idle' }));
    const { hat, shirt } = workerColors('portrait-1');
    expect(seen).toContain(hat);
    expect(seen).toContain(shirt);
  });
});

describe('buildings by level', () => {
  /** Lowest y the building's wall reaches (its top), from fillRects inside lot 0's x range. */
  const top = (levels: Record<string, number>, group: Lot['group'] = 'working') => {
    const { ctx, calls } = fakeCtx();
    const l = lot(0, { group, workers: group === 'none' ? [] : [agent('w', { state: 'working' })] });
    new TownScene(true).draw(ctx, frame([l], 0, { levels }));
    return Math.min(...calls.fillRect.filter(([x, y]) => x >= 12 && x <= 52 && y > 60 && y < 140).map(([, y]) => y));
  };
  const colours = (levels: Record<string, number>) => {
    const seen: string[] = [];
    const { ctx } = fakeCtx();
    const rec = new Proxy(ctx, { set: (t, k, v) => ((k === 'fillStyle' && seen.push(String(v)), ((t as Record<string, unknown>)[k as string] = v)), true) });
    const l = lot(0, { group: 'working', workers: [agent('w', { state: 'working' })] });
    new TownScene(true).draw(rec, frame([l], 0, { levels }));
    return seen;
  };

  it('a shack is lower than a house, a tower higher', () => {
    const shack = top({ 'project:p0': 1 });
    const house = top({ 'project:p0': 2 });
    const tower = top({ 'project:p0': 4 });
    expect(shack).toBeGreaterThan(house);
    expect(tower).toBeLessThan(house);
  });

  it('flowers from level 3, a flag from level 5', () => {
    expect(colours({ 'project:p0': 2 })).not.toContain(FLOWER_COLOR);
    expect(colours({ 'project:p0': 3 })).toContain(FLOWER_COLOR);
    expect(colours({ 'project:p0': 4 })).not.toContain(FLAG_COLOR);
    expect(colours({ 'project:p0': 5 })).toContain(FLAG_COLOR);
  });
});

describe('walking workers', () => {
  const at = (scene: TownScene, id: string) => scene.workerPosition(id);

  it('workers already there when the town opens stand at their spots', () => {
    const scene = new TownScene(false);
    const lots = [lot(0, { group: 'working', workers: [agent('a', { state: 'working' })] })];
    scene.update(lots, 0.016, 0);
    expect(at(scene, 'a')).toEqual(workerSpot(0, 0, 'working'));
  });

  it('a new worker walks in and reaches its spot', () => {
    const scene = new TownScene(false);
    scene.update([lot(0)], 0.016, 0);
    const lots = [lot(0, { group: 'working', workers: [agent('b', { state: 'working' })] })];
    scene.update(lots, 0.016, 16);
    expect(at(scene, 'b')).not.toEqual(workerSpot(0, 0, 'working'));
    for (let t = 0; t < 120; t++) scene.update(lots, 0.016, 32 + t * 16);
    expect(at(scene, 'b')).toEqual(workerSpot(0, 0, 'working'));
  });

  it('an ended worker walks away and is gone after 1.5 s', () => {
    const scene = new TownScene(false);
    const lots = [lot(0, { group: 'working', workers: [agent('c', { state: 'working' })] })];
    scene.update(lots, 0.016, 0);
    scene.update([lot(0)], 0.016, 16);
    for (let t = 0; t < 30; t++) scene.update([lot(0)], 0.016, 32 + t * 16);
    expect(at(scene, 'c')).not.toBeNull();
    for (let t = 0; t < 70; t++) scene.update([lot(0)], 0.016, 600 + t * 16);
    expect(at(scene, 'c')).toBeNull();
  });

  it('ten workers ending together with their lot do not break drawing', () => {
    const scene = new TownScene(false);
    const many = [lot(0, { group: 'working', workers: Array.from({ length: 10 }, (_, i) => agent('m' + i, { state: 'working' })) })];
    scene.update(many, 0.016, 0);
    const { ctx } = fakeCtx();
    scene.update([], 0.016, 16);
    expect(() => scene.draw(ctx, frame([], 0))).not.toThrow();
  });

  it('with reduced motion new workers appear at their spot and ended ones go at once', () => {
    const scene = new TownScene(true);
    scene.update([lot(0)], 0.016, 0);
    const lots = [lot(0, { group: 'working', workers: [agent('d', { state: 'working' })] })];
    scene.update(lots, 0.016, 16);
    expect(at(scene, 'd')).toEqual(workerSpot(0, 0, 'working'));
    scene.update([lot(0)], 0.016, 32);
    expect(at(scene, 'd')).toBeNull();
  });
});
