import type { LogLineDto } from '../api/types';
import { MAX_LOG_LINES } from '../state/reducer';
import { parseTime } from './format';

/** Merges several per-source buffers by timestamp (stable within a source). */
export function mergeLogs(buffers: LogLineDto[][], max = MAX_LOG_LINES): LogLineDto[] {
  const nonEmpty = buffers.filter((b) => b.length);
  if (nonEmpty.length === 0) return [];
  if (nonEmpty.length === 1) return nonEmpty[0].length > max ? nonEmpty[0].slice(-max) : nonEmpty[0];
  const tagged: { t: number; i: number; l: LogLineDto }[] = [];
  let i = 0;
  for (const b of nonEmpty) for (const l of b) tagged.push({ t: parseTime(l.timestamp), i: i++, l });
  tagged.sort((a, b) => a.t - b.t || a.i - b.i);
  const out = tagged.map((x) => x.l);
  return out.length > max ? out.slice(-max) : out;
}
