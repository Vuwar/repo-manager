import type { Lot } from './street';

/** Something waiting on the user. */
export interface Quest {
  sessionId: string;
  lotName: string;
  kind: 'needsYou' | 'review';
  title: string;
  /** When it started waiting (ms). */
  since: number;
}

/**
 * Needs-you sessions, then done sessions not yet seen (seen[id] holds the `since` the user looked at), each
 * oldest first.
 */
export function questsFor(lots: Lot[], seen: Record<string, string>): Quest[] {
  const needs: Quest[] = [];
  const review: Quest[] = [];
  for (const lot of lots)
    for (const w of lot.workers) {
      const since = Date.parse(w.since);
      if (w.state === 'waiting') needs.push({ sessionId: w.id, lotName: lot.name, kind: 'needsYou', title: `${lot.name}: ${w.message}`, since });
      else if (w.state === 'done' && seen[w.id] !== w.since)
        review.push({
          sessionId: w.id,
          lotName: lot.name,
          kind: 'review',
          title: w.wrapUp?.ask ? `${lot.name}: ${w.wrapUp.ask}` : `Review ${lot.name}'s result`,
          since,
        });
    }
  const bySince = (a: Quest, b: Quest) => a.since - b.since;
  return [...needs.sort(bySince), ...review.sort(bySince)];
}

/** "just now", "3 min", "2 h", "2 h 5 min". */
export function waitText(ms: number): string {
  const min = Math.floor(ms / 60_000);
  if (min < 1) return 'just now';
  if (min < 60) return `${min} min`;
  const h = Math.floor(min / 60), m = min % 60;
  return m ? `${h} h ${m} min` : `${h} h`;
}
