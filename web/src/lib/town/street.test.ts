import { describe, expect, it } from 'vitest';
import { agent } from '../../test/fixtures';
import { needsYouCount, windowTitle } from './street';

describe('needs-you count and title', () => {
  it('counts waiting sessions only', () => {
    const list = [
      agent('a', { state: 'waiting' }),
      agent('b', { state: 'working' }),
      agent('c', { state: 'done' }),
      agent('d', { state: 'failed' }),
      agent('e', { state: 'waiting' }),
    ];
    expect(needsYouCount(Object.fromEntries(list.map((a) => [a.id, a])))).toBe(2);
  });

  it('puts the count in front of the app name', () => {
    expect(windowTitle(0)).toBe('RepoManager');
    expect(windowTitle(3)).toBe('(3) RepoManager');
  });
});
