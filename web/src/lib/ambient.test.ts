import { describe, expect, it } from 'vitest';
import { accentVars, ambientStyle, isDarkColor, matchVars, nextAmbient } from './ambient';

describe('ambient', () => {
  it('cycles glass, match, mixed', () => {
    expect(nextAmbient('glass')).toBe('match');
    expect(nextAmbient('match')).toBe('mixed');
    expect(nextAmbient('mixed')).toBe('glass');
  });

  it('builds a dark palette with light text from a dark gray', () => {
    const v = matchVars([30, 30, 30]);
    expect(isDarkColor([30, 30, 30])).toBe(true);
    expect(v.colorScheme).toBe('dark');
    expect(v['--bg']).toBe('#1e1e1e');
    expect(v['--text']).toBe('#ededed');
    // Gray has no hue: the app's own accent stays.
    expect(v['--accent']).toBeUndefined();
  });

  it('builds a light palette with dark text from a light color', () => {
    const v = matchVars([235, 240, 245]);
    expect(v.colorScheme).toBe('light');
    expect(v['--text']).toBe('#131314');
    expect(v['--ok']).toBe('#1f9d3a');
  });

  it('borrows the hue of a colorful background for the accent', () => {
    const v = accentVars([20, 60, 160], true);
    expect(v['--accent']).toMatch(/^#/);
    const [r, , b] = [1, 3, 5].map((i) => parseInt(v['--accent'].slice(i, i + 2), 16));
    expect(b).toBeGreaterThan(r);
  });

  it('picks the look from the mode and what the shell applied', () => {
    const rgb: [number, number, number] = [40, 90, 60];
    expect(ambientStyle('glass', { glass: true, rgb: null }, true)).toEqual({ className: 'is-glass', style: {} });
    expect(ambientStyle('glass', { glass: false, rgb: null }, true)).toEqual({ className: '', style: {} });
    expect(ambientStyle('match', { glass: false, rgb }, true).className).toBe('is-matched');
    const mixed = ambientStyle('mixed', { glass: true, rgb }, true);
    expect(mixed.className).toBe('is-glass');
    expect(mixed.style['--ambient']).toBe('rgba(40, 90, 60, 0.34)');
    // No acrylic (older Windows): mixed falls back to match.
    expect(ambientStyle('mixed', { glass: false, rgb }, true).className).toBe('is-matched');
    // No color yet: normal look.
    expect(ambientStyle('match', { glass: false, rgb: null }, true).className).toBe('');
  });
});
