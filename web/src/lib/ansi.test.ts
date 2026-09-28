import { describe, expect, it } from 'vitest';
import { parseAnsi, segmentStyle, stripAnsi } from './ansi';

const E = '\x1b';

describe('parseAnsi', () => {
  it('returns plain text as one unstyled segment', () => {
    expect(parseAnsi('hello world')).toEqual([{ text: 'hello world' }]);
  });

  it('applies basic foreground colours and reset', () => {
    const segs = parseAnsi(`${E}[31mred${E}[0m plain ${E}[92mbright${E}[39m`);
    expect(segs).toEqual([{ text: 'red', fg: 'p1' }, { text: ' plain ' }, { text: 'bright', fg: 'p10' }]);
  });

  it('handles bold, background and combined params', () => {
    const segs = parseAnsi(`${E}[1;33;44mwarn${E}[22mnormal${E}[49m${E}[m!`);
    expect(segs[0]).toEqual({ text: 'warn', bold: true, fg: 'p3', bg: 'p4' });
    expect(segs[1]).toEqual({ text: 'normal', fg: 'p3', bg: 'p4' });
    expect(segs[2]).toEqual({ text: '!' });
  });

  it('supports 256-colour and truecolour', () => {
    const segs = parseAnsi(`${E}[38;5;196ma${E}[38;5;9mb${E}[38;2;10;20;30mc${E}[48;5;240md`);
    expect(segs[0].fg).toBe('rgb(255,0,0)');
    expect(segs[1].fg).toBe('p9');
    expect(segs[2].fg).toBe('rgb(10,20,30)');
    expect(segs[3].bg).toBe('rgb(88,88,88)');
  });

  it('strips non-SGR sequences (cursor, erase, OSC hyperlinks)', () => {
    const s = `${E}[2K${E}[1G${E}]8;;http://x${E}\link${E}]8;;${E}\ done${E}[?25h`;
    expect(parseAnsi(s)).toEqual([{ text: 'link done' }]);
    expect(stripAnsi(`${E}[32m  VITE${E}[39m ready`)).toBe('  VITE ready');
  });

  it('merges adjacent segments with the same style', () => {
    expect(parseAnsi(`${E}[31ma${E}[31mb`)).toEqual([{ text: 'ab', fg: 'p1' }]);
  });

  it('builds inline styles', () => {
    expect(segmentStyle({ fg: 'p2', bold: true })).toEqual({ color: 'var(--ansi-2)', fontWeight: '600' });
    expect(segmentStyle({})).toBeUndefined();
    expect(segmentStyle({ inverse: true, fg: 'p1' })).toEqual({ color: 'var(--log-bg)', backgroundColor: 'var(--ansi-1)' });
  });
});
