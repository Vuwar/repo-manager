import { describe, expect, it } from 'vitest';
import { line, sampleProjects, svc } from '../test/fixtures';
import { initialState, MAX_LOG_LINES, reducer, type AppState } from './reducer';

function loaded(): AppState {
  return reducer(initialState(), { type: 'projectsLoaded', projects: sampleProjects() });
}

describe('reducer', () => {
  it('selects the first instance and a default log source on first load', () => {
    const s = loaded();
    expect(s.view).toEqual({ kind: 'instance', key: 'panel-pro' });
    expect(s.logSources).toEqual(['panel-pro/api']);
  });

  it('keeps a persisted instance selection if it still exists', () => {
    const s = reducer(initialState('other'), { type: 'projectsLoaded', projects: sampleProjects() });
    expect(s.view).toEqual({ kind: 'instance', key: 'other' });
    expect(s.logSources).toEqual(['other/site']);
  });

  it('applies a status event by replacing only that service', () => {
    const s = loaded();
    const before = s.projects!;
    const next = reducer(s, { type: 'status', service: svc('panel-pro', 'web', { state: 'running', port: 5173 }) });
    const web = next.projects![0].instances[0].services.find((x) => x.name === 'web')!;
    expect(web.state).toBe('running');
    expect(web.lastError).toBeUndefined();
    // untouched project / instance keep their identity (cheap re-renders)
    expect(next.projects![1]).toBe(before[1]);
    expect(next.projects![0].instances[1]).toBe(before[0].instances[1]);
  });

  it('ignores status events for unknown services', () => {
    const s = loaded();
    expect(reducer(s, { type: 'status', service: svc('nope', 'x') })).toBe(s);
  });

  it('applies task events', () => {
    const s = loaded();
    const next = reducer(s, { type: 'task', task: { id: 'panel-pro/test', name: 'test', command: 'dotnet test', running: true } });
    expect(next.projects![0].instances[0].tasks[0].running).toBe(true);
  });

  it('appends log lines only for watched sources and caps the buffer', () => {
    let s = loaded();
    s = reducer(s, {
      type: 'logLines',
      lines: [line('panel-pro/api', 'a', '2026-09-28T10:00:00Z'), line('panel-pro/web', 'ignored', '2026-09-28T10:00:00Z')],
    });
    expect(s.logs['panel-pro/api'].map((l) => l.text)).toEqual(['a']);
    expect(s.logs['panel-pro/web']).toBeUndefined();

    const many = Array.from({ length: MAX_LOG_LINES + 10 }, (_, i) => line('panel-pro/api', 'l' + i, '2026-09-28T10:00:01Z'));
    s = reducer(s, { type: 'logLines', lines: many });
    expect(s.logs['panel-pro/api']).toHaveLength(MAX_LOG_LINES);
    expect(s.logs['panel-pro/api'].at(-1)!.text).toBe('l' + (MAX_LOG_LINES + 9));
  });

  it('merges history with live lines that arrived while loading', () => {
    let s = loaded();
    s = reducer(s, {
      type: 'logLines',
      lines: [line('panel-pro/api', 'old-live', '2026-09-28T10:00:01Z'), line('panel-pro/api', 'new-live', '2026-09-28T10:00:05.1234567+00:00')],
    });
    s = reducer(s, {
      type: 'logHistory',
      source: 'panel-pro/api',
      lines: [line('panel-pro/api', 'h1', '2026-09-28T10:00:00Z'), line('panel-pro/api', 'old-live', '2026-09-28T10:00:01Z')],
    });
    expect(s.logs['panel-pro/api'].map((l) => l.text)).toEqual(['h1', 'old-live', 'new-live']);
  });

  it('drops buffers of deselected sources and clears on demand', () => {
    let s = loaded();
    s = reducer(s, { type: 'setLogSources', sources: ['panel-pro/api', 'panel-pro/web'] });
    s = reducer(s, {
      type: 'logLines',
      lines: [line('panel-pro/web', 'w', '2026-09-28T10:00:00Z'), line('panel-pro/api', 'a', '2026-09-28T10:00:00Z')],
    });
    s = reducer(s, { type: 'setLogSources', sources: ['panel-pro/api'] });
    expect(Object.keys(s.logs)).toEqual(['panel-pro/api']);
    s = reducer(s, { type: 'clearLogs', sources: ['panel-pro/api'] });
    expect(s.logs['panel-pro/api']).toEqual([]);
  });

  it('switching instance resets log sources to that instance', () => {
    let s = loaded();
    s = reducer(s, { type: 'selectInstance', key: 'panel-pro@feat-x' });
    expect(s.view).toEqual({ kind: 'instance', key: 'panel-pro@feat-x' });
    expect(s.logSources).toEqual(['panel-pro@feat-x/api']);
  });

  it('falls back to the first instance when the selected one disappears', () => {
    let s = loaded();
    s = reducer(s, { type: 'selectInstance', key: 'other' });
    s = reducer(s, { type: 'projectsLoaded', projects: sampleProjects().slice(0, 1) });
    expect(s.view).toEqual({ kind: 'instance', key: 'panel-pro' });
  });
});
