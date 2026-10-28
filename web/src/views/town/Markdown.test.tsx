import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Markdown } from './Markdown';

const html = (text: string) => render(<Markdown text={text} />).container;

describe('Markdown', () => {
  it('renders bold, italic and inline code', () => {
    const c = html('Fixed **the bug** in *one* place: `npm test` passes, __really__ _yes_');
    expect(c.querySelector('strong')?.textContent).toBe('the bug');
    expect([...c.querySelectorAll('em')].map((e) => e.textContent)).toEqual(['one', 'yes']);
    expect(c.querySelectorAll('strong')[1]?.textContent).toBe('really');
    expect(c.querySelector('code')?.textContent).toBe('npm test');
  });

  it('keeps markdown inside inline code literal', () => {
    const c = html('Run `a **b** c`');
    expect(c.querySelector('code')?.textContent).toBe('a **b** c');
    expect(c.querySelector('strong')).toBeNull();
  });

  it('renders headings, bullet and numbered lists', () => {
    const c = html('## Summary\n\n- one\n- **two**\n\n1. first\n2. second');
    expect(c.querySelector('.md-h')?.textContent).toBe('Summary');
    expect([...c.querySelectorAll('ul > li')].map((e) => e.textContent)).toEqual(['one', 'two']);
    expect([...c.querySelectorAll('ol > li')].map((e) => e.textContent)).toEqual(['first', 'second']);
  });

  it('renders fenced code blocks verbatim', () => {
    const c = html('Look:\n```ts\nconst a = **1**;\n  indented\n```\nDone');
    expect(c.querySelector('pre')?.textContent).toBe('const a = **1**;\n  indented');
    expect(c.textContent).toContain('Done');
  });

  it('shows links as their text, never as HTML', () => {
    const c = html('See [the docs](https://example.com) and <b>raw</b>');
    expect(c.querySelector('a')).toBeNull();
    expect(c.querySelector('b')).toBeNull();
    expect(c.textContent).toBe('See the docs and <b>raw</b>');
    expect(c.querySelector('[title="https://example.com"]')?.textContent).toBe('the docs');
  });

  it('makes links to other sessions clickable', () => {
    const seen: string[] = [];
    const c = render(<Markdown text="Ask [AI chatbox](#local_4bbbecd2-8f88) or [docs](https://x.y)" onSessionLink={(id) => seen.push(id)} />).container;
    const buttons = c.querySelectorAll('button.md-session');
    expect(buttons).toHaveLength(1);
    expect(buttons[0].textContent).toBe('AI chatbox');
    (buttons[0] as HTMLButtonElement).click();
    expect(seen).toEqual(['local_4bbbecd2-8f88']);
  });

  it('leaves an unclosed marker as plain text (half-typed text)', () => {
    const c = html('Fixed **the b');
    expect(c.querySelector('strong')).toBeNull();
    expect(c.textContent).toBe('Fixed **the b');
  });

  it('keeps plain text with its line breaks', () => {
    const c = html('line one\nline two');
    expect(c.textContent).toBe('line one\nline two');
  });
});
