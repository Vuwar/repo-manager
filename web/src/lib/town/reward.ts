/** The quest reward: three rising square-wave notes (Hz), 70 ms each, quiet. */
export const REWARD_NOTES = [523, 659, 784];
const NOTE_S = 0.07;
const GAIN = 0.08;

export function playReward(ctx: AudioContext): void {
  REWARD_NOTES.forEach((freq, i) => {
    const start = ctx.currentTime + i * NOTE_S;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'square';
    osc.frequency.value = freq;
    gain.gain.setValueAtTime(GAIN, start);
    gain.gain.linearRampToValueAtTime(0, start + NOTE_S);
    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.start(start);
    osc.stop(start + NOTE_S);
  });
}

let shared: AudioContext | null = null;

/** Plays the reward on one shared AudioContext, created on first use. Never throws (no audio is fine). */
export function reward(): void {
  try {
    const Ctor = (window as unknown as { AudioContext?: new () => AudioContext }).AudioContext;
    if (!Ctor) return;
    shared ??= new Ctor();
    // Chromium may start a context suspended until the page has had a user gesture.
    if (shared.state === 'suspended') void shared.resume();
    playReward(shared);
  } catch {
    /* no audio device: the pop-up is enough */
  }
}

/** For tests: forget the shared context. */
export function resetRewardAudio(): void {
  shared = null;
}
