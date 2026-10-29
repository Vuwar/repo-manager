import type { LevelDto } from '../../api/types';

/** "Lv 3 · 180 XP" for the town's top bar. */
export function levelText(l: LevelDto): string {
  return `Lv ${l.level} · ${l.xp} XP`;
}

export type BuildingTier = 'shack' | 'house' | 'tower';

/** Buildings grow with their repo: a shack at level 1, a house at 2 and 3, a tower from 4. */
export function buildingTier(level: number): BuildingTier {
  return level <= 1 ? 'shack' : level <= 3 ? 'house' : 'tower';
}

/** Flower boxes from level 3, a flag on the roof from level 5. */
export function decorations(level: number): { flowers: boolean; flag: boolean } {
  return { flowers: level >= 3, flag: level >= 5 };
}
