import { describe, expect, it } from 'vitest';
import { playReward, REWARD_NOTES } from './reward';

/** A recording stand-in for the bits of AudioContext the reward uses. */
function fakeAudio() {
  const notes: { freq: number; type: string; start: number; stop: number }[] = [];
  const ctx = {
    currentTime: 10,
    destination: {},
    createOscillator() {
      const o = {
        type: '',
        frequency: { value: 0 },
        connect: () => {},
        start: (t: number) => notes.push({ freq: o.frequency.value, type: o.type, start: t, stop: 0 }),
        stop: (t: number) => (notes[notes.length - 1].stop = t),
      };
      return o;
    },
    createGain() {
      return { gain: { value: 0, setValueAtTime: () => {}, linearRampToValueAtTime: () => {} }, connect: () => {} };
    },
  };
  return { ctx: ctx as unknown as AudioContext, notes };
}

describe('playReward', () => {
  it('plays three rising square notes, 70 ms each', () => {
    const { ctx, notes } = fakeAudio();
    playReward(ctx);
    expect(notes.map((n) => n.freq)).toEqual([523, 659, 784]);
    expect(REWARD_NOTES).toEqual([523, 659, 784]);
    expect(notes.every((n) => n.type === 'square')).toBe(true);
    expect(notes[1].start).toBeCloseTo(10.07, 5);
    expect(notes[2].stop).toBeCloseTo(10.21, 5);
  });
});
