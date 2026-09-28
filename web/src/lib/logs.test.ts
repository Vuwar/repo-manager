import { describe, expect, it } from 'vitest';
import { line } from '../test/fixtures';
import { mergeLogs } from './logs';

describe('mergeLogs', () => {
  it('interleaves several sources by timestamp and caps the result', () => {
    const a = [line('p/api', 'a1', '2026-09-28T10:00:00.1000000+00:00'), line('p/api', 'a2', '2026-09-28T10:00:02Z')];
    const b = [line('p/web', 'b1', '2026-09-28T10:00:01Z'), line('p/web', 'b2', '2026-09-28T10:00:03Z')];
    expect(mergeLogs([a, b]).map((l) => l.text)).toEqual(['a1', 'b1', 'a2', 'b2']);
    expect(mergeLogs([a, b], 2).map((l) => l.text)).toEqual(['a2', 'b2']);
  });

  it('returns a single buffer untouched', () => {
    const a = [line('p/api', 'x', '2026-09-28T10:00:00Z')];
    expect(mergeLogs([a, []])).toBe(a);
  });
});
