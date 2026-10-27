// Compact view background: frosted glass (Windows acrylic), colors matched to the screen around
// the window, or both. The desktop shell does the native part and reports back what it applied.

export type AmbientMode = 'glass' | 'match' | 'mixed';
export type Rgb = [number, number, number];

/** What the shell applied: whether the acrylic backdrop is on, and the color sampled around the window. */
export interface AmbientState {
  glass: boolean;
  rgb: Rgb | null;
}

export const AMBIENT_MODES: AmbientMode[] = ['glass', 'match', 'mixed'];

export const AMBIENT_LABELS: Record<AmbientMode, string> = {
  glass: 'Glass',
  match: 'Match',
  mixed: 'Mixed',
};

export const nextAmbient = (m: AmbientMode): AmbientMode => AMBIENT_MODES[(AMBIENT_MODES.indexOf(m) + 1) % AMBIENT_MODES.length];

export const isAmbientMode = (v: unknown): v is AmbientMode => AMBIENT_MODES.includes(v as AmbientMode);

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

function mix(a: Rgb, b: Rgb, t: number): Rgb {
  return [0, 1, 2].map((i) => Math.round(a[i] + (b[i] - a[i]) * t)) as Rgb;
}

const hex = (c: Rgb) => '#' + c.map((v) => clamp(Math.round(v), 0, 255).toString(16).padStart(2, '0')).join('');
const rgba = (c: Rgb, a: number) => `rgba(${c[0]}, ${c[1]}, ${c[2]}, ${a})`;

/** WCAG relative luminance, 0 (black) to 1 (white). */
export function luminance([r, g, b]: Rgb): number {
  const lin = (v: number) => {
    const s = v / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

function toHsl([r, g, b]: Rgb): [number, number, number] {
  const [rn, gn, bn] = [r / 255, g / 255, b / 255];
  const max = Math.max(rn, gn, bn);
  const min = Math.min(rn, gn, bn);
  const l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h = max === rn ? (gn - bn) / d + (gn < bn ? 6 : 0) : max === gn ? (bn - rn) / d + 2 : (rn - gn) / d + 4;
  h *= 60;
  return [h, s, l];
}

function fromHsl(h: number, s: number, l: number): Rgb {
  const k = (n: number) => (n + h / 30) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return [f(0), f(8), f(4)].map((v) => Math.round(v * 255)) as Rgb;
}

/** True when a dark surface needs light text. */
export const isDarkColor = (c: Rgb) => luminance(c) < 0.18;

/**
 * Accent taken from the sampled color's hue, so buttons and links pick up the surroundings.
 * Grays have no hue to borrow: the app's own blue stays.
 */
export function accentVars(rgb: Rgb, darkSurface: boolean): Record<string, string> {
  const [h, s] = toHsl(rgb);
  if (s < 0.18) return {};
  const sat = clamp(s, 0.55, 0.85);
  const accent = fromHsl(h, sat, darkSurface ? 0.58 : 0.46);
  const hover = fromHsl(h, sat, darkSurface ? 0.66 : 0.4);
  const text = fromHsl(h, sat, darkSurface ? 0.74 : 0.36);
  return {
    '--accent': hex(accent),
    '--accent-hover': hex(hover),
    '--accent-text': hex(text),
    '--accent-soft': rgba(accent, 0.16),
    '--accent-fg': luminance(accent) > 0.4 ? '#111317' : '#ffffff',
  };
}

const DARK_STATES = { ok: [63, 185, 80], warn: [215, 160, 43], bad: [242, 85, 90], busy: [88, 166, 255], off: [93, 100, 114] } as const;
const LIGHT_STATES = { ok: [31, 157, 58], warn: [178, 122, 6], bad: [216, 52, 59], busy: [47, 127, 224], off: [154, 161, 173] } as const;

/** A full solid palette built around the sampled color: the window looks cut from its background. */
export function matchVars(rgb: Rgb): Record<string, string> {
  const dark = isDarkColor(rgb);
  const fg: Rgb = dark ? [255, 255, 255] : [0, 0, 0];
  const states = dark ? DARK_STATES : LIGHT_STATES;
  const vars: Record<string, string> = {
    colorScheme: dark ? 'dark' : 'light',
    '--bg': hex(rgb),
    '--bg-sidebar': hex(mix(rgb, [0, 0, 0], dark ? 0.16 : 0.05)),
    '--bg-elev': hex(mix(rgb, fg, 0.05)),
    '--bg-elev-2': hex(mix(rgb, fg, 0.08)),
    '--bg-input': hex(dark ? mix(rgb, [0, 0, 0], 0.22) : mix(rgb, [255, 255, 255], 0.6)),
    '--bg-hover': rgba(fg, 0.06),
    '--border': hex(mix(rgb, fg, 0.13)),
    '--border-soft': hex(mix(rgb, fg, 0.08)),
    '--border-strong': hex(mix(rgb, fg, 0.22)),
    '--text': hex(mix(fg, rgb, 0.08)),
    '--text-2': hex(mix(fg, rgb, 0.32)),
    '--text-3': hex(mix(fg, rgb, 0.5)),
  };
  for (const [name, c] of Object.entries(states)) {
    vars[`--${name}`] = hex(c as unknown as Rgb);
    vars[`--${name}-soft`] = rgba(c as unknown as Rgb, 0.13);
  }
  return { ...vars, ...accentVars(rgb, dark) };
}

/**
 * Class and inline style for the compact view's root.
 * - glass: translucent surfaces over the acrylic backdrop (the `is-glass` class does the work).
 * - match: solid palette from the sampled color.
 * - mixed: glass, tinted with the sampled color, accent borrowed from it.
 * Without the shell (or before it answers) the view keeps its normal look.
 * When acrylic is unavailable, glass falls back to the normal look and mixed to match.
 */
export function ambientStyle(mode: AmbientMode, state: AmbientState, darkTheme: boolean): { className: string; style: Record<string, string> } {
  const { glass, rgb } = state;
  if (glass && (mode === 'glass' || mode === 'mixed')) {
    if (mode === 'glass' || !rgb) return { className: 'is-glass', style: {} };
    // Light colors over a dark theme (and the reverse) get a lighter wash so text stays readable.
    const alpha = isDarkColor(rgb) === darkTheme ? 0.34 : 0.1;
    return { className: 'is-glass', style: { '--ambient': rgba(rgb, alpha), ...accentVars(rgb, darkTheme) } };
  }
  if ((mode === 'match' || mode === 'mixed') && rgb) return { className: 'is-matched', style: matchVars(rgb) };
  return { className: '', style: {} };
}
