import type { Lot, LotGroup } from './street';

// Scene geometry in logical pixels (the prototype's 320 x 180 pixel-art scale), drawn at an integer zoom.
export const SCENE_H = 180;
export const GROUND = 140;
export const SLOT0 = 12;
export const SLOT_W = 51;
export const BW = 40;
export const SMALL_BW = 28;
/** Lot labels sit on the grass below the road, clear of the workers. */
export const LABEL_Y = 171;

/** Logical pixels per wheel delta unit. */
export const WHEEL_FACTOR = 0.5;

/** Left edge of lot `index`'s building. */
export const lotX = (index: number): number => SLOT0 + index * SLOT_W;

export const worldWidth = (lotCount: number): number => SLOT0 * 2 + lotCount * SLOT_W;

/** Largest integer zoom at which the whole scene height fits. */
export const scaleFor = (viewHeightPx: number): number => Math.max(1, Math.floor(viewHeightPx / SCENE_H));

/** Camera x kept inside the street; 0 when the street is narrower than the view. */
export function clampCamera(x: number, world: number, view: number): number {
  if (world <= view) return 0;
  return Math.min(Math.max(0, x), world - view);
}

/** Camera x that centres lot `index`. */
export function cameraToShow(index: number, world: number, view: number): number {
  return clampCamera(lotX(index) + BW / 2 - view / 2, world, view);
}

/** First and last lot at least partly on screen (inclusive); [0, -1] for none. */
export function visibleRange(camX: number, view: number, lotCount: number): [first: number, last: number] {
  if (lotCount <= 0) return [0, -1];
  const first = Math.max(0, Math.floor((camX - BW - SLOT0) / SLOT_W) + 1);
  const last = Math.min(lotCount - 1, Math.ceil((camX + view - SLOT0) / SLOT_W) - 1);
  return [first, last];
}

export interface EdgeHint {
  side: 'left' | 'right';
  group: LotGroup;
  count: number;
  /** Nearest off-screen lot of that group. */
  target: number;
}

const HINT_GROUPS: LotGroup[] = ['waiting', 'failed', 'working', 'done'];

/** Per side, the most urgent group of sessions wholly off screen there, with how many sessions are in it. */
export function edgeHints(lots: Lot[], camX: number, view: number): EdgeHint[] {
  const left = lots.map((_, i) => i).filter((i) => lotX(i) + BW <= camX).reverse();
  const right = lots.map((_, i) => i).filter((i) => lotX(i) >= camX + view);
  const hints: EdgeHint[] = [];
  for (const [side, indexes] of [['left', left], ['right', right]] as const) {
    for (const group of HINT_GROUPS) {
      const count = indexes.reduce((n, i) => n + lots[i].workers.filter((w) => w.state === group).length, 0);
      if (!count) continue;
      const target = indexes.find((i) => lots[i].group === group) ?? indexes.find((i) => lots[i].workers.some((w) => w.state === group))!;
      hints.push({ side, group, count, target });
      break;
    }
  }
  return hints;
}

export function edgeText(h: EdgeHint): string {
  const what =
    h.group === 'waiting' ? (h.count === 1 ? '1 needs you' : `${h.count} need you`) : `${h.count} ${h.group}`;
  return h.side === 'right' ? `${what} →` : `← ${what}`;
}
