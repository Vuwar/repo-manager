import { memo, type ReactNode } from 'react';
import { parseAnsi, segmentStyle, type AnsiSegment } from '../lib/ansi';

const cache = new Map<string, AnsiSegment[]>();

/** Parsed segments, memoised by raw text (log lines repeat a lot). */
export function segmentsOf(text: string): AnsiSegment[] {
  let s = cache.get(text);
  if (!s) {
    s = parseAnsi(text);
    if (cache.size > 20000) cache.clear();
    cache.set(text, s);
  }
  return s;
}

function highlight(text: string, needle: string, keyBase: string): ReactNode {
  if (!needle) return text;
  const lower = text.toLowerCase();
  const parts: ReactNode[] = [];
  let i = 0;
  let n = 0;
  for (;;) {
    const j = lower.indexOf(needle, i);
    if (j === -1) break;
    if (j > i) parts.push(text.slice(i, j));
    parts.push(
      <mark key={keyBase + ':' + n++} className="hl">
        {text.slice(j, j + needle.length)}
      </mark>,
    );
    i = j + needle.length;
  }
  if (!parts.length) return text;
  if (i < text.length) parts.push(text.slice(i));
  return parts;
}

/** Renders ANSI-coloured text; `needle` (lower-case) is highlighted. */
export const AnsiText = memo(function AnsiText({ text, needle = '' }: { text: string; needle?: string }) {
  const segs = segmentsOf(text);
  return (
    <>
      {segs.map((seg, i) => {
        const style = segmentStyle(seg);
        const content = highlight(seg.text, needle, String(i));
        return style ? (
          <span key={i} style={style}>
            {content}
          </span>
        ) : (
          <span key={i}>{content}</span>
        );
      })}
    </>
  );
});
