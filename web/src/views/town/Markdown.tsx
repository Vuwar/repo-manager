import type { ReactNode } from 'react';

// A small Markdown subset for Claude's replies in the dialogue box: headings, lists, fenced code, paragraphs,
// **bold**, *italic*, `code` and [links](url). Everything becomes React text nodes, never HTML, so a reply can
// not inject markup. Links show their text (URL in the tooltip): the town window must not navigate away.

const INLINE =
  /`([^`\n]+)`|\*\*(.+?)\*\*|__(.+?)__|\*([^*\s](?:[^*\n]*?[^*\s])?)\*|(?<![A-Za-z0-9])_([^_\s](?:[^_\n]*?[^_\s])?)_(?![A-Za-z0-9])|\[([^\]\n]+)\]\(([^)\s]+)\)/g;

function inline(text: string, key = 'i'): ReactNode[] {
  const out: ReactNode[] = [];
  let at = 0;
  let n = 0;
  for (const m of text.matchAll(INLINE)) {
    if (m.index > at) out.push(text.slice(at, m.index));
    const k = `${key}-${n++}`;
    const [, code, bold1, bold2, em1, em2, linkText, url] = m;
    if (code !== undefined) out.push(<code key={k}>{code}</code>);
    else if (bold1 !== undefined || bold2 !== undefined) out.push(<strong key={k}>{inline(bold1 ?? bold2, k)}</strong>);
    else if (em1 !== undefined || em2 !== undefined) out.push(<em key={k}>{inline(em1 ?? em2, k)}</em>);
    else
      out.push(
        <span key={k} className="md-link" title={url}>
          {inline(linkText, k)}
        </span>,
      );
    at = m.index + m[0].length;
  }
  if (at < text.length) out.push(text.slice(at));
  return out;
}

const HEADING = /^(#{1,6})\s+(.*)$/;
const BULLET = /^\s*[-*+]\s+(.*)$/;
const NUMBERED = /^\s*\d+[.)]\s+(.*)$/;
const FENCE = /^\s*```/;

export function Markdown({ text }: { text: string }) {
  const lines = text.split('\n');
  const blocks: ReactNode[] = [];
  let para: string[] = [];
  const flush = () => {
    if (para.length) blocks.push(<p key={blocks.length}>{inline(para.join('\n'), 'p' + blocks.length)}</p>);
    para = [];
  };
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (FENCE.test(line)) {
      flush();
      const code: string[] = [];
      while (++i < lines.length && !FENCE.test(lines[i])) code.push(lines[i]);
      blocks.push(<pre key={blocks.length}>{code.join('\n')}</pre>);
      continue;
    }
    const h = HEADING.exec(line);
    const listRe = BULLET.test(line) ? BULLET : NUMBERED.test(line) ? NUMBERED : null;
    if (h) {
      flush();
      blocks.push(
        <div key={blocks.length} className={`md-h md-h${h[1].length}`}>
          {inline(h[2], 'h' + blocks.length)}
        </div>,
      );
    } else if (listRe) {
      flush();
      const items: string[] = [];
      for (; i < lines.length && listRe.test(lines[i]); i++) items.push(listRe.exec(lines[i])![1]);
      i--;
      const lis = items.map((t, k) => <li key={k}>{inline(t, `l${blocks.length}-${k}`)}</li>);
      blocks.push(listRe === BULLET ? <ul key={blocks.length}>{lis}</ul> : <ol key={blocks.length}>{lis}</ol>);
    } else if (!line.trim()) flush();
    else para.push(line);
  }
  flush();
  return <div className="md">{blocks}</div>;
}
