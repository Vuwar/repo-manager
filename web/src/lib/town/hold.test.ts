import { describe, expect, it } from 'vitest';
import { heldOrder } from './hold';

describe('heldOrder', () => {
  it('takes the wanted order when not holding', () => {
    expect(heldOrder(['a', 'b'], ['b', 'a'], false)).toEqual(['b', 'a']);
  });

  it('keeps the shown order while holding', () => {
    expect(heldOrder(['a', 'b', 'c'], ['c', 'a', 'b'], true)).toEqual(['a', 'b', 'c']);
  });

  it('adds new keys at the right end and drops removed ones while holding', () => {
    expect(heldOrder(['a', 'b'], ['c', 'b'], true)).toEqual(['b', 'c']);
  });

  it('returns the same array when nothing changed', () => {
    const shown = ['a', 'b'];
    expect(heldOrder(shown, ['b', 'a'], true)).toBe(shown);
    expect(heldOrder(shown, ['a', 'b'], false)).toBe(shown);
  });
});
