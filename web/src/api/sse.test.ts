import { describe, expect, it } from 'vitest';
import { parseEvent, SseParser } from './sse';

describe('SseParser', () => {
  it('parses complete frames and ignores comments', () => {
    const p = new SseParser();
    expect(p.push(': connected\n\ndata: {"type":"projects"}\n\n')).toEqual(['{"type":"projects"}']);
  });

  it('reassembles frames split across chunk boundaries', () => {
    const p = new SseParser();
    const frame = 'data: {"type":"log","payload":{"text":"hello"}}\n\n';
    const out: string[] = [];
    for (const ch of frame) out.push(...p.push(ch));
    expect(out).toEqual(['{"type":"log","payload":{"text":"hello"}}']);
  });

  it('handles splits in the middle of "data:" and between the two newlines', () => {
    const p = new SseParser();
    expect(p.push('da')).toEqual([]);
    expect(p.push('ta: {"a":1}\n')).toEqual([]);
    expect(p.push('\ndata: {"b":2}\n\nda')).toEqual(['{"a":1}', '{"b":2}']);
    expect(p.push('ta: {"c":3}\n\n')).toEqual(['{"c":3}']);
  });

  it('supports CRLF, including a CR/LF pair split across chunks', () => {
    const p = new SseParser();
    expect(p.push('data: x\r')).toEqual([]);
    expect(p.push('\n\r')).toEqual(['x']);
    expect(p.push('\ndata: y\r\n\r\n')).toEqual(['y']);
  });

  it('joins multi-line data with newlines', () => {
    const p = new SseParser();
    expect(p.push('data: one\ndata: two\n\n')).toEqual(['one\ntwo']);
  });

  it('parseEvent rejects garbage', () => {
    expect(parseEvent('not json')).toBeNull();
    expect(parseEvent('{"type":"status","payload":{"id":"a/b"}}')?.type).toBe('status');
  });
});
