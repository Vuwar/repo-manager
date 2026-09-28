/**
 * Minimal ANSI SGR parser: turns "\x1b[31mred\x1b[0m" into styled segments.
 * Supports 30-37/90-97 fg, 40-47/100-107 bg, 39/49 defaults, bold/dim/italic/underline/inverse
 * (and their resets), 256-colour (38;5;n / 48;5;n) and truecolour (38;2;r;g;b).
 * Every other escape sequence (cursor movement, erase, OSC titles/hyperlinks) is stripped.
 */

export interface AnsiStyle {
  /** Palette index 0-15 as "p<n>", or a CSS colour string for 256/truecolour. */
  fg?: string;
  bg?: string;
  bold?: boolean;
  dim?: boolean;
  italic?: boolean;
  underline?: boolean;
  inverse?: boolean;
}

export interface AnsiSegment extends AnsiStyle {
  text: string;
}

// CSI: ESC [ params intermediates final.  OSC: ESC ] ... (BEL | ESC \).  Other: ESC + one char.
// eslint-disable-next-line no-control-regex
const ESCAPE = /\x1b\[([0-9;:?<>=]*)([ -/]*)([@-~])|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)?|\x1b[@-Z\\-_]|\x9b[0-9;]*[@-~]/g;

function color256(n: number): string | undefined {
  if (!Number.isFinite(n) || n < 0 || n > 255) return undefined;
  if (n < 16) return 'p' + n;
  if (n < 232) {
    const i = n - 16;
    const steps = [0, 95, 135, 175, 215, 255];
    const r = steps[Math.floor(i / 36)];
    const g = steps[Math.floor(i / 6) % 6];
    const b = steps[i % 6];
    return `rgb(${r},${g},${b})`;
  }
  const v = 8 + (n - 232) * 10;
  return `rgb(${v},${v},${v})`;
}

function applySgr(params: string, style: AnsiStyle): AnsiStyle {
  const codes = params === '' ? [0] : params.split(/[;:]/).map((p) => (p === '' ? 0 : Number(p)));
  let s: AnsiStyle = { ...style };
  for (let i = 0; i < codes.length; i++) {
    const c = codes[i];
    if (c === 0) s = {};
    else if (c === 1) s.bold = true;
    else if (c === 2) s.dim = true;
    else if (c === 3) s.italic = true;
    else if (c === 4) s.underline = true;
    else if (c === 7) s.inverse = true;
    else if (c === 22) {
      s.bold = undefined;
      s.dim = undefined;
    } else if (c === 23) s.italic = undefined;
    else if (c === 24) s.underline = undefined;
    else if (c === 27) s.inverse = undefined;
    else if (c >= 30 && c <= 37) s.fg = 'p' + (c - 30);
    else if (c >= 90 && c <= 97) s.fg = 'p' + (c - 90 + 8);
    else if (c === 39) s.fg = undefined;
    else if (c >= 40 && c <= 47) s.bg = 'p' + (c - 40);
    else if (c >= 100 && c <= 107) s.bg = 'p' + (c - 100 + 8);
    else if (c === 49) s.bg = undefined;
    else if (c === 38 || c === 48) {
      const mode = codes[i + 1];
      let col: string | undefined;
      if (mode === 5) {
        col = color256(codes[i + 2]);
        i += 2;
      } else if (mode === 2) {
        const [r, g, b] = [codes[i + 2], codes[i + 3], codes[i + 4]].map((v) => Math.max(0, Math.min(255, v || 0)));
        col = `rgb(${r},${g},${b})`;
        i += 4;
      } else {
        continue;
      }
      if (c === 38) s.fg = col;
      else s.bg = col;
    }
    // unknown codes ignored
  }
  // drop undefined keys so segments stay compact and comparable
  for (const k of Object.keys(s) as (keyof AnsiStyle)[]) if (s[k] === undefined) delete s[k];
  return s;
}

/** Parses one log line. `initial` lets callers carry style across lines if they want to. */
export function parseAnsi(input: string, initial: AnsiStyle = {}): AnsiSegment[] {
  const out: AnsiSegment[] = [];
  let style: AnsiStyle = initial;
  let last = 0;
  const push = (text: string) => {
    if (!text) return;
    const prev = out[out.length - 1];
    if (prev && sameStyle(prev, style)) prev.text += text;
    else out.push({ ...style, text });
  };
  ESCAPE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = ESCAPE.exec(input))) {
    push(clean(input.slice(last, m.index)));
    last = ESCAPE.lastIndex;
    if (m[0].startsWith('\x1b[') && m[3] === 'm' && !m[2]) style = applySgr(m[1], style);
  }
  push(clean(input.slice(last)));
  return out;
}

/** Removes every escape sequence. */
export function stripAnsi(input: string): string {
  return clean(input.replace(ESCAPE, ''));
}

function clean(s: string): string {
  // Carriage-return progress output: keep what would be visible last.
  if (s.includes('\r')) {
    const parts = s.split('\r').filter((p) => p !== '');
    s = parts.length ? parts[parts.length - 1] : '';
  }
  // eslint-disable-next-line no-control-regex
  return s.replace(/[\x00-\x08\x0b-\x1f\x7f]/g, '');
}

function sameStyle(a: AnsiStyle, b: AnsiStyle): boolean {
  return (
    a.fg === b.fg &&
    a.bg === b.bg &&
    !!a.bold === !!b.bold &&
    !!a.dim === !!b.dim &&
    !!a.italic === !!b.italic &&
    !!a.underline === !!b.underline &&
    !!a.inverse === !!b.inverse
  );
}

/** Resolves a segment colour ("p3" or "rgb(...)") to a CSS value. */
export function cssColor(c: string | undefined): string | undefined {
  if (!c) return undefined;
  return c.startsWith('p') ? `var(--ansi-${c.slice(1)})` : c;
}

/** Inline style for a segment. Returns undefined when unstyled (keeps the DOM light). */
export function segmentStyle(seg: AnsiStyle): Record<string, string> | undefined {
  if (!seg.fg && !seg.bg && !seg.bold && !seg.dim && !seg.italic && !seg.underline && !seg.inverse) return undefined;
  let fg = cssColor(seg.fg);
  let bg = cssColor(seg.bg);
  if (seg.inverse) {
    const f = fg ?? 'var(--log-fg)';
    fg = bg ?? 'var(--log-bg)';
    bg = f;
  }
  const st: Record<string, string> = {};
  if (fg) st.color = fg;
  if (bg) st.backgroundColor = bg;
  if (seg.bold) st.fontWeight = '600';
  if (seg.dim) st.opacity = '0.65';
  if (seg.italic) st.fontStyle = 'italic';
  if (seg.underline) st.textDecoration = 'underline';
  return st;
}
