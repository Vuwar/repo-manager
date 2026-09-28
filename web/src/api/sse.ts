import { authHeaders } from './client';
import type { EventDto } from './types';

/**
 * Incremental Server-Sent Events parser. Feed it decoded text chunks in any split;
 * it returns the `data` payload of every event completed by that chunk.
 * Handles \n, \r\n and \r line endings, comment lines (":"), multi-line data and
 * frames split across chunk boundaries.
 */
export class SseParser {
  private buffer = '';
  private data: string[] = [];
  private pendingCR = false;

  push(chunk: string): string[] {
    const out: string[] = [];
    let text = chunk;
    // A "\r" at the end of the previous chunk may be the first half of "\r\n".
    if (this.pendingCR) {
      this.pendingCR = false;
      if (text.startsWith('\n')) text = text.slice(1);
    }
    this.buffer += text;

    let start = 0;
    for (let i = 0; i < this.buffer.length; i++) {
      const c = this.buffer[i];
      if (c !== '\n' && c !== '\r') continue;
      const line = this.buffer.slice(start, i);
      if (c === '\r') {
        if (i + 1 < this.buffer.length) {
          if (this.buffer[i + 1] === '\n') i++;
        } else {
          this.pendingCR = true;
        }
      }
      start = i + 1;
      this.line(line, out);
    }
    this.buffer = this.buffer.slice(start);
    return out;
  }

  private line(line: string, out: string[]): void {
    if (line === '') {
      if (this.data.length > 0) out.push(this.data.join('\n'));
      this.data = [];
      return;
    }
    if (line.startsWith(':')) return; // comment / keep-alive
    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);
    if (field === 'data') this.data.push(value);
    // "event", "id", "retry" are not used by the daemon.
  }
}

export function parseEvent(data: string): EventDto | null {
  try {
    const e = JSON.parse(data) as EventDto;
    return e && typeof e.type === 'string' ? e : null;
  } catch {
    return null;
  }
}

export interface EventStreamOptions {
  /** Log sources to stream ("*" = all, empty = none). */
  logSources: string[];
  onEvent: (e: EventDto) => void;
  /** Called every time the stream connects; `reconnected` is true after a drop. */
  onOpen?: (reconnected: boolean) => void;
  onDown?: () => void;
  onUnauthorized?: () => void;
}

const MIN_DELAY = 500;
const MAX_DELAY = 10_000;

/** Opens /api/events with fetch (so the token can go in a header) and keeps it open with backoff. */
export function openEventStream(opts: EventStreamOptions): () => void {
  let closed = false;
  let controller: AbortController | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let delay = MIN_DELAY;
  let dropped = false;

  const url = '/api/events?logs=' + encodeURIComponent(opts.logSources.join(','));

  const schedule = () => {
    if (closed) return;
    timer = setTimeout(connect, delay);
    delay = Math.min(MAX_DELAY, delay * 2);
  };

  const connect = async () => {
    if (closed) return;
    controller = new AbortController();
    try {
      const res = await fetch(url, { headers: { ...authHeaders(), Accept: 'text/event-stream' }, signal: controller.signal });
      if (res.status === 401) {
        opts.onUnauthorized?.();
        return; // do not retry with a bad token
      }
      if (!res.ok || !res.body) throw new Error(`events: HTTP ${res.status}`);
      delay = MIN_DELAY;
      opts.onOpen?.(dropped);
      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      const parser = new SseParser();
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        for (const data of parser.push(decoder.decode(value, { stream: true }))) {
          const e = parseEvent(data);
          if (e) opts.onEvent(e);
        }
      }
      throw new Error('events: stream ended');
    } catch {
      if (closed) return;
      dropped = true;
      opts.onDown?.();
      schedule();
    }
  };

  void connect();

  return () => {
    closed = true;
    if (timer) clearTimeout(timer);
    controller?.abort();
  };
}
