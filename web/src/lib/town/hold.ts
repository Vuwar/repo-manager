/** How long after the pointer leaves the town the street re-sorts. */
export const RESORT_DELAY_MS = 3000;

const same = (a: string[], b: string[]) => a.length === b.length && a.every((k, i) => k === b[i]);

/**
 * Street order to show. Not holding: the wanted order. Holding (pointer over the town): lots keep their place,
 * new ones join at the right end, gone ones drop out. Returns `shown` itself when nothing changed.
 */
export function heldOrder(shown: string[], wanted: string[], hold: boolean): string[] {
  let next = wanted;
  if (hold) {
    const want = new Set(wanted);
    const kept = shown.filter((k) => want.has(k));
    const have = new Set(kept);
    next = [...kept, ...wanted.filter((k) => !have.has(k))];
  }
  return same(next, shown) ? shown : next;
}
