import type { AgentSessionDto } from '../../api/types';
import { clampCamera } from './camera';
import { MAX_WORKERS, workerSpot } from './render';
import type { Lot } from './street';

/** The boss walks (arrow keys held) at BOSS_SPEED and runs to a clicked worker at BOSS_RUN, in scene px per second. */
export const BOSS_SPEED = 90;
export const BOSS_RUN = 120;
/** The camera keeps the boss at least this far inside the view. */
export const BOSS_MARGIN = 40;
/** Enter talks to a worker within this distance of the boss. */
export const TALK_DISTANCE = 14;
/** The boss stands on the road, in front of the workers. */
export const BOSS_Y = 166;

export interface Boss {
  x: number;
  /** Where it runs to after a click; null when it stands or walks by key. */
  target: number | null;
}

/** One step: a held key walks (and drops any target), else the boss runs to its target. Stays on the street. */
export function stepBoss(b: Boss, dir: -1 | 0 | 1, dt: number, world: number): Boss {
  const clamp = (x: number) => Math.min(Math.max(0, x), world);
  if (dir !== 0) return { x: clamp(b.x + dir * BOSS_SPEED * dt), target: null };
  if (b.target === null) return b;
  const d = b.target - b.x, step = BOSS_RUN * dt;
  if (Math.abs(d) <= step) return { x: clamp(b.target), target: null };
  return { x: clamp(b.x + Math.sign(d) * step), target: b.target };
}

/** Camera x that keeps the boss BOSS_MARGIN inside the view, moving as little as possible. */
export function followCamera(camX: number, bossX: number, view: number, world: number): number {
  let x = camX;
  if (bossX < camX + BOSS_MARGIN) x = bossX - BOSS_MARGIN;
  else if (bossX > camX + view - BOSS_MARGIN) x = bossX + BOSS_MARGIN - view;
  return clampCamera(x, world, view);
}

/** The drawn worker closest to the boss along the street, within maxDist. */
export function nearestWorker(lots: Lot[], bossX: number, maxDist = TALK_DISTANCE): { session: AgentSessionDto; lot: number; index: number } | null {
  let best: { session: AgentSessionDto; lot: number; index: number; d: number } | null = null;
  lots.forEach((lot, i) =>
    lot.workers.slice(0, MAX_WORKERS).forEach((session, k) => {
      const d = Math.abs(workerSpot(i, k, session.state)[0] - bossX);
      if (d <= maxDist && (!best || d < best.d)) best = { session, lot: i, index: k, d };
    }),
  );
  if (!best) return null;
  const { session, lot, index } = best;
  return { session, lot, index };
}
